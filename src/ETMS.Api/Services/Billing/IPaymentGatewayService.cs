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
}

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
