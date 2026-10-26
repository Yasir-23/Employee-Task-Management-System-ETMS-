using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Implementations;
using ETMS.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ETMS.UnitTests.Services;

public sealed class NotificationServiceTests : IDisposable
{
    private readonly TaskServiceTestContext _ctx = new();
    private readonly Mock<IEmailService> _email = new();
    private readonly NotificationService _service;

    public NotificationServiceTests()
    {
        _service = new NotificationService(_ctx.Db, _email.Object, _ctx.Webhooks);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task Reminders_TaskDueWithin23To24Hours_EmailsAssigneeAndSetsFlag()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id, dueDateUtc: DateTime.UtcNow.AddHours(23.5));

        var sent = await _service.SendDueDateRemindersAsync();

        Assert.Equal(1, sent);
        _email.Verify(e => e.SendEmailAsync(_ctx.Employee.Email, It.Is<string>(s => s.Contains(task.Title)),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.True((await _ctx.Db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id)).ReminderSent);
    }

    [Fact]
    public async Task Reminders_AreNotResentOnSubsequentRuns()
    {
        _ctx.AddTask(_ctx.Employee.Id, dueDateUtc: DateTime.UtcNow.AddHours(23.5));

        await _service.SendDueDateRemindersAsync();
        var secondRun = await _service.SendDueDateRemindersAsync();

        Assert.Equal(0, secondRun);
        _email.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(22.5)] // too early
    [InlineData(24.5)] // too far out
    public async Task Reminders_TaskOutsideWindow_IsIgnored(double hoursUntilDue)
    {
        _ctx.AddTask(_ctx.Employee.Id, dueDateUtc: DateTime.UtcNow.AddHours(hoursUntilDue));

        Assert.Equal(0, await _service.SendDueDateRemindersAsync());
        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Reminders_SkipCompletedAndUnassignedTasks()
    {
        _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.Completed, dueDateUtc: DateTime.UtcNow.AddHours(23.5));
        _ctx.AddTask(assigneeId: null, dueDateUtc: DateTime.UtcNow.AddHours(23.5));

        Assert.Equal(0, await _service.SendDueDateRemindersAsync());
        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OverdueAlerts_EmailCreatorOnceAndSetFlag()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.InProgress, dueDateUtc: DateTime.UtcNow.AddHours(-2));

        var firstRun = await _service.SendOverdueAlertsAsync();
        var secondRun = await _service.SendOverdueAlertsAsync();

        Assert.Equal(1, firstRun);
        Assert.Equal(0, secondRun);
        _email.Verify(e => e.SendEmailAsync(_ctx.Manager.Email, It.Is<string>(s => s.Contains("Overdue")),
            It.Is<string>(b => b.Contains(_ctx.Employee.FullName)), It.IsAny<CancellationToken>()), Times.Once);
        Assert.True((await _ctx.Db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id)).OverdueAlertSent);
    }

    [Fact]
    public async Task OverdueAlerts_SkipCompletedAndNotYetDueTasks()
    {
        _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.Completed, dueDateUtc: DateTime.UtcNow.AddHours(-2));
        _ctx.AddTask(_ctx.Employee.Id, dueDateUtc: DateTime.UtcNow.AddHours(2));

        Assert.Equal(0, await _service.SendOverdueAlertsAsync());
        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NotificationFlagUpdates_DoNotRequireAuthenticatedUser()
    {
        // The worker runs outside any HTTP request; flag-only updates must not trip the audit interceptor.
        _ctx.CurrentUserId = null;
        _ctx.AddTask(_ctx.Employee.Id, dueDateUtc: DateTime.UtcNow.AddHours(-1));

        Assert.Equal(1, await _service.SendOverdueAlertsAsync());
        Assert.Empty(_ctx.Db.AuditLogs);
    }
}
