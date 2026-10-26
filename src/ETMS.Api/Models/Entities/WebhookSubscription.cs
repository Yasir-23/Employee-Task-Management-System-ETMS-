namespace ETMS.Api.Models.Entities;

/// <summary>An external receiver of outbound task events (SDD §3.9). Soft-deleted via <see cref="IsActive"/>.</summary>
public class WebhookSubscription
{
    public Guid Id { get; set; }

    /// <summary>Absolute https URL that passed the SSRF checks at registration.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>HMAC signing secret, protected with ASP.NET Core Data Protection. Never returned after creation.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>A concrete event type such as "task.completed", or "*" for every event.</summary>
    public string EventType { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    public ICollection<WebhookDeliveryLog> Deliveries { get; set; } = new List<WebhookDeliveryLog>();
}
