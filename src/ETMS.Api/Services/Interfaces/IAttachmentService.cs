using ETMS.Api.DTOs.Responses;

namespace ETMS.Api.Services.Interfaces;

public interface IAttachmentService
{
    /// <summary>Validates (BR-06) and stores a file for a task the caller can see.</summary>
    Task<TaskAttachmentResponseDto> UploadAsync(Guid currentUserId, string role, Guid taskId, IFormFile file, CancellationToken cancellationToken = default);

    /// <summary>Opens an attachment of a task the caller can see. The caller disposes <see cref="AttachmentDownload.Content"/>.</summary>
    Task<AttachmentDownload> DownloadAsync(Guid currentUserId, string role, Guid taskId, Guid attachmentId, CancellationToken cancellationToken = default);
}

public sealed record AttachmentDownload(Stream Content, string ContentType, string FileName);
