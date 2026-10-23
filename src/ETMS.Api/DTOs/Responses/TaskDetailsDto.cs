namespace ETMS.Api.DTOs.Responses;

/// <summary>GET /api/tasks/{id}: the task plus its discussion thread and audit history (API.md §3.3).</summary>
public record TaskDetailsDto : TaskResponseDto
{
    // Reuses the base record's copy constructor so task fields are mapped in one place.
    public TaskDetailsDto(TaskResponseDto task) : base(task)
    {
    }

    public IReadOnlyList<CommentResponseDto> Comments { get; init; } = Array.Empty<CommentResponseDto>();
    public IReadOnlyList<AuditLogResponseDto> AuditLogs { get; init; } = Array.Empty<AuditLogResponseDto>();
}
