using ETMS.Api.Common.Exceptions;
using ETMS.Api.Models.Entities;
using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Implementations;

namespace ETMS.UnitTests.Services;

public sealed class ReportServiceTests : IDisposable
{
    private readonly TaskServiceTestContext _ctx = new();
    private readonly ReportService _service;

    public ReportServiceTests()
    {
        _service = new ReportService(_ctx.Db);
    }

    public void Dispose() => _ctx.Dispose();

    // ---------------------------------------------------------------------
    // Summary (FR-DASH-01)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Summary_ForAdmin_CountsEveryTaskByStatusAndOverdue()
    {
        _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.Pending);
        _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.InProgress, dueDateUtc: DateTime.UtcNow.AddHours(-1)); // overdue
        _ctx.AddTask(_ctx.Teammate.Id, TaskLifecycleStatus.InReview);
        _ctx.AddTask(_ctx.Teammate.Id, TaskLifecycleStatus.Completed, dueDateUtc: DateTime.UtcNow.AddHours(-1)); // past due but done
        _ctx.AddTask(_ctx.ForeignEmployee.Id, TaskLifecycleStatus.Pending, _ctx.OtherManager.Id, DateTime.UtcNow.AddDays(-1)); // overdue

        var summary = await _service.GetSummaryAsync(Guid.NewGuid(), nameof(Role.Admin));

        Assert.Equal(5, summary.TotalTasks);
        Assert.Equal(2, summary.PendingTasks);
        Assert.Equal(1, summary.InProgressTasks);
        Assert.Equal(1, summary.InReviewTasks);
        Assert.Equal(1, summary.CompletedTasks);
        Assert.Equal(2, summary.OverdueTasks);
    }

    [Fact]
    public async Task Summary_ForManager_IsScopedToOwnAndTeamTasks()
    {
        _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.Pending);
        _ctx.AddTask(_ctx.Teammate.Id, TaskLifecycleStatus.Completed);
        _ctx.AddTask(_ctx.ForeignEmployee.Id, TaskLifecycleStatus.Pending, _ctx.OtherManager.Id);

        var summary = await _service.GetSummaryAsync(_ctx.Manager.Id, nameof(Role.Manager));

        Assert.Equal(2, summary.TotalTasks);
        Assert.Equal(1, summary.PendingTasks);
        Assert.Equal(1, summary.CompletedTasks);
    }

    [Fact]
    public async Task Summary_WithNoTasks_ReturnsZeros()
    {
        var summary = await _service.GetSummaryAsync(_ctx.Manager.Id, nameof(Role.Manager));

        Assert.Equal(0, summary.TotalTasks);
        Assert.Equal(0, summary.OverdueTasks);
    }

    [Fact]
    public async Task Summary_ForEmployee_ThrowsForbidden()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => _service.GetSummaryAsync(_ctx.Employee.Id, nameof(Role.Employee)));
    }

    // ---------------------------------------------------------------------
    // Workload (FR-DASH-02)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Workload_ForAdmin_ListsAllEmployeesWithActiveAndCompletedCounts()
    {
        _ctx.AddTasks(_ctx.Employee.Id, 3);
        _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.InReview);
        _ctx.AddTasks(_ctx.Employee.Id, 2, TaskLifecycleStatus.Completed);
        _ctx.AddTask(_ctx.ForeignEmployee.Id, creatorId: _ctx.OtherManager.Id);

        var workload = await _service.GetWorkloadReportAsync(Guid.NewGuid(), nameof(Role.Admin));

        // Managers are not part of the workload report; ordered by name.
        Assert.Equal(new[] { "Alex Employee", "Fiona Employee", "Taylor Employee" }, workload.Select(w => w.EmployeeName));

        var alex = workload.Single(w => w.EmployeeId == _ctx.Employee.Id);
        Assert.Equal(4, alex.ActiveTasksCount);
        Assert.Equal(2, alex.CompletedTasksCount);
        Assert.Equal("Engineering", alex.DepartmentName);

        var taylor = workload.Single(w => w.EmployeeId == _ctx.Teammate.Id);
        Assert.Equal(0, taylor.ActiveTasksCount);
        Assert.Equal(0, taylor.CompletedTasksCount);
    }

    [Fact]
    public async Task Workload_ForManager_ListsOnlyDirectReports()
    {
        var workload = await _service.GetWorkloadReportAsync(_ctx.Manager.Id, nameof(Role.Manager));

        Assert.Equal(
            new[] { _ctx.Employee.Id, _ctx.Teammate.Id }.OrderBy(id => id),
            workload.Select(w => w.EmployeeId).OrderBy(id => id));
    }

    [Fact]
    public async Task Workload_ForEmployee_ThrowsForbidden()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => _service.GetWorkloadReportAsync(_ctx.Employee.Id, nameof(Role.Employee)));
    }

    [Fact]
    public async Task Reports_WithUnknownRole_ThrowForbidden()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => _service.GetSummaryAsync(_ctx.Manager.Id, "SuperUser"));
    }

    // ---------------------------------------------------------------------
    // Department performance (FR-DASH-03)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DepartmentPerformance_ComputesCompletionRateAndTurnaroundWithinWindow()
    {
        var windowStart = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        var sales = new Department { Id = Guid.NewGuid(), Name = "Sales", CreatedAtUtc = windowStart };
        var salesRep = new User
        {
            Id = Guid.NewGuid(),
            FullName = "Sam Sales",
            Email = "sam@company.com",
            PasswordHash = "n/a",
            Role = Role.Employee,
            DepartmentId = sales.Id,
            ManagerId = _ctx.OtherManager.Id,
            CreatedAtUtc = windowStart
        };
        _ctx.Db.AddRange(sales, salesRep);
        await _ctx.Db.SaveChangesAsync();

        // Engineering: 4 tasks in window, 2 completed after 10h and 20h => 50%, 15h average.
        AddTimedTask(_ctx.Employee.Id, TaskLifecycleStatus.Completed, windowStart.AddDays(1), turnaroundHours: 10);
        AddTimedTask(_ctx.Teammate.Id, TaskLifecycleStatus.Completed, windowStart.AddDays(2), turnaroundHours: 20);
        AddTimedTask(_ctx.Employee.Id, TaskLifecycleStatus.InProgress, windowStart.AddDays(3));
        AddTimedTask(_ctx.Teammate.Id, TaskLifecycleStatus.Pending, windowStart.AddDays(4));
        // Outside the window: ignored.
        AddTimedTask(_ctx.Employee.Id, TaskLifecycleStatus.Completed, windowStart.AddDays(-5), turnaroundHours: 100);
        AddTimedTask(_ctx.Employee.Id, TaskLifecycleStatus.Completed, windowStart.AddDays(40), turnaroundHours: 100);
        // Sales: 3 tasks, 1 completed after 6h => 33.33%, 6h average.
        AddTimedTask(salesRep.Id, TaskLifecycleStatus.Completed, windowStart.AddDays(5), turnaroundHours: 6, creatorId: _ctx.OtherManager.Id);
        AddTimedTask(salesRep.Id, TaskLifecycleStatus.InReview, windowStart.AddDays(6), creatorId: _ctx.OtherManager.Id);
        AddTimedTask(salesRep.Id, TaskLifecycleStatus.Pending, windowStart.AddDays(7), creatorId: _ctx.OtherManager.Id);

        var report = await _service.GetDepartmentPerformanceAsync(windowStart, new DateTime(2026, 10, 23));

        Assert.Equal(new[] { "Engineering", "Sales" }, report.Select(r => r.DepartmentName));

        var engineering = report.Single(r => r.DepartmentName == "Engineering");
        Assert.Equal(4, engineering.TotalTasks);
        Assert.Equal(2, engineering.CompletedTasks);
        Assert.Equal(50, engineering.CompletionRatePercentage);
        Assert.Equal(15, engineering.AverageTurnaroundHours);

        var salesRow = report.Single(r => r.DepartmentName == "Sales");
        Assert.Equal(3, salesRow.TotalTasks);
        Assert.Equal(1, salesRow.CompletedTasks);
        Assert.Equal(33.33, salesRow.CompletionRatePercentage);
        Assert.Equal(6, salesRow.AverageTurnaroundHours);
    }

    [Fact]
    public async Task DepartmentPerformance_DateOnlyUpperBound_IncludesWholeDay()
    {
        AddTimedTask(_ctx.Employee.Id, TaskLifecycleStatus.Pending, new DateTime(2026, 10, 23, 18, 0, 0, DateTimeKind.Utc));

        var report = await _service.GetDepartmentPerformanceAsync(new DateTime(2026, 10, 1), new DateTime(2026, 10, 23));

        Assert.Equal(1, Assert.Single(report).TotalTasks);
    }

    [Fact]
    public async Task DepartmentPerformance_DepartmentWithoutTasks_ReportsZeroAndNullTurnaround()
    {
        var report = await _service.GetDepartmentPerformanceAsync(null, null);

        var row = Assert.Single(report);
        Assert.Equal(0, row.TotalTasks);
        Assert.Equal(0, row.CompletionRatePercentage);
        Assert.Null(row.AverageTurnaroundHours);
    }

    [Fact]
    public async Task DepartmentPerformance_FromAfterTo_ThrowsBadRequest()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _service.GetDepartmentPerformanceAsync(new DateTime(2026, 10, 23, 12, 0, 0), new DateTime(2026, 10, 1, 12, 0, 0)));
    }

    private void AddTimedTask(Guid assigneeId, TaskLifecycleStatus status, DateTime createdAtUtc, double turnaroundHours = 1, Guid? creatorId = null)
    {
        var task = _ctx.AddTask(assigneeId, status, creatorId);
        task.CreatedAtUtc = createdAtUtc;
        task.UpdatedAtUtc = createdAtUtc.AddHours(turnaroundHours);
        _ctx.Db.SaveChanges();
    }
}
