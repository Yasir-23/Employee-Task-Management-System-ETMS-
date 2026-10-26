namespace ETMS.Api.Services.Webhooks;

/// <summary>Outbound event names (SRS FR-HOOK-03) a subscription can listen to.</summary>
public static class WebhookEventTypes
{
    public const string TaskCreated = "task.created";
    public const string TaskReassigned = "task.reassigned";
    public const string TaskStatusChanged = "task.status_changed";
    public const string TaskCompleted = "task.completed";
    public const string TaskOverdue = "task.overdue";

    /// <summary>Subscribes to every event type.</summary>
    public const string Wildcard = "*";

    public static readonly IReadOnlySet<string> Subscribable = new HashSet<string>(StringComparer.Ordinal)
    {
        TaskCreated, TaskReassigned, TaskStatusChanged, TaskCompleted, TaskOverdue, Wildcard
    };
}
