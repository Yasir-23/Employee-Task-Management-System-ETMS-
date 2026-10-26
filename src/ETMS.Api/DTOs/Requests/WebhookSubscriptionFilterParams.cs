using System.ComponentModel.DataAnnotations;

namespace ETMS.Api.DTOs.Requests;

public record WebhookSubscriptionFilterParams
{
    /// <summary>Only active (true) or removed (false) subscriptions; all when omitted.</summary>
    public bool? IsActive { get; init; }

    [Range(1, int.MaxValue)]
    public int PageNumber { get; init; } = 1;

    // NFR 5.2: list endpoints enforce a hard upper limit of 100.
    [Range(1, 100)]
    public int PageSize { get; init; } = 20;
}
