namespace ETMS.Api.Models.Entities;

public class AuditLog
{
    public long Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid ChangedByUserId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime TimestampUtc { get; set; }

    public TaskItem Task { get; set; } = null!;
    public User ChangedByUser { get; set; } = null!;
}
