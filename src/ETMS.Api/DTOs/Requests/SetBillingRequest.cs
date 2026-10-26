using System.ComponentModel.DataAnnotations;

namespace ETMS.Api.DTOs.Requests;

/// <summary>PUT /api/tasks/{id}/billing (FR-PAY-01).</summary>
public record SetBillingRequest
{
    /// <summary>Amount in minor units, e.g. $150.00 = 15000.</summary>
    [Required]
    public long? AmountCents { get; init; }

    /// <summary>ISO 4217 code; defaults to "usd".</summary>
    public string? Currency { get; init; }
}
