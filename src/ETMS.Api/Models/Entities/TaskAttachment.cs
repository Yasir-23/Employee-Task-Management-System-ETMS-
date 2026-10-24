namespace ETMS.Api.Models.Entities;

public class TaskAttachment
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }

    /// <summary>Sanitized original client file name; used only for display and Content-Disposition.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Server-generated path relative to the storage root; never derived from <see cref="FileName"/>.</summary>
    public string StoragePath { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public Guid UploadedByUserId { get; set; }
    public DateTime UploadedAtUtc { get; set; }

    public TaskItem Task { get; set; } = null!;
    public User UploadedByUser { get; set; } = null!;
}
