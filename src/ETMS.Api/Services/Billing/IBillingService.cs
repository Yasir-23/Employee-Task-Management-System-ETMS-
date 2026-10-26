using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;

namespace ETMS.Api.Services.Billing;

public interface IBillingService
{
    /// <summary>FR-PAY-01: creates or updates the task's billing. Admins, or managers related to the task.</summary>
    Task<BillingUpsertResult> SetTaskBillingAsync(Guid currentUserId, string role, Guid taskId, SetBillingRequest request, CancellationToken cancellationToken = default);

    /// <summary>FR-PAY-02/04 + BR-07: starts (or reuses an open) Stripe checkout session.</summary>
    Task<CheckoutResult> CreateCheckoutSessionAsync(Guid currentUserId, string role, Guid taskId, CreateCheckoutRequest? request, string? idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Billing state of a task the caller can see.</summary>
    Task<TaskBillingResponseDto> GetBillingStatusAsync(Guid currentUserId, string role, Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// FR-PAY-03 / FR-HOOK-01: applies a verified provider event to the billing state machine. Replays, stale
    /// sessions, unknown billings and unhandled event types are acknowledged without side effects.
    /// </summary>
    Task HandleWebhookEventAsync(PaymentWebhookEvent webhookEvent, CancellationToken cancellationToken = default);
}

/// <param name="Billing">The billing after the change.</param>
/// <param name="Created">True when this call created the billing (201), false when it updated it (200).</param>
public sealed record BillingUpsertResult(TaskBillingResponseDto Billing, bool Created);

/// <param name="Session">The checkout session to redirect the payer to.</param>
/// <param name="IsNewSession">True when a new session was created (201), false when an open one was reused (200).</param>
public sealed record CheckoutResult(CheckoutSessionResponseDto Session, bool IsNewSession);
