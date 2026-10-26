using System.Security.Cryptography;
using System.Text;
using ETMS.Api.Common.Extensions;
using ETMS.Api.Common.Settings;
using ETMS.Api.Data;
using ETMS.Api.Models.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;

namespace ETMS.Api.Services.Webhooks;

/// <summary>
/// SDD §10.3 outbox relay. Due rows are claimed by pushing their NextRetryUtc forward (a lease) inside a short
/// FOR UPDATE SKIP LOCKED transaction, so several API replicas never send the same row concurrently and no
/// database transaction stays open while receivers are being called.
/// </summary>
public class WebhookDeliveryService : IWebhookDeliveryService
{
    public const string HttpClientName = "OutboundWebhooks";

    /// <summary>How long a claimed row is hidden from other workers; comfortably longer than one batch.</summary>
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(15);

    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDataProtector _secretProtector;
    private readonly WebhookSettings _settings;
    private readonly ILogger<WebhookDeliveryService> _logger;

    public WebhookDeliveryService(
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        IDataProtectionProvider dataProtection,
        IOptions<WebhookSettings> settings,
        ILogger<WebhookDeliveryService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _secretProtector = dataProtection.CreateProtector(WebhookSubscriptionService.SecretProtectionPurpose);
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<int> DeliverDueAsync(CancellationToken cancellationToken = default)
    {
        var deliveries = await ClaimDueAsync(cancellationToken);
        if (deliveries.Count == 0)
        {
            return 0;
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        foreach (var delivery in deliveries)
        {
            int? statusCode;
            try
            {
                statusCode = await SendAsync(client, delivery, cancellationToken);
            }
            catch (BrokenCircuitException)
            {
                // FR-RESIL-01: the receiver's circuit is open. Postpone without consuming an attempt.
                delivery.NextRetryUtc = DateTime.UtcNow + HttpResilienceExtensions.CircuitBreakDuration;
                await _db.SaveChangesAsync(cancellationToken);
                continue;
            }

            var deadLettered = WebhookRetryPolicy.RecordAttempt(delivery, statusCode, DateTime.UtcNow, _settings.MaxAttempts);
            if (deadLettered)
            {
                _logger.LogWarning(
                    "Webhook delivery {DeliveryId} ({EventType}) to subscription {SubscriptionId} dead-lettered after {Attempts} attempts; last status {StatusCode}.",
                    delivery.Id, delivery.EventType, delivery.SubscriptionId, delivery.AttemptCount, delivery.StatusCode);
            }

            // Saved per row so a crash mid-batch never repeats an attempt that was already recorded.
            await _db.SaveChangesAsync(cancellationToken);
        }

        return deliveries.Count;
    }

    private async Task<List<WebhookDeliveryLog>> ClaimDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        if (!_db.Database.IsRelational())
        {
            // Unit tests (in-memory provider): single worker, no locking needed.
            return await DueQuery(now).Take(_settings.BatchSize).ToListAsync(cancellationToken);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var ids = await _db.Database.SqlQuery<Guid>($"""
            SELECT d."Id" AS "Value"
            FROM "WebhookDeliveryLogs" d
            JOIN "WebhookSubscriptions" s ON s."Id" = d."SubscriptionId"
            WHERE d."IsSuccess" = FALSE
              AND d."AttemptCount" < {_settings.MaxAttempts}
              AND d."NextRetryUtc" <= {now}
              AND s."IsActive"
            ORDER BY d."NextRetryUtc"
            LIMIT {_settings.BatchSize}
            FOR UPDATE OF d SKIP LOCKED
            """).ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            return [];
        }

        var deliveries = await _db.WebhookDeliveryLogs
            .Include(d => d.Subscription)
            .Where(d => ids.Contains(d.Id))
            .OrderBy(d => d.NextRetryUtc)
            .ToListAsync(cancellationToken);

        foreach (var delivery in deliveries)
        {
            delivery.NextRetryUtc = now + ClaimLease;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deliveries;
    }

    // Rows of inactive subscriptions are skipped without consuming an attempt (API.md §8.4).
    private IQueryable<WebhookDeliveryLog> DueQuery(DateTime now) =>
        _db.WebhookDeliveryLogs
            .Include(d => d.Subscription)
            .Where(d => !d.IsSuccess
                && d.AttemptCount < _settings.MaxAttempts
                && d.NextRetryUtc <= now
                && d.Subscription.IsActive)
            .OrderBy(d => d.NextRetryUtc);

    /// <returns>The receiver's HTTP status, or null after a network error or timeout.</returns>
    private async Task<int?> SendAsync(HttpClient client, WebhookDeliveryLog delivery, CancellationToken cancellationToken)
    {
        string secret;
        try
        {
            secret = _secretProtector.Unprotect(delivery.Subscription.SecretKey);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Cannot decrypt the secret of webhook subscription {SubscriptionId}; is the Data Protection key ring persisted?",
                delivery.SubscriptionId);
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Subscription.Url)
        {
            Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(WebhookSigner.EventHeader, delivery.EventType);
        request.Headers.Add(WebhookSigner.DeliveryHeader, delivery.Id.ToString());
        request.Headers.Add(WebhookSigner.SignatureHeader,
            WebhookSigner.BuildHeader(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), delivery.Payload));

        // Per-attempt/total timeouts and inner retries come from the "OutboundWebhooks" Polly pipeline (SDD §12.1).
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return (int)response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is ExecutionRejectedException and not BrokenCircuitException)
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogInformation("Webhook delivery {DeliveryId} to subscription {SubscriptionId} failed: {Error}",
                delivery.Id, delivery.SubscriptionId, ex.Message);
            return null;
        }
    }
}
