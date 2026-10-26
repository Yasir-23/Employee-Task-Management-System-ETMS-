using System.Security.Cryptography;
using ETMS.Api.Common.Exceptions;
using ETMS.Api.Data;
using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Models.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ETMS.Api.Services.Webhooks;

public class WebhookSubscriptionService : IWebhookSubscriptionService
{
    /// <summary>Data Protection purpose for subscription secrets (SDD §3.9).</summary>
    public const string SecretProtectionPurpose = "ETMS.Webhooks.Secret";
    public const string SecretPrefix = "whsec_etms_";
    private const int SecretByteLength = 32;

    private readonly AppDbContext _db;
    private readonly IDataProtector _secretProtector;
    private readonly ILogger<WebhookSubscriptionService> _logger;

    public WebhookSubscriptionService(AppDbContext db, IDataProtectionProvider dataProtection, ILogger<WebhookSubscriptionService> logger)
    {
        _db = db;
        _secretProtector = dataProtection.CreateProtector(SecretProtectionPurpose);
        _logger = logger;
    }

    public async Task<WebhookSubscriptionCreatedDto> CreateAsync(CreateWebhookSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        var url = request.Url.Trim();
        if (url.Length > 2048)
        {
            throw new BadRequestException("Url must be at most 2048 characters.");
        }

        if (SsrfGuard.ValidateUrl(url) is { } urlError)
        {
            throw new BadRequestException(urlError);
        }

        var eventType = request.EventType.Trim();
        if (!WebhookEventTypes.Subscribable.Contains(eventType))
        {
            throw new BadRequestException(
                $"EventType must be one of: {string.Join(", ", WebhookEventTypes.Subscribable)}.");
        }

        var duplicate = await _db.WebhookSubscriptions
            .AnyAsync(s => s.IsActive && s.Url == url && s.EventType == eventType, cancellationToken);
        if (duplicate)
        {
            throw new ConflictException("An active subscription with the same Url and EventType already exists.");
        }

        var secret = SecretPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(SecretByteLength));
        var subscription = new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Url = url,
            SecretKey = _secretProtector.Protect(secret),
            EventType = eventType,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.WebhookSubscriptions.Add(subscription);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Registered webhook subscription {SubscriptionId} for {EventType}.", subscription.Id, eventType);

        return new WebhookSubscriptionCreatedDto
        {
            Id = subscription.Id,
            Url = subscription.Url,
            EventType = subscription.EventType,
            IsActive = subscription.IsActive,
            SecretKey = secret,
            CreatedAtUtc = subscription.CreatedAtUtc
        };
    }

    public async Task<PagedResult<WebhookSubscriptionDto>> GetSubscriptionsAsync(WebhookSubscriptionFilterParams filter, CancellationToken cancellationToken = default)
    {
        var query = _db.WebhookSubscriptions.AsNoTracking();
        if (filter.IsActive is { } isActive)
        {
            query = query.Where(s => s.IsActive == isActive);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(s => s.CreatedAtUtc)
            .ThenBy(s => s.Id)
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(s => new
            {
                s.Id,
                s.Url,
                s.EventType,
                s.IsActive,
                s.SecretKey,
                s.CreatedAtUtc,
                LastDelivery = s.Deliveries
                    .Where(d => d.AttemptCount > 0)
                    .OrderByDescending(d => d.TimestampUtc)
                    .Select(d => new WebhookDeliverySummaryDto
                    {
                        IsSuccess = d.IsSuccess,
                        StatusCode = d.StatusCode,
                        AttemptCount = d.AttemptCount,
                        TimestampUtc = d.TimestampUtc
                    })
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<WebhookSubscriptionDto>
        {
            Items = rows.Select(s => new WebhookSubscriptionDto
            {
                Id = s.Id,
                Url = s.Url,
                EventType = s.EventType,
                IsActive = s.IsActive,
                SecretKeyHint = MaskSecret(s.SecretKey),
                CreatedAtUtc = s.CreatedAtUtc,
                LastDelivery = s.LastDelivery
            }).ToList(),
            PageNumber = filter.PageNumber,
            PageSize = filter.PageSize,
            TotalCount = totalCount
        };
    }

    public async Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var subscription = await _db.WebhookSubscriptions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new NotFoundException($"Webhook subscription '{id}' was not found.");

        if (!subscription.IsActive)
        {
            return;
        }

        subscription.IsActive = false;
        subscription.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Deactivated webhook subscription {SubscriptionId}.", id);
    }

    private string MaskSecret(string protectedSecret)
    {
        try
        {
            var secret = _secretProtector.Unprotect(protectedSecret);
            return "••••" + secret[^4..];
        }
        catch (CryptographicException)
        {
            // The key ring that protected this secret is gone; deliveries for it will fail too.
            return "••••";
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
