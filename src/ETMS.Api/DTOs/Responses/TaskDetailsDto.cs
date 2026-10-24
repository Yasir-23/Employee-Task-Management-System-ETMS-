namespace ETMS.Api.DTOs.Responses;

/// <summary>GET /api/tasks/{id}: the task plus its discussion thread, attachments and audit history (API.md §3.3, §5).</summary>
public record TaskDetailsDto : TaskResponseDto
{
    // Reuses the base record's copy constructor so task fields are mapped in one place.
    public TaskDetailsDto(TaskResponseDto task) : base(task)
    {
    }

    public IReadOnlyList<CommentResponseDto> Comments { get; init; } = Array.Empty<CommentResponseDto>();
    public IReadOnlyList<TaskAttachmentResponseDto> Attachments { get; init; } = Array.Empty<TaskAttachmentResponseDto>();
    public IReadOnlyList<AuditLogResponseDto> AuditLogs { get; init; } = Array.Empty<AuditLogResponseDto>();
}
