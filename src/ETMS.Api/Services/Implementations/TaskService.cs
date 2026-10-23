using ETMS.Api.Common.Exceptions;
using ETMS.Api.Common.Mappings;
using ETMS.Api.Data;
using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Models.Entities;
using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ETMS.Api.Services.Implementations;

public class TaskService : ITaskService
{
    public const int MaxActiveTasksPerEmployee = 5;

    private readonly AppDbContext _db;

    public TaskService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<TaskResponseDto> CreateTaskAsync(Guid creatorUserId, CreateTaskRequest request, CancellationToken cancellationToken = default)
    {
        var title = request.Title.Trim();
        if (title.Length == 0)
        {
            throw new BadRequestException("Task title is required.");
        }

        var dueDateUtc = EnsureDueDateInFuture(request.DueDateUtc!.Value);
        var assigneeId = request.AssigneeId!.Value;

        await using var transaction = await BeginAssignmentTransactionAsync(assigneeId, cancellationToken);

        await EnsureDirectReportAsync(creatorUserId, assigneeId, cancellationToken);
        await EnsureActiveTaskCapacityAsync(assigneeId, cancellationToken);

        var now = DateTime.UtcNow;
        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = request.Description.Trim(),
            Priority = request.Priority,
            Status = TaskLifecycleStatus.Pending,
            DueDateUtc = dueDateUtc,
            CreatedByUserId = creatorUserId,
            AssigneeId = assigneeId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        return await GetProjectedAsync(task.Id, cancellationToken);
    }

    public async Task<PagedResult<TaskResponseDto>> GetTasksAsync(Guid currentUserId, string role, TaskFilterParams filter, CancellationToken cancellationToken = default)
    {
        var query = ScopeToUser(_db.Tasks.AsNoTracking(), currentUserId, role);

        if (filter.Status is { } status)
        {
            query = query.Where(t => t.Status == status);
        }

        if (filter.Priority is { } priority)
        {
            query = query.Where(t => t.Priority == priority);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        // Deterministic ordering: soonest due first, Id breaks ties.
        var items = await query
            .OrderBy(t => t.DueDateUtc)
            .ThenBy(t => t.Id)
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(TaskMappings.ToDtoExpression)
            .ToListAsync(cancellationToken);

        return new PagedResult<TaskResponseDto>
        {
            Items = items,
            PageNumber = filter.PageNumber,
            PageSize = filter.PageSize,
            TotalCount = totalCount
        };
    }

    public async Task<TaskResponseDto> GetTaskByIdAsync(Guid currentUserId, string role, Guid taskId, CancellationToken cancellationToken = default)
    {
        // Out-of-scope tasks are reported as 404 so their existence is not disclosed.
        return await ScopeToUser(_db.Tasks.AsNoTracking(), currentUserId, role)
            .Where(t => t.Id == taskId)
            .Select(TaskMappings.ToDtoExpression)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw TaskNotFound(taskId);
    }

    public async Task<TaskResponseDto> UpdateTaskStatusAsync(Guid currentUserId, Guid taskId, UpdateTaskStatusRequest request, CancellationToken cancellationToken = default)
    {
        var newStatus = request.NewStatus!.Value;

        var task = await _db.Tasks.SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw TaskNotFound(taskId);

        // BR-04: only the assignee may move a task through its lifecycle.
        if (task.AssigneeId != currentUserId)
        {
            throw new ForbiddenException("You can only update the status of tasks assigned to you.");
        }

        // BR-02: Completed is terminal.
        if (task.Status == TaskLifecycleStatus.Completed)
        {
            throw new ConflictException("Completed tasks are immutable; their status cannot be changed.");
        }

        // FR-TASK-02: strictly linear Pending -> InProgress -> InReview -> Completed.
        if (newStatus != task.Status + 1)
        {
            throw new BadRequestException(
                $"Invalid status transition from {task.Status} to {newStatus}. The next allowed status is {task.Status + 1}.");
        }

        task.Status = newStatus;
        task.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return await GetProjectedAsync(task.Id, cancellationToken);
    }

    public async Task<TaskResponseDto> ReassignTaskAsync(Guid currentManagerId, Guid taskId, ReassignTaskRequest request, CancellationToken cancellationToken = default)
    {
        var newAssigneeId = request.NewAssigneeId!.Value;

        await using var transaction = await BeginAssignmentTransactionAsync(newAssigneeId, cancellationToken);

        var task = await _db.Tasks
            .Include(t => t.Assignee)
            .SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw TaskNotFound(taskId);

        // API.md §3.5: only the task's creator or the current assignee's line manager may reassign it.
        if (task.CreatedByUserId != currentManagerId && task.Assignee?.ManagerId != currentManagerId)
        {
            throw new ForbiddenException("You can only reassign tasks you created or that belong to your team.");
        }

        // BR-02: Completed tasks cannot be reassigned.
        if (task.Status == TaskLifecycleStatus.Completed)
        {
            throw new ConflictException("Completed tasks are immutable and cannot be reassigned.");
        }

        await EnsureDirectReportAsync(currentManagerId, newAssigneeId, cancellationToken);

        if (task.AssigneeId == newAssigneeId)
        {
            return await GetProjectedAsync(task.Id, cancellationToken);
        }

        await EnsureActiveTaskCapacityAsync(newAssigneeId, cancellationToken);

        task.AssigneeId = newAssigneeId;
        // The new assignee has not received this task's 24-hour reminder yet.
        task.ReminderSent = false;
        task.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        return await GetProjectedAsync(task.Id, cancellationToken);
    }

    private IQueryable<TaskItem> ScopeToUser(IQueryable<TaskItem> query, Guid currentUserId, string role)
    {
        if (!Enum.TryParse<Role>(role, ignoreCase: false, out var parsedRole))
        {
            throw new ForbiddenException("Unrecognized role.");
        }

        return parsedRole switch
        {
            Role.Admin => query,
            Role.Manager => query.Where(t =>
                t.CreatedByUserId == currentUserId
                || (t.Assignee != null && t.Assignee.ManagerId == currentUserId)),
            Role.Employee => query.Where(t => t.AssigneeId == currentUserId),
            _ => throw new ForbiddenException("Unrecognized role.")
        };
    }

    // BR-03: due dates must lie in the future relative to server UTC time.
    private static DateTime EnsureDueDateInFuture(DateTime dueDate)
    {
        var dueDateUtc = dueDate.Kind switch
        {
            DateTimeKind.Utc => dueDate,
            DateTimeKind.Local => dueDate.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dueDate, DateTimeKind.Utc)
        };

        if (dueDateUtc <= DateTime.UtcNow)
        {
            throw new BadRequestException("DueDate must be in the future (UTC).");
        }

        return dueDateUtc;
    }

