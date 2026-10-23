using ETMS.Api.DTOs.Requests;
using ETMS.Api.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace ETMS.UnitTests.Services;

public sealed class TaskAuditInterceptorTests : IDisposable
{
    private readonly TaskServiceTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task UpdateStatus_WritesStatusAuditEntry_AttributedToCurrentUser()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        _ctx.CurrentUserId = _ctx.Employee.Id;

        await _ctx.Service.UpdateTaskStatusAsync(_ctx.Employee.Id, task.Id,
            new UpdateTaskStatusRequest { NewStatus = TaskLifecycleStatus.InProgress });

        var log = Assert.Single(await _ctx.Db.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal(task.Id, log.TaskId);
        Assert.Equal(_ctx.Employee.Id, log.ChangedByUserId);
        Assert.Equal("Status", log.PropertyName);
        Assert.Equal("Pending", log.OldValue);
        Assert.Equal("InProgress", log.NewValue);
        Assert.True(log.TimestampUtc <= DateTime.UtcNow && log.TimestampUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Reassign_WritesAssigneeAuditEntry_Only()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        _ctx.CurrentUserId = _ctx.Manager.Id;

        await _ctx.Service.ReassignTaskAsync(_ctx.Manager.Id, task.Id, new ReassignTaskRequest { NewAssigneeId = _ctx.Teammate.Id });

        // ReminderSent / UpdatedAtUtc also change, but only AssigneeId is an audited property.
        var log = Assert.Single(await _ctx.Db.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal("AssigneeId", log.PropertyName);
        Assert.Equal(_ctx.Employee.Id.ToString(), log.OldValue);
        Assert.Equal(_ctx.Teammate.Id.ToString(), log.NewValue);
        Assert.Equal(_ctx.Manager.Id, log.ChangedByUserId);
    }

    [Fact]
    public async Task DueDateChange_WritesIso8601AuditEntry()
    {
        var originalDue = new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var newDue = new DateTime(2030, 1, 2, 17, 30, 0, DateTimeKind.Utc);
        var task = _ctx.AddTask(_ctx.Employee.Id, dueDateUtc: originalDue);

        task.DueDateUtc = newDue;
        await _ctx.Db.SaveChangesAsync();

        var log = Assert.Single(await _ctx.Db.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal("DueDateUtc", log.PropertyName);
        Assert.Equal("2030-01-01T09:00:00.0000000Z", log.OldValue);
        Assert.Equal("2030-01-02T17:30:00.0000000Z", log.NewValue);
    }

    [Fact]
    public async Task UnauditedPropertyChange_WritesNoAuditEntry()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        task.Title = "Renamed";
        task.ReminderSent = true;
        await _ctx.Db.SaveChangesAsync();

        Assert.Empty(_ctx.Db.AuditLogs);
    }

    [Fact]
    public async Task NewTask_WritesNoAuditEntry()
    {
        await _ctx.Service.CreateTaskAsync(_ctx.Manager.Id, TaskServiceTestContext.CreateRequest(_ctx.Employee.Id));

        Assert.Empty(_ctx.Db.AuditLogs);
    }

    [Fact]
    public async Task AuditedChange_WithoutAuthenticatedUser_IsRejected()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        _ctx.CurrentUserId = null;

        task.Status = TaskLifecycleStatus.InProgress;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _ctx.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task GetTaskById_ReturnsAuditHistoryInOrder()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        _ctx.CurrentUserId = _ctx.Employee.Id;

        foreach (var next in new[] { TaskLifecycleStatus.InProgress, TaskLifecycleStatus.InReview, TaskLifecycleStatus.Completed })
        {
            await _ctx.Service.UpdateTaskStatusAsync(_ctx.Employee.Id, task.Id, new UpdateTaskStatusRequest { NewStatus = next });
        }

        var details = await _ctx.Service.GetTaskByIdAsync(_ctx.Manager.Id, nameof(Role.Manager), task.Id);

        Assert.Equal(
            new[] { ("Pending", "InProgress"), ("InProgress", "InReview"), ("InReview", "Completed") },
            details.AuditLogs.Select(a => (a.OldValue!, a.NewValue!)));
        Assert.All(details.AuditLogs, a => Assert.Equal(_ctx.Employee.FullName, a.ChangedByName));
    }
}
