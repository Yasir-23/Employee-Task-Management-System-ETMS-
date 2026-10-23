using ETMS.Api.Models.Enums;

namespace ETMS.Api.Models.Entities;

public class User
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public Role Role { get; set; }

    // Nullable for SuperAdmin accounts that do not belong to a department.
    public Guid? DepartmentId { get; set; }
    public Guid? ManagerId { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public Department? Department { get; set; }
    public User? Manager { get; set; }
    public ICollection<User> Subordinates { get; set; } = new List<User>();
    public ICollection<TaskItem> AssignedTasks { get; set; } = new List<TaskItem>();
    public ICollection<TaskItem> CreatedTasks { get; set; } = new List<TaskItem>();
    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
}
