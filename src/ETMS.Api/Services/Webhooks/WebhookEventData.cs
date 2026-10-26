using ETMS.Api.Models.Enums;

namespace ETMS.Api.Services.Webhooks;

// The "data" object of each outbound event (API.md §8.5).

public sealed record TaskCreatedEventData(
    Guid TaskId, string Title, TaskPriority Priority, TaskLifecycleStatus Status, DateTime DueDateUtc, Guid? AssigneeId, Guid CreatedByUserId);

public sealed record TaskReassignedEventData(Guid TaskId, Guid? PreviousAssigneeId, Guid NewAssigneeId, Guid ChangedByUserId);

public sealed record TaskStatusChangedEventData(Guid TaskId, TaskLifecycleStatus OldStatus, TaskLifecycleStatus NewStatus, Guid ChangedByUserId);

public sealed record TaskCompletedEventData(Guid TaskId, Guid? AssigneeId, DateTime CompletedAtUtc);

public sealed record TaskOverdueEventData(Guid TaskId, Guid? AssigneeId, Guid CreatedByUserId, TaskLifecycleStatus Status, DateTime DueDateUtc);

/// <summary>The JSON envelope stored in WebhookDeliveryLog.Payload; <see cref="Id"/> is shared by every delivery of one event.</summary>
public sealed record WebhookEnvelope(Guid Id, string Type, DateTime OccurredAtUtc, object Data);
