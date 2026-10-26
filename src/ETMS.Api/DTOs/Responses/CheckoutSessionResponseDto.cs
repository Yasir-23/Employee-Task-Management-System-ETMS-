namespace ETMS.Api.DTOs.Responses;

/// <summary>Hosted Stripe Checkout page to redirect the payer to (FR-PAY-02).</summary>
public record CheckoutSessionResponseDto(string SessionUrl, string SessionId);
