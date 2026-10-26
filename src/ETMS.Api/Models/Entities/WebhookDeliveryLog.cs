namespace ETMS.Api.Models.Entities;

/// <summary>
/// One (event, subscription) delivery (SDD §3.10). Written in the same transaction as the task change that
/// raised the event (transactional outbox), then updated by the delivery worker after each attempt.
/// </summary>
public class WebhookDeliveryLog
{
    /// <summary>Sent to receivers as X-ETMS-Delivery so they can deduplicate.</summary>
    public Guid Id { get; set; }

    public Guid SubscriptionId { get; set; }

    /// <summary>The concrete event type (never "*").</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>The full JSON envelope, frozen at enqueue time so every retry sends the same body.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>HTTP status of the last attempt; null before the first attempt or after a network error/timeout.</summary>
    public int? StatusCode { get; set; }

    public bool IsSuccess { get; set; }

    /// <summary>0 = not attempted yet; the configured maximum without success = dead-lettered.</summary>
    public int AttemptCount { get; set; }

    /// <summary>When the worker may next attempt the delivery; null once it succeeded or was dead-lettered.</summary>
    public DateTime? NextRetryUtc { get; set; }

    /// <summary>Enqueue time until the first attempt, then the time of the last attempt.</summary>
    public DateTime TimestampUtc { get; set; }

    public WebhookSubscription Subscription { get; set; } = null!;
}
