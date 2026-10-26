using System.ComponentModel.DataAnnotations;

namespace ETMS.Api.DTOs.Requests;

public record CreateWebhookSubscriptionRequest
{
    /// <summary>Absolute https URL; loopback, link-local and private hosts are rejected.</summary>
    [Required, MaxLength(2048)]
    public string Url { get; init; } = string.Empty;

    /// <summary>task.created, task.reassigned, task.status_changed, task.completed, task.overdue, or "*".</summary>
    [Required, MaxLength(64)]
    public string EventType { get; init; } = string.Empty;
}
