namespace ETMS.Api.DTOs.Responses;

/// <summary>Attachment metadata returned by upload and embedded in GET /api/tasks/{id} (API.md §5).</summary>
public record TaskAttachmentResponseDto(
    Guid Id,
    Guid TaskId,
    string FileName,
    string ContentType,
    long FileSizeBytes,
    Guid UploadedByUserId,
    string UploaderName,
    DateTime UploadedAtUtc);
