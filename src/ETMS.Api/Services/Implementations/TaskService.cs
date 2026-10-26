using System.Globalization;
using ETMS.Api.Common.Exceptions;
using ETMS.Api.Common.Extensions;
using ETMS.Api.Common.Mappings;
using ETMS.Api.Data;
using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Models.Entities;
using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Interfaces;
using ETMS.Api.Services.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ETMS.Api.Services.Implementations;

public class TaskService : ITaskService
{
    public const int MaxActiveTasksPerEmployee = 5;

    private readonly AppDbContext _db;
    private readonly IEmailService _emailService;
    private readonly IWebhookDispatcher _webhooks;
    private readonly ILogger<TaskService> _logger;

    public TaskService(AppDbContext db, IEmailService emailService, IWebhookDispatcher webhooks, ILogger<TaskService> logger)
    {
        _db = db;
        _emailService = emailService;
        _webhooks = webhooks;
        _logger = logger;
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

        var assignee = await EnsureDirectReportAsync(creatorUserId, assigneeId, cancellationToken);
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
        // FR-HOOK-03: outbox rows are saved by the same SaveChanges as the task.
        await _webhooks.EnqueueAsync(WebhookEventTypes.TaskCreated, new TaskCreatedEventData(
            task.Id, task.Title, task.Priority, task.Status, task.DueDateUtc, task.AssigneeId, creatorUserId), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        var created = await GetProjectedAsync(task.Id, cancellationToken);
        await NotifyAssigneeAsync(assignee, created, isReassignment: false, cancellationToken);
        return created;
    }

    public async Task<PagedResult<TaskResponseDto>> GetTasksAsync(Guid currentUserId, string role, TaskFilterParams filter, CancellationToken cancellationToken = default)
    {
        var query = _db.Tasks.AsNoTracking().ScopeToUser(currentUserId, role);

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

    public async Task<TaskDetailsDto> GetTaskByIdAsync(Guid currentUserId, string role, Guid taskId, CancellationToken cancellationToken = default)
    {
        // Out-of-scope tasks are reported as 404 so their existence is not disclosed.
        var task = await _db.Tasks.AsNoTracking()
            .ScopeToUser(currentUserId, role)
            .Where(t => t.Id == taskId)
            .Select(TaskMappings.ToDtoExpression)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw TaskNotFound(taskId);

        var comments = await _db.Comments
            .AsNoTracking()
            .Where(c => c.TaskId == taskId)
            .OrderBy(c => c.CreatedAtUtc)
            .ThenBy(c => c.Id)
            .Select(TaskMappings.CommentToDtoExpression)
            .ToListAsync(cancellationToken);

        var attachments = await _db.TaskAttachments
            .AsNoTracking()
            .Where(a => a.TaskId == taskId)
            .OrderBy(a => a.UploadedAtUtc)
            .ThenBy(a => a.Id)
            .Select(TaskMappings.AttachmentToDtoExpression)
            .ToListAsync(cancellationToken);

        var auditLogs = await _db.AuditLogs
            .AsNoTracking()
            .Where(a => a.TaskId == taskId)
            .OrderBy(a => a.TimestampUtc)
            .ThenBy(a => a.Id)
            .Select(a => new AuditLogResponseDto
            {
                Id = a.Id,
                ChangedByUserId = a.ChangedByUserId,
                ChangedByName = a.ChangedByUser.FullName,
                PropertyName = a.PropertyName,
                OldValue = a.OldValue,
                NewValue = a.NewValue,
                TimestampUtc = a.TimestampUtc
            })
            .ToListAsync(cancellationToken);

        return new TaskDetailsDto(task)
        {
            Comments = comments,
            Attachments = attachments,
            AuditLogs = auditLogs
        };
    }

    public async Task<CommentResponseDto> AddCommentAsync(Guid currentUserId, string role, Guid taskId, AddCommentRequest request, CancellationToken cancellationToken = default)
    {
        var content = request.Content.Trim();
        if (content.Length == 0)
        {
            throw new BadRequestException("Comment content is required.");
        }

        var task = await _db.Tasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => new { t.CreatedByUserId, t.AssigneeId })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw TaskNotFound(taskId);

        // API.md §3.6: only the assignee, the creator, or an Admin may comment.
        var isAdmin = role == nameof(Role.Admin);
        if (!isAdmin && task.CreatedByUserId != currentUserId && task.AssigneeId != currentUserId)
        {
            throw new ForbiddenException("Only the task's assignee, its creator, or an Admin can comment on it.");
        }

        var comment = new Comment
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            UserId = currentUserId,
            Content = content,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.Comments.Add(comment);
        await _db.SaveChangesAsync(cancellationToken);

        return await _db.Comments
            .AsNoTracking()
            .Where(c => c.Id == comment.Id)
            .Select(TaskMappings.CommentToDtoExpression)
            .SingleAsync(cancellationToken);
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

        var oldStatus = task.Status;
        task.Status = newStatus;
        task.UpdatedAtUtc = DateTime.UtcNow;

        await _webhooks.EnqueueAsync(WebhookEventTypes.TaskStatusChanged,
            new TaskStatusChangedEventData(task.Id, oldStatus, newStatus, currentUserId), cancellationToken);
        if (newStatus == TaskLifecycleStatus.Completed)
        {
            await _webhooks.EnqueueAsync(WebhookEventTypes.TaskCompleted,
                new TaskCompletedEventData(task.Id, task.AssigneeId, task.UpdatedAtUtc), cancellationToken);
        }

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

        var newAssignee = await EnsureDirectReportAsync(currentManagerId, newAssigneeId, cancellationToken);

        if (task.AssigneeId == newAssigneeId)
        {
            return await GetProjectedAsync(task.Id, cancellationToken);
        }

        await EnsureActiveTaskCapacityAsync(newAssigneeId, cancellationToken);

        var previousAssigneeId = task.AssigneeId;
        task.AssigneeId = newAssigneeId;
        // The new assignee has not received this task's 24-hour reminder yet.
        task.ReminderSent = false;
        task.UpdatedAtUtc = DateTime.UtcNow;

        await _webhooks.EnqueueAsync(WebhookEventTypes.TaskReassigned,
            new TaskReassignedEventData(task.Id, previousAssigneeId, newAssigneeId, currentManagerId), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        var reassigned = await GetProjectedAsync(task.Id, cancellationToken);
        await NotifyAssigneeAsync(newAssignee, reassigned, isReassignment: true, cancellationToken);
        return reassigned;
    }

    /// <summary>
    /// FR-NOTIF-01: emails the assignee once the assignment has been committed. A delivery failure is
    /// logged rather than surfaced, because the task change itself has already succeeded.
    /// </summary>
    private async Task NotifyAssigneeAsync(AssigneeContact assignee, TaskResponseDto task, bool isReassignment, CancellationToken cancellationToken)
    {
        var subject = isReassignment
            ? $"Task reassigned to you: \"{task.Title}\""
            : $"New task assigned: \"{task.Title}\"";
        var body =
            $"Hi {assignee.FullName}, {task.CreatorName} has assigned you the task \"{task.Title}\". " +
            $"Priority: {task.Priority}. Due: {task.DueDateUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}.";

        try
        {
            await _emailService.SendEmailAsync(assignee.Email, subject, body, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to send assignment email for task {TaskId} to {Email}.", task.Id, assignee.Email);
        }
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
    private async Task<AssigneeContact> EnsureDirectReportAsync(Guid managerId, Guid assigneeId, CancellationToken cancellationToken)
    {
        return await _db.Users
            .Where(u => u.Id == assigneeId && u.Role == Role.Employee && u.ManagerId == managerId)
            .Select(u => new AssigneeContact(u.Email, u.FullName))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ForbiddenException("Tasks can only be assigned to employees who report directly to you.");
    }

    private sealed record AssigneeContact(string Email, string FullName);

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
