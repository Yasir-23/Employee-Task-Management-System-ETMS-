using ETMS.Api.Common.Exceptions;
using ETMS.Api.DTOs.Requests;
using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Implementations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace ETMS.UnitTests.Services;

public sealed class TaskServiceBusinessRuleTests : IDisposable
{
    private readonly TaskServiceTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    // ---------------------------------------------------------------------
    // BR-01: Active task limit (max 5 Pending/InProgress/InReview)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task BR01_CreateTask_WhenAssigneeHasFiveActiveTasks_ThrowsConflict()
    {
        _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.Pending);
        _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.Pending);
        _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.InProgress);
        _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.InProgress);
        _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.InReview);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            _ctx.Service.CreateTaskAsync(_ctx.Manager.Id, TaskServiceTestContext.CreateRequest(_ctx.Employee.Id)));

        Assert.Equal(StatusCodes.Status409Conflict, ex.StatusCode);
        Assert.Equal(5, await _ctx.Db.Tasks.CountAsync(t => t.AssigneeId == _ctx.Employee.Id));
    }

    [Fact]
    public async Task BR01_CreateTask_WhenAssigneeHasFourActiveTasks_Succeeds()
    {
        _ctx.AddTasks(_ctx.Employee.Id, 4);

        var result = await _ctx.Service.CreateTaskAsync(_ctx.Manager.Id, TaskServiceTestContext.CreateRequest(_ctx.Employee.Id));

        Assert.Equal(_ctx.Employee.Id, result.AssigneeId);
        Assert.Equal(TaskLifecycleStatus.Pending, result.Status);
        Assert.Equal(TaskService.MaxActiveTasksPerEmployee, await _ctx.Db.Tasks.CountAsync(t => t.AssigneeId == _ctx.Employee.Id));
    }

    [Fact]
    public async Task BR01_CompletedTasks_DoNotCountTowardsLimit()
    {
        _ctx.AddTasks(_ctx.Employee.Id, 4);
        _ctx.AddTasks(_ctx.Employee.Id, 10, TaskLifecycleStatus.Completed);

        var result = await _ctx.Service.CreateTaskAsync(_ctx.Manager.Id, TaskServiceTestContext.CreateRequest(_ctx.Employee.Id));

        Assert.Equal(_ctx.Employee.Id, result.AssigneeId);
    }

    [Fact]
    public async Task BR01_Reassign_WhenNewAssigneeHasFiveActiveTasks_ThrowsConflict()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        _ctx.AddTasks(_ctx.Teammate.Id, 5);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _ctx.Service.ReassignTaskAsync(_ctx.Manager.Id, task.Id, new ReassignTaskRequest { NewAssigneeId = _ctx.Teammate.Id }));

        var stored = await _ctx.Db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        Assert.Equal(_ctx.Employee.Id, stored.AssigneeId);
    }

    // ---------------------------------------------------------------------
    // BR-02: Completed tasks are immutable
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(TaskLifecycleStatus.Pending)]
    [InlineData(TaskLifecycleStatus.InProgress)]
    [InlineData(TaskLifecycleStatus.InReview)]
    [InlineData(TaskLifecycleStatus.Completed)]
    public async Task BR02_UpdateStatus_OnCompletedTask_ThrowsConflict(TaskLifecycleStatus newStatus)
    {
        var task = _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.Completed);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            _ctx.Service.UpdateTaskStatusAsync(_ctx.Employee.Id, task.Id, new UpdateTaskStatusRequest { NewStatus = newStatus }));

        Assert.Equal(StatusCodes.Status409Conflict, ex.StatusCode);
        var stored = await _ctx.Db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        Assert.Equal(TaskLifecycleStatus.Completed, stored.Status);
    }

    [Fact]
    public async Task BR02_Reassign_OnCompletedTask_ThrowsConflict()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.Completed);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            _ctx.Service.ReassignTaskAsync(_ctx.Manager.Id, task.Id, new ReassignTaskRequest { NewAssigneeId = _ctx.Teammate.Id }));

        Assert.Equal(StatusCodes.Status409Conflict, ex.StatusCode);
        var stored = await _ctx.Db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        Assert.Equal(_ctx.Employee.Id, stored.AssigneeId);
    }

    // ---------------------------------------------------------------------
    // BR-03: Due date must be in the future
    // ---------------------------------------------------------------------

    [Fact]
    public async Task BR03_CreateTask_WithPastDueDate_ThrowsBadRequest()
    {
        var request = TaskServiceTestContext.CreateRequest(_ctx.Employee.Id, DateTime.UtcNow.AddMinutes(-1));

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => _ctx.Service.CreateTaskAsync(_ctx.Manager.Id, request));

        Assert.Equal(StatusCodes.Status400BadRequest, ex.StatusCode);
        Assert.Empty(_ctx.Db.Tasks);
    }

    [Fact]
    public async Task BR03_CreateTask_WithFutureDueDate_Succeeds()
    {
        var dueDate = DateTime.UtcNow.AddHours(1);

        var result = await _ctx.Service.CreateTaskAsync(
            _ctx.Manager.Id, TaskServiceTestContext.CreateRequest(_ctx.Employee.Id, dueDate));

        Assert.Equal(dueDate, result.DueDateUtc);
    }

    // ---------------------------------------------------------------------
    // BR-04: Employees may only update their own tasks
    // ---------------------------------------------------------------------

    [Fact]
    public async Task BR04_UpdateStatus_WhenNotAssignee_ThrowsForbidden()
    {
        var task = _ctx.AddTask(_ctx.Teammate.Id);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            _ctx.Service.UpdateTaskStatusAsync(_ctx.Employee.Id, task.Id,
                new UpdateTaskStatusRequest { NewStatus = TaskLifecycleStatus.InProgress }));

        Assert.Equal(StatusCodes.Status403Forbidden, ex.StatusCode);
        var stored = await _ctx.Db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        Assert.Equal(TaskLifecycleStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task BR04_UpdateStatus_WhenAssignee_Succeeds()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        var result = await _ctx.Service.UpdateTaskStatusAsync(_ctx.Employee.Id, task.Id,
            new UpdateTaskStatusRequest { NewStatus = TaskLifecycleStatus.InProgress });

        Assert.Equal(TaskLifecycleStatus.InProgress, result.Status);
    }

    // ---------------------------------------------------------------------
    // BR-05: Managers may only assign to their direct reports
    // ---------------------------------------------------------------------

    [Fact]
    public async Task BR05_CreateTask_ForEmployeeOfAnotherManager_ThrowsForbidden()
    {
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            _ctx.Service.CreateTaskAsync(_ctx.Manager.Id, TaskServiceTestContext.CreateRequest(_ctx.ForeignEmployee.Id)));

        Assert.Equal(StatusCodes.Status403Forbidden, ex.StatusCode);
        Assert.Empty(_ctx.Db.Tasks);
    }

    [Fact]
    public async Task BR05_CreateTask_ForNonExistentUser_ThrowsForbidden()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            _ctx.Service.CreateTaskAsync(_ctx.Manager.Id, TaskServiceTestContext.CreateRequest(Guid.NewGuid())));
    }

    [Fact]
    public async Task BR05_CreateTask_ForAnotherManager_ThrowsForbidden()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            _ctx.Service.CreateTaskAsync(_ctx.Manager.Id, TaskServiceTestContext.CreateRequest(_ctx.OtherManager.Id)));
    }

    [Fact]
    public async Task BR05_Reassign_ToEmployeeOfAnotherManager_ThrowsForbidden()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            _ctx.Service.ReassignTaskAsync(_ctx.Manager.Id, task.Id, new ReassignTaskRequest { NewAssigneeId = _ctx.ForeignEmployee.Id }));

        Assert.Equal(StatusCodes.Status403Forbidden, ex.StatusCode);
        var stored = await _ctx.Db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        Assert.Equal(_ctx.Employee.Id, stored.AssigneeId);
    }

    [Fact]
    public async Task BR05_Reassign_TaskOutsideManagersScope_ThrowsForbidden()
    {
        var foreignTask = _ctx.AddTask(_ctx.ForeignEmployee.Id, creatorId: _ctx.OtherManager.Id);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            _ctx.Service.ReassignTaskAsync(_ctx.Manager.Id, foreignTask.Id, new ReassignTaskRequest { NewAssigneeId = _ctx.Employee.Id }));
    }

    [Fact]
    public async Task BR05_Reassign_ToOwnDirectReport_Succeeds()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        var result = await _ctx.Service.ReassignTaskAsync(_ctx.Manager.Id, task.Id, new ReassignTaskRequest { NewAssigneeId = _ctx.Teammate.Id });

        Assert.Equal(_ctx.Teammate.Id, result.AssigneeId);
        Assert.Equal(_ctx.Teammate.FullName, result.AssigneeName);
    }

    // ---------------------------------------------------------------------
    // State machine: Pending -> InProgress -> InReview -> Completed
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(TaskLifecycleStatus.Pending, TaskLifecycleStatus.InProgress)]
    [InlineData(TaskLifecycleStatus.InProgress, TaskLifecycleStatus.InReview)]
    [InlineData(TaskLifecycleStatus.InReview, TaskLifecycleStatus.Completed)]
    public async Task StateMachine_ValidForwardTransition_Succeeds(TaskLifecycleStatus from, TaskLifecycleStatus to)
    {
        var task = _ctx.AddTask(_ctx.Employee.Id, from);

        var result = await _ctx.Service.UpdateTaskStatusAsync(_ctx.Employee.Id, task.Id, new UpdateTaskStatusRequest { NewStatus = to });

        Assert.Equal(to, result.Status);
    }

    [Fact]
    public async Task StateMachine_FullLifecycle_ReachesCompleted()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        foreach (var next in new[] { TaskLifecycleStatus.InProgress, TaskLifecycleStatus.InReview, TaskLifecycleStatus.Completed })
        {
            await _ctx.Service.UpdateTaskStatusAsync(_ctx.Employee.Id, task.Id, new UpdateTaskStatusRequest { NewStatus = next });
        }

        var stored = await _ctx.Db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        Assert.Equal(TaskLifecycleStatus.Completed, stored.Status);
    }

    [Theory]
    [InlineData(TaskLifecycleStatus.Pending, TaskLifecycleStatus.Completed)]   // skip two steps
    [InlineData(TaskLifecycleStatus.Pending, TaskLifecycleStatus.InReview)]    // skip one step
    [InlineData(TaskLifecycleStatus.InProgress, TaskLifecycleStatus.Completed)] // skip one step
    [InlineData(TaskLifecycleStatus.InProgress, TaskLifecycleStatus.Pending)]  // backwards
    [InlineData(TaskLifecycleStatus.InReview, TaskLifecycleStatus.InProgress)] // backwards
    [InlineData(TaskLifecycleStatus.InReview, TaskLifecycleStatus.Pending)]    // backwards
    [InlineData(TaskLifecycleStatus.Pending, TaskLifecycleStatus.Pending)]     // no-op
    public async Task StateMachine_InvalidTransition_ThrowsBadRequest(TaskLifecycleStatus from, TaskLifecycleStatus to)
    {
        var task = _ctx.AddTask(_ctx.Employee.Id, from);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            _ctx.Service.UpdateTaskStatusAsync(_ctx.Employee.Id, task.Id, new UpdateTaskStatusRequest { NewStatus = to }));

        Assert.Equal(StatusCodes.Status400BadRequest, ex.StatusCode);
        var stored = await _ctx.Db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        Assert.Equal(from, stored.Status);
    }

    // ---------------------------------------------------------------------
    // Role-scoped visibility
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetTasks_ScopesResultsByRole()
    {
        var mine = _ctx.AddTask(_ctx.Employee.Id);
        var teammates = _ctx.AddTask(_ctx.Teammate.Id);
        var foreign = _ctx.AddTask(_ctx.ForeignEmployee.Id, creatorId: _ctx.OtherManager.Id);
        var filter = new TaskFilterParams();

        var employeeView = await _ctx.Service.GetTasksAsync(_ctx.Employee.Id, nameof(Role.Employee), filter);
        var managerView = await _ctx.Service.GetTasksAsync(_ctx.Manager.Id, nameof(Role.Manager), filter);
        var adminView = await _ctx.Service.GetTasksAsync(Guid.NewGuid(), nameof(Role.Admin), filter);

        Assert.Equal(new[] { mine.Id }, employeeView.Items.Select(t => t.Id));
        Assert.Equal(new[] { mine.Id, teammates.Id }.OrderBy(id => id), managerView.Items.Select(t => t.Id).OrderBy(id => id));
        Assert.Equal(3, adminView.TotalCount);
        Assert.DoesNotContain(foreign.Id, managerView.Items.Select(t => t.Id));
    }

    [Fact]
    public async Task GetTaskById_OutsideEmployeeScope_ThrowsNotFound()
    {
        var teammatesTask = _ctx.AddTask(_ctx.Teammate.Id);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _ctx.Service.GetTaskByIdAsync(_ctx.Employee.Id, nameof(Role.Employee), teammatesTask.Id));
    }
}
