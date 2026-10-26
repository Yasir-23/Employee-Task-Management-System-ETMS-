using System.Text.RegularExpressions;
using ETMS.Api.Common.Exceptions;
using ETMS.Api.Common.Extensions;
using ETMS.Api.Common.Settings;
using ETMS.Api.Data;
using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Models.Entities;
using ETMS.Api.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ETMS.Api.Services.Billing;

public partial class BillingService : IBillingService
{
    // FR-PAY-01: Stripe's smallest chargeable amount (50 cents) up to its 8-digit ceiling.
    public const long MinAmountCents = 50;
    public const long MaxAmountCents = 99_999_999;
    public const string DefaultCurrency = "usd";

    private readonly AppDbContext _db;
    private readonly IPaymentGatewayService _paymentGateway;
    private readonly StripeSettings _stripeSettings;
    private readonly ILogger<BillingService> _logger;

    public BillingService(
        AppDbContext db,
        IPaymentGatewayService paymentGateway,
        IOptions<StripeSettings> stripeSettings,
        ILogger<BillingService> logger)
    {
        _db = db;
        _paymentGateway = paymentGateway;
        _stripeSettings = stripeSettings.Value;
        _logger = logger;
    }

    public async Task<BillingUpsertResult> SetTaskBillingAsync(Guid currentUserId, string role, Guid taskId, SetBillingRequest request, CancellationToken cancellationToken = default)
    {
        var task = await LoadTaskForBillingAsync(taskId, cancellationToken);
        EnsureCanManageBilling(currentUserId, role, task);

        var amountCents = request.AmountCents ?? 0;
        if (amountCents is < MinAmountCents or > MaxAmountCents)
        {
            throw new BadRequestException(
                $"AmountCents must be between {MinAmountCents} and {MaxAmountCents:N0} (minor units, e.g. 15000 = 150.00).");
        }

        var currency = (request.Currency ?? DefaultCurrency).Trim().ToLowerInvariant();
        if (!CurrencyCodePattern().IsMatch(currency))
        {
            throw new BadRequestException("Currency must be a 3-letter ISO 4217 code, e.g. \"usd\".");
        }

        var now = DateTime.UtcNow;
        var billing = task.Billing;
        var created = billing is null;

        if (billing is null)
        {
            billing = new TaskBilling
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                Status = BillingStatus.Unpaid,
                CreatedAtUtc = now
            };
            _db.TaskBillings.Add(billing);
        }
        else if (billing.Status is BillingStatus.Processing or BillingStatus.Paid)
        {
            // FR-PAY-01: the amount is locked once a checkout is in flight or the task is paid.
            throw new ConflictException($"Billing is {billing.Status}; its amount can no longer be changed.");
        }
        else
        {
            billing.UpdatedAtUtc = now;
        }

        billing.AmountCents = amountCents;
        billing.Currency = currency;

