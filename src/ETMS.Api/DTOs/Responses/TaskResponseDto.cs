using ETMS.Api.Models.Enums;

namespace ETMS.Api.DTOs.Responses;

public record TaskResponseDto
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public TaskPriority Priority { get; init; }
    public TaskLifecycleStatus Status { get; init; }
    public DateTime DueDateUtc { get; init; }
    public Guid CreatedByUserId { get; init; }
    public string CreatorName { get; init; } = string.Empty;
    public Guid? AssigneeId { get; init; }
    public string? AssigneeName { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}
