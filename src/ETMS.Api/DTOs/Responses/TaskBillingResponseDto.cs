using ETMS.Api.Models.Enums;

namespace ETMS.Api.DTOs.Responses;

public record TaskBillingResponseDto
{
    public Guid TaskBillingId { get; init; }
    public Guid TaskId { get; init; }
    public long AmountCents { get; init; }
    public string Currency { get; init; } = string.Empty;
    public BillingStatus Status { get; init; }
    public string? StripeSessionId { get; init; }
    public string? StripePaymentIntentId { get; init; }
    public DateTime? PaidAtUtc { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
}
