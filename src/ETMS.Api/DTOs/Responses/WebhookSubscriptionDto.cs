namespace ETMS.Api.DTOs.Responses;

/// <summary>API.md §8.3 list item. The secret is only ever shown as a masked hint.</summary>
public record WebhookSubscriptionDto
{
    public Guid Id { get; init; }
    public string Url { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public string SecretKeyHint { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public WebhookDeliverySummaryDto? LastDelivery { get; init; }
}

/// <summary>Outcome of the most recent delivery attempt to a subscription.</summary>
public record WebhookDeliverySummaryDto
{
    public bool IsSuccess { get; init; }
    public int? StatusCode { get; init; }
    public int AttemptCount { get; init; }
    public DateTime TimestampUtc { get; init; }
}

/// <summary>API.md §8.2 creation response: the only time the plain secret is returned.</summary>
public record WebhookSubscriptionCreatedDto
{
    public Guid Id { get; init; }
    public string Url { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public string SecretKey { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
}
