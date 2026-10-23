using ETMS.Api.Data;
using ETMS.Api.DTOs.Requests;
using ETMS.Api.Models.Entities;
using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Implementations;
using Microsoft.EntityFrameworkCore;

namespace ETMS.UnitTests.Services;

/// <summary>
/// Isolated in-memory database seeded with two managers, each with their own direct reports.
/// </summary>
internal sealed class TaskServiceTestContext : IDisposable
{
    public TaskServiceTestContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"etms-tests-{Guid.NewGuid()}")
            .Options;

        Db = new AppDbContext(options);
        Service = new TaskService(Db);

        var department = new Department { Id = Guid.NewGuid(), Name = "Engineering", CreatedAtUtc = DateTime.UtcNow };
        Db.Departments.Add(department);

        Manager = AddUser("Sarah Manager", Role.Manager, department.Id, managerId: null);
        OtherManager = AddUser("Omar Manager", Role.Manager, department.Id, managerId: null);
        Employee = AddUser("Alex Employee", Role.Employee, department.Id, Manager.Id);
        Teammate = AddUser("Taylor Employee", Role.Employee, department.Id, Manager.Id);
        ForeignEmployee = AddUser("Fiona Employee", Role.Employee, department.Id, OtherManager.Id);

        Db.SaveChanges();
    }

    public AppDbContext Db { get; }
    public TaskService Service { get; }

    public User Manager { get; }
    public User OtherManager { get; }

    /// <summary>Direct report of <see cref="Manager"/>.</summary>
    public User Employee { get; }

    /// <summary>Second direct report of <see cref="Manager"/>.</summary>
    public User Teammate { get; }

    /// <summary>Direct report of <see cref="OtherManager"/>, outside <see cref="Manager"/>'s team.</summary>
    public User ForeignEmployee { get; }

    public static DateTime FutureDueDate => DateTime.UtcNow.AddDays(3);

    public TaskItem AddTask(Guid? assigneeId, TaskLifecycleStatus status = TaskLifecycleStatus.Pending, Guid? creatorId = null)
    {
        var now = DateTime.UtcNow;
        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            Title = $"Task {Guid.NewGuid():N}",
            Description = "Seeded task",
            Priority = TaskPriority.Medium,
            Status = status,
            DueDateUtc = FutureDueDate,
            CreatedByUserId = creatorId ?? Manager.Id,
            AssigneeId = assigneeId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        Db.Tasks.Add(task);
        Db.SaveChanges();
        return task;
    }

    public void AddTasks(Guid assigneeId, int count, TaskLifecycleStatus status = TaskLifecycleStatus.Pending)
    {
        for (var i = 0; i < count; i++)
        {
            AddTask(assigneeId, status);
        }
    }

    public static CreateTaskRequest CreateRequest(Guid assigneeId, DateTime? dueDateUtc = null) => new()
    {
        Title = "Build Payment Gateway Adapter",
        Description = "Implement Stripe checkout flow with webhook handlers.",
        Priority = TaskPriority.High,
        DueDateUtc = dueDateUtc ?? FutureDueDate,
        AssigneeId = assigneeId
    };

    public void Dispose() => Db.Dispose();

    private User AddUser(string fullName, Role role, Guid departmentId, Guid? managerId)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            FullName = fullName,
            Email = $"{fullName.Replace(' ', '.').ToLowerInvariant()}@company.com",
            PasswordHash = "not-used-in-tests",
            Role = role,
            DepartmentId = departmentId,
            ManagerId = managerId,
            CreatedAtUtc = DateTime.UtcNow
        };

        Db.Users.Add(user);
        return user;
    }
}