    // BR-05: managers may only assign work to employees whose ManagerId is the manager.
    private async Task EnsureDirectReportAsync(Guid managerId, Guid assigneeId, CancellationToken cancellationToken)
    {
        var isDirectReport = await _db.Users.AnyAsync(
            u => u.Id == assigneeId && u.Role == Role.Employee && u.ManagerId == managerId,
            cancellationToken);

        if (!isDirectReport)
        {
            throw new ForbiddenException("Tasks can only be assigned to employees who report directly to you.");
        }
    }

    // BR-01: an employee may hold at most five active (non-Completed) tasks.
    private async Task EnsureActiveTaskCapacityAsync(Guid assigneeId, CancellationToken cancellationToken)
    {
        var activeCount = await _db.Tasks.CountAsync(
            t => t.AssigneeId == assigneeId && t.Status != TaskLifecycleStatus.Completed,
            cancellationToken);

        if (activeCount >= MaxActiveTasksPerEmployee)
        {
            throw new ConflictException(
                $"The employee already has {activeCount} active tasks; the maximum is {MaxActiveTasksPerEmployee}.");
        }
    }

    /// <summary>
    /// Opens a transaction and row-locks the assignee so concurrent assignments cannot both pass
    /// the BR-01 capacity check. Returns null on non-relational providers (unit tests).
    /// </summary>
    private async Task<IDbContextTransaction?> BeginAssignmentTransactionAsync(Guid assigneeId, CancellationToken cancellationToken)
    {
        if (!_db.Database.IsRelational())
        {
            return null;
        }

        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Users\" WHERE \"Id\" = {assigneeId} FOR UPDATE",
            cancellationToken);
        return transaction;
    }

    private static Task CommitAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken) =>
        transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

    private async Task<TaskResponseDto> GetProjectedAsync(Guid taskId, CancellationToken cancellationToken) =>
        await _db.Tasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(TaskMappings.ToDtoExpression)
            .SingleAsync(cancellationToken);

    private static NotFoundException TaskNotFound(Guid taskId) => new($"Task '{taskId}' was not found.");
}
