using System.ComponentModel.DataAnnotations;

namespace ETMS.Api.DTOs.Requests;

/// <summary>Optional body for POST /api/tasks/{id}/billing/checkout; omitted URLs fall back to Stripe settings.</summary>
public record CreateCheckoutRequest
{
    [MaxLength(2048)]
    public string? SuccessUrl { get; init; }

    [MaxLength(2048)]
    public string? CancelUrl { get; init; }
}