        await SaveBillingChangesAsync(cancellationToken);
        return new BillingUpsertResult(ToDto(billing), created);
    }

    public async Task<CheckoutResult> CreateCheckoutSessionAsync(Guid currentUserId, string role, Guid taskId, CreateCheckoutRequest? request, string? idempotencyKey, CancellationToken cancellationToken = default)
    {
        // FR-PAY-04: the client key makes retries of the same request safe end-to-end.
        if (!Guid.TryParse(idempotencyKey, out var clientKey))
        {
            throw new BadRequestException("An Idempotency-Key header containing a UUID is required.");
        }

        var task = await LoadTaskForBillingAsync(taskId, cancellationToken);
        EnsureCanManageBilling(currentUserId, role, task);

        // BR-07: only billable tasks can be checked out.
        var billing = task.Billing
            ?? throw new ConflictException("BR-07: this task is not billable. Configure billing with PUT /api/tasks/{id}/billing first.");

        var successUrl = ResolveReturnUrl(request?.SuccessUrl, _stripeSettings.SuccessUrl, "SuccessUrl");
        var cancelUrl = ResolveReturnUrl(request?.CancelUrl, _stripeSettings.CancelUrl, "CancelUrl");

        switch (billing.Status)
        {
            case BillingStatus.Paid:
                // BR-07: a paid task can never be billed again.
                throw new ConflictException("BR-07: this task has already been paid and cannot be billed again.");

            case BillingStatus.Processing when billing.StripeSessionId is not null:
                var existing = await _paymentGateway.GetCheckoutSessionAsync(billing.StripeSessionId, cancellationToken);
                switch (existing.State)
                {
                    case CheckoutSessionState.Open when existing.Url is not null:
                        // FR-PAY-04: one open session per billing; repeated calls return it.
                        return new CheckoutResult(new CheckoutSessionResponseDto(existing.Url, existing.SessionId), IsNewSession: false);

                    case CheckoutSessionState.Complete:
                        throw new ConflictException("Payment for this task has been submitted and is awaiting confirmation from the payment provider.");

                    default:
                        // The previous session expired before payment: back to Unpaid, then start a new one.
                        _logger.LogInformation("Checkout session {SessionId} for billing {BillingId} expired; starting a new one.",
                            billing.StripeSessionId, billing.Id);
                        billing.Status = BillingStatus.Unpaid;
                        break;
                }

                break;
        }

        // Unpaid, Failed, or an expired Processing session: Unpaid/Failed -> Processing (FR-PAY-03).
        var session = await _paymentGateway.CreateCheckoutSessionAsync(
            billing, task, successUrl, cancelUrl, $"checkout:{billing.Id}:{clientKey:D}", cancellationToken);

        if (session.State != CheckoutSessionState.Open || session.Url is null)
        {
            // Stripe replays the original response for a reused idempotency key, which may be a stale session.
            throw new ConflictException("This Idempotency-Key was already used for an earlier checkout that is no longer open. Retry with a new key.");
        }

        billing.StripeSessionId = session.SessionId;
        billing.Status = BillingStatus.Processing;
        billing.UpdatedAtUtc = DateTime.UtcNow;
        await SaveBillingChangesAsync(cancellationToken);

        return new CheckoutResult(new CheckoutSessionResponseDto(session.Url, session.SessionId), IsNewSession: true);
    }

    public async Task<TaskBillingResponseDto> GetBillingStatusAsync(Guid currentUserId, string role, Guid taskId, CancellationToken cancellationToken = default)
    {
        // Same visibility as GET /api/tasks/{id}; out-of-scope tasks are reported as 404.
        var visible = await _db.Tasks
            .AsNoTracking()
            .ScopeToUser(currentUserId, role)
            .AnyAsync(t => t.Id == taskId, cancellationToken);
        if (!visible)
        {
            throw TaskNotFound(taskId);
        }

        var billing = await _db.TaskBillings
            .AsNoTracking()
            .SingleOrDefaultAsync(b => b.TaskId == taskId, cancellationToken)
            ?? throw new NotFoundException($"Task '{taskId}' is not billable.");

        return ToDto(billing);
    }

    public async Task HandleWebhookEventAsync(PaymentWebhookEvent webhookEvent, CancellationToken cancellationToken = default)
    {
        var target = webhookEvent.Type switch
        {
            // "unpaid" means a delayed payment method; async_payment_succeeded/failed follows later.
            "checkout.session.completed" when webhookEvent.PaymentStatus is "paid" or "no_payment_required" => BillingStatus.Paid,
            "checkout.session.async_payment_succeeded" or "payment_intent.succeeded" => BillingStatus.Paid,
            "checkout.session.async_payment_failed" or "payment_intent.payment_failed" => BillingStatus.Failed,
            "checkout.session.expired" => BillingStatus.Unpaid,
            _ => (BillingStatus?)null
        };

        if (target is null)
        {
            _logger.LogDebug("Ignoring Stripe event {EventId} of unhandled type {EventType}.", webhookEvent.EventId, webhookEvent.Type);
            return;
        }

        // SDD §9.3: on a concurrent update (xmin), reload and re-evaluate once.
        for (var attempt = 1; ; attempt++)
        {
            var billing = await FindBillingForEventAsync(webhookEvent, cancellationToken);
            if (billing is null)
            {
                _logger.LogWarning("Stripe event {EventId} ({EventType}) matches no billing or only a stale session; ignored.",
                    webhookEvent.EventId, webhookEvent.Type);
                return;
            }

            if (!IsWebhookTransitionAllowed(billing.Status, target.Value))
            {
                _logger.LogInformation("Stripe event {EventId} ({EventType}) does not apply to billing {BillingId} in {Status}; treated as a replay.",
                    webhookEvent.EventId, webhookEvent.Type, billing.Id, billing.Status);
                return;
            }

            var now = DateTime.UtcNow;
            billing.Status = target.Value;
            billing.UpdatedAtUtc = now;
            billing.StripePaymentIntentId = webhookEvent.PaymentIntentId ?? billing.StripePaymentIntentId;
            if (target == BillingStatus.Paid)
            {
                billing.PaidAtUtc = now;
            }

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Billing {BillingId} moved to {Status} by Stripe event {EventId}.", billing.Id, billing.Status, webhookEvent.EventId);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 1)
            {
                _db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// Webhook-driven transitions (FR-PAY-03). A payment that succeeds after an earlier failed card attempt on the
    /// same session (Failed -> Paid) is accepted, otherwise money taken by Stripe would never be recorded.
    /// </summary>
    private static bool IsWebhookTransitionAllowed(BillingStatus current, BillingStatus target) => (current, target) switch
    {
        (BillingStatus.Processing, BillingStatus.Paid) => true,
        (BillingStatus.Failed, BillingStatus.Paid) => true,
        (BillingStatus.Processing, BillingStatus.Failed) => true,
        (BillingStatus.Processing, BillingStatus.Unpaid) => true,
        _ => false
    };

    private Task<TaskBilling?> FindBillingForEventAsync(PaymentWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        // Matching on the billing's current session means events for a replaced (stale) session find nothing.
        if (webhookEvent.SessionId is not null)
        {
            return _db.TaskBillings.SingleOrDefaultAsync(b => b.StripeSessionId == webhookEvent.SessionId, cancellationToken);
        }

        // payment_intent.* events carry no session; they are correlated through the checkout metadata.
        return webhookEvent.TaskBillingId is { } billingId
            ? _db.TaskBillings.SingleOrDefaultAsync(b => b.Id == billingId, cancellationToken)
            : Task.FromResult<TaskBilling?>(null);
    }

    private async Task<TaskItem> LoadTaskForBillingAsync(Guid taskId, CancellationToken cancellationToken) =>
        await _db.Tasks
            .Include(t => t.Billing)
            .Include(t => t.Creator)
            .Include(t => t.Assignee)
            .SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken)
        ?? throw TaskNotFound(taskId);

    /// <summary>Admins may bill any task; a manager must have created it or manage its creator or assignee.</summary>
    private static void EnsureCanManageBilling(Guid currentUserId, string role, TaskItem task)
    {
        var allowed = TaskQueryExtensions.ParseRole(role) switch
        {
            Role.Admin => true,
            Role.Manager => task.CreatedByUserId == currentUserId
                || task.Creator.ManagerId == currentUserId
                || task.Assignee?.ManagerId == currentUserId,
            _ => false
        };

        if (!allowed)
        {
            throw new ForbiddenException("Only an Admin, or a manager responsible for this task, can manage its billing.");
        }
    }

    private static string ResolveReturnUrl(string? requested, string fallback, string name)
    {
        var url = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new BadRequestException($"{name} must be an absolute http(s) URL.");
        }

        return url;
    }

    private async Task SaveBillingChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // xmin token (concurrent checkout) or the unique TaskId index (concurrent first setup).
            _logger.LogInformation(ex, "Concurrent billing update rejected.");
            throw new ConflictException("The billing was changed by another request. Reload it and try again.");
        }
    }

    private static TaskBillingResponseDto ToDto(TaskBilling billing) => new()
    {
        TaskBillingId = billing.Id,
        TaskId = billing.TaskId,
        AmountCents = billing.AmountCents,
        Currency = billing.Currency,
        Status = billing.Status,
        StripeSessionId = billing.StripeSessionId,
        StripePaymentIntentId = billing.StripePaymentIntentId,
        PaidAtUtc = billing.PaidAtUtc,
        CreatedAtUtc = billing.CreatedAtUtc,
        UpdatedAtUtc = billing.UpdatedAtUtc
    };

    private static NotFoundException TaskNotFound(Guid taskId) => new($"Task '{taskId}' was not found.");

    [GeneratedRegex("^[a-z]{3}$")]
    private static partial Regex CurrencyCodePattern();
}
