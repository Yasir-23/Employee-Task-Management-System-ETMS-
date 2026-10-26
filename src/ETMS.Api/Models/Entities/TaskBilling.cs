using ETMS.Api.Models.Enums;

namespace ETMS.Api.Models.Entities;

public class TaskBilling
{
    public Guid Id { get; set; }

    /// <summary>One billing per task (unique).</summary>
    public Guid TaskId { get; set; }

    /// <summary>Amount in minor units, e.g. $150.00 = 15000.</summary>
    public long AmountCents { get; set; }

    /// <summary>Lowercase ISO 4217 code.</summary>
    public string Currency { get; set; } = "usd";

    public BillingStatus Status { get; set; }
    public string? StripePaymentIntentId { get; set; }

    /// <summary>The most recent Checkout Session created for this billing.</summary>
    public string? StripeSessionId { get; set; }

    public DateTime? PaidAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    public TaskItem Task { get; set; } = null!;
}
