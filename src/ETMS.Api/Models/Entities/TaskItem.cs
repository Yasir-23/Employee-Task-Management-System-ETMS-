using ETMS.Api.Models.Enums;

namespace ETMS.Api.Models.Entities;

// Named TaskItem to avoid clashing with System.Threading.Tasks.Task; mapped to the "Tasks" table.
public class TaskItem
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TaskPriority Priority { get; set; }
    public TaskLifecycleStatus Status { get; set; }
    public DateTime DueDateUtc { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid? AssigneeId { get; set; }
    public bool ReminderSent { get; set; }
    public bool OverdueAlertSent { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public User Creator { get; set; } = null!;
    public User? Assignee { get; set; }
    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
    public ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();
    public ICollection<TaskAttachment> Attachments { get; set; } = new List<TaskAttachment>();
}
