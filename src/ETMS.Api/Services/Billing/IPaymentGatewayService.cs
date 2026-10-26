using ETMS.Api.Models.Entities;

namespace ETMS.Api.Services.Billing;

/// <summary>Payment provider abstraction (SDD §9.1). Stripe SDK types never leave the implementation.</summary>
public interface IPaymentGatewayService
{
    /// <summary>
    /// Creates a hosted checkout session for the billing. <paramref name="idempotencyKey"/> is forwarded to the
    /// provider so a retried request never creates a second session (FR-PAY-04).
    /// </summary>
    Task<CheckoutSessionResult> CreateCheckoutSessionAsync(TaskBilling billing, TaskItem task, string successUrl, string cancelUrl, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Looks up an existing session, used to reuse one that is still open.</summary>
    Task<CheckoutSessionResult> GetCheckoutSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// BR-08: verifies the provider's signature header against the raw body, then maps the event.
    /// Throws WebhookSignatureException (missing/malformed header -> 400, mismatch/stale -> 401).
    /// </summary>
    PaymentWebhookEvent ParseWebhookEvent(string rawBody, string? signatureHeader);
}

/// <param name="EventId">Provider event ID (Stripe "evt_...").</param>
/// <param name="Type">Provider event type, e.g. "checkout.session.completed".</param>
/// <param name="SessionId">Checkout session the event belongs to, when the event carries one.</param>
/// <param name="PaymentIntentId">Payment intent ("pi_...") when known.</param>
/// <param name="PaymentStatus">Session payment_status or payment intent status.</param>
/// <param name="TaskBillingId">The billing ID from the metadata set at checkout.</param>
public sealed record PaymentWebhookEvent(string EventId, string Type, string? SessionId, string? PaymentIntentId, string? PaymentStatus, Guid? TaskBillingId);

/// <param name="SessionId">Provider session ID (Stripe "cs_...").</param>
/// <param name="Url">Hosted checkout URL; only present while the session is open.</param>
/// <param name="State">Whether the session can still be paid.</param>
/// <param name="ExpiresAtUtc">When an open session stops accepting payment.</param>
public sealed record CheckoutSessionResult(string SessionId, string? Url, CheckoutSessionState State, DateTime? ExpiresAtUtc);

public enum CheckoutSessionState
{
    Open,
    Complete,
    Expired
}
