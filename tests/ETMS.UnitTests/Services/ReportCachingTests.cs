using ETMS.Api.Common.Exceptions;
using ETMS.Api.Common.Settings;
using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Caching;
using ETMS.Api.Services.Implementations;
using ETMS.Api.Services.Interfaces;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ETMS.UnitTests.Services;

/// <summary>FR-RESIL-02 / SDD §12.2: cache-aside analytics with generation-based invalidation and fail-open.</summary>
public sealed class ReportCachingTests : IDisposable
{
    private readonly TaskServiceTestContext _ctx = new();
    private readonly Mock<IReportService> _inner = new(MockBehavior.Strict);
    private readonly CachedReportService _cached;

    public ReportCachingTests()
    {
        _cached = new CachedReportService(_inner.Object, _ctx.ReportCache);
    }

    public void Dispose() => _ctx.Dispose();

    // ---------------------------------------------------------------------
    // Cache hits and scoping
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Summary_SecondCall_IsServedFromCacheWithoutQuerying()
    {
        var adminId = Guid.NewGuid();
        _inner.Setup(r => r.GetSummaryAsync(adminId, nameof(Role.Admin), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DashboardSummaryDto { TotalTasks = 7, PendingTasks = 3, OverdueTasks = 1 });

        var first = await _cached.GetSummaryAsync(adminId, nameof(Role.Admin));
        var second = await _cached.GetSummaryAsync(adminId, nameof(Role.Admin));

        Assert.Equal(first, second);
        Assert.Equal(7, second.TotalTasks);
        _inner.Verify(r => r.GetSummaryAsync(adminId, nameof(Role.Admin), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Workload_SecondCall_IsServedFromCache()
    {
        var managerId = Guid.NewGuid();
        IReadOnlyList<WorkloadReportDto> rows = [new WorkloadReportDto { EmployeeId = Guid.NewGuid(), EmployeeName = "Alex", ActiveTasksCount = 2 }];
        _inner.Setup(r => r.GetWorkloadReportAsync(managerId, nameof(Role.Manager), It.IsAny<CancellationToken>())).ReturnsAsync(rows);

        await _cached.GetWorkloadReportAsync(managerId, nameof(Role.Manager));
        var second = await _cached.GetWorkloadReportAsync(managerId, nameof(Role.Manager));

        Assert.Equal(rows, second);
        _inner.Verify(r => r.GetWorkloadReportAsync(managerId, nameof(Role.Manager), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Summary_IsCachedPerCallerScope()
    {
        // Managers in the same department must not see each other's team figures.
        var adminId = Guid.NewGuid();
        _inner.Setup(r => r.GetSummaryAsync(adminId, nameof(Role.Admin), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DashboardSummaryDto { TotalTasks = 100 });
        _inner.Setup(r => r.GetSummaryAsync(_ctx.Manager.Id, nameof(Role.Manager), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DashboardSummaryDto { TotalTasks = 4 });
        _inner.Setup(r => r.GetSummaryAsync(_ctx.OtherManager.Id, nameof(Role.Manager), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DashboardSummaryDto { TotalTasks = 9 });

        await _cached.GetSummaryAsync(adminId, nameof(Role.Admin));
        await _cached.GetSummaryAsync(_ctx.Manager.Id, nameof(Role.Manager));

        Assert.Equal(9, (await _cached.GetSummaryAsync(_ctx.OtherManager.Id, nameof(Role.Manager))).TotalTasks);
        Assert.Equal(4, (await _cached.GetSummaryAsync(_ctx.Manager.Id, nameof(Role.Manager))).TotalTasks);
        Assert.Equal(100, (await _cached.GetSummaryAsync(Guid.NewGuid(), nameof(Role.Admin))).TotalTasks); // any Admin shares "all"
        _inner.Verify(r => r.GetSummaryAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task DepartmentPerformance_IsCachedPerDateWindow()
    {
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
        _inner.Setup(r => r.GetDepartmentPerformanceAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DepartmentPerformanceDto>());

        await _cached.GetDepartmentPerformanceAsync(from, to);
        await _cached.GetDepartmentPerformanceAsync(from, to);
        await _cached.GetDepartmentPerformanceAsync(from, to.AddDays(1));

        _inner.Verify(r => r.GetDepartmentPerformanceAsync(from, to, It.IsAny<CancellationToken>()), Times.Once);
        _inner.Verify(r => r.GetDepartmentPerformanceAsync(from, to.AddDays(1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailedReport_IsNotCached()
    {
        _inner.Setup(r => r.GetSummaryAsync(_ctx.Employee.Id, nameof(Role.Employee), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ForbiddenException("Reports are available to Admins and Managers only."));

        await Assert.ThrowsAsync<ForbiddenException>(() => _cached.GetSummaryAsync(_ctx.Employee.Id, nameof(Role.Employee)));
        await Assert.ThrowsAsync<ForbiddenException>(() => _cached.GetSummaryAsync(_ctx.Employee.Id, nameof(Role.Employee)));

        _inner.Verify(r => r.GetSummaryAsync(_ctx.Employee.Id, nameof(Role.Employee), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void Keys_IncludeGenerationEndpointScopeAndParameters()
    {
        Assert.Equal("reports:g1:summary:all:-", ReportCache.BuildKey("g1", "summary", CachedReportService.Scope(Guid.NewGuid(), "Admin"), "-"));

        var managerId = Guid.NewGuid();
        Assert.Equal($"manager:{managerId:N}", CachedReportService.Scope(managerId, "Manager"));
    }

    // ---------------------------------------------------------------------
    // Invalidation
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Invalidate_MakesTheNextCallMiss()
    {
        var adminId = Guid.NewGuid();
        _inner.SetupSequence(r => r.GetSummaryAsync(adminId, nameof(Role.Admin), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DashboardSummaryDto { TotalTasks = 1 })
            .ReturnsAsync(new DashboardSummaryDto { TotalTasks = 2 });

        await _cached.GetSummaryAsync(adminId, nameof(Role.Admin));
        await _ctx.ReportCache.InvalidateAsync();
        var refreshed = await _cached.GetSummaryAsync(adminId, nameof(Role.Admin));

        Assert.Equal(2, refreshed.TotalTasks);
    }

    [Fact]
    public async Task TaskCreateStatusChangeAndReassign_EachInvalidateCachedReports()
    {
        // Real report queries behind the cache: every task change must be visible on the next read.
        var reports = new CachedReportService(new ReportService(_ctx.Db), _ctx.ReportCache);
        var adminId = Guid.NewGuid();
        Assert.Equal(0, (await reports.GetSummaryAsync(adminId, nameof(Role.Admin))).TotalTasks);

        var created = await _ctx.Service.CreateTaskAsync(_ctx.Manager.Id, TaskServiceTestContext.CreateRequest(_ctx.Employee.Id));
        Assert.Equal(1, (await reports.GetSummaryAsync(adminId, nameof(Role.Admin))).PendingTasks);

        await _ctx.Service.UpdateTaskStatusAsync(_ctx.Employee.Id, created.Id,
            new UpdateTaskStatusRequest { NewStatus = TaskLifecycleStatus.InProgress });
        var afterStatus = await reports.GetSummaryAsync(adminId, nameof(Role.Admin));
        Assert.Equal(0, afterStatus.PendingTasks);
        Assert.Equal(1, afterStatus.InProgressTasks);

        var workloadBefore = await reports.GetWorkloadReportAsync(_ctx.Manager.Id, nameof(Role.Manager));
        Assert.Equal(1, workloadBefore.Single(w => w.EmployeeId == _ctx.Employee.Id).ActiveTasksCount);

        await _ctx.Service.ReassignTaskAsync(_ctx.Manager.Id, created.Id, new ReassignTaskRequest { NewAssigneeId = _ctx.Teammate.Id });
        var workloadAfter = await reports.GetWorkloadReportAsync(_ctx.Manager.Id, nameof(Role.Manager));
        Assert.Equal(0, workloadAfter.Single(w => w.EmployeeId == _ctx.Employee.Id).ActiveTasksCount);
        Assert.Equal(1, workloadAfter.Single(w => w.EmployeeId == _ctx.Teammate.Id).ActiveTasksCount);
    }

    [Fact]
    public async Task RejectedTaskChange_DoesNotInvalidate()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        await _ctx.ReportCache.InvalidateAsync();
        var generation = await _ctx.Cache.GetStringAsync(ReportCache.GenerationKey);

        await Assert.ThrowsAsync<BadRequestException>(() => _ctx.Service.UpdateTaskStatusAsync(_ctx.Employee.Id, task.Id,
            new UpdateTaskStatusRequest { NewStatus = TaskLifecycleStatus.Completed }));

        Assert.Equal(generation, await _ctx.Cache.GetStringAsync(ReportCache.GenerationKey));
    }

    // ---------------------------------------------------------------------
    // Expiration and fail-open behaviour
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Entries_ExpireAbsolutelyAfterTheTtl_AndGenerationNeverExpires()
    {
        var writes = new Dictionary<string, DistributedCacheEntryOptions>();
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>((key, _, options, _) => writes[key] = options)
            .Returns(Task.CompletedTask);
        var reportCache = new ReportCache(cache.Object, Options.Create(new CacheSettings { AnalyticsTtlSeconds = 300 }), NullLogger<ReportCache>.Instance);

        await reportCache.GetOrCreateAsync("summary", "all", "-", _ => Task.FromResult(new DashboardSummaryDto()), CancellationToken.None);
        await reportCache.InvalidateAsync();

        var entry = writes["reports:0:summary:all:-"];
        Assert.Equal(TimeSpan.FromMinutes(5), entry.AbsoluteExpirationRelativeToNow);
        Assert.Null(entry.SlidingExpiration);
        var generation = writes[ReportCache.GenerationKey];
        Assert.Null(generation.AbsoluteExpiration);
        Assert.Null(generation.AbsoluteExpirationRelativeToNow);
        Assert.Null(generation.SlidingExpiration);
    }

    [Fact]
    public async Task UnavailableCache_FallsThroughToTheDatabase()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Redis timed out"));
        cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No connection is available"));
        var reportCache = new ReportCache(cache.Object, Options.Create(new CacheSettings()), NullLogger<ReportCache>.Instance);
        var adminId = Guid.NewGuid();
        _inner.Setup(r => r.GetSummaryAsync(adminId, nameof(Role.Admin), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DashboardSummaryDto { TotalTasks = 3 });
        var reports = new CachedReportService(_inner.Object, reportCache);

        var first = await reports.GetSummaryAsync(adminId, nameof(Role.Admin));
        var second = await reports.GetSummaryAsync(adminId, nameof(Role.Admin));
        await reportCache.InvalidateAsync(); // must not throw either

        Assert.Equal(3, first.TotalTasks);
        Assert.Equal(3, second.TotalTasks);
        _inner.Verify(r => r.GetSummaryAsync(adminId, nameof(Role.Admin), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CacheWriteFailure_StillReturnsTheReport()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Redis timed out"));
        var reportCache = new ReportCache(cache.Object, Options.Create(new CacheSettings()), NullLogger<ReportCache>.Instance);

        var value = await reportCache.GetOrCreateAsync("summary", "all", "-",
            _ => Task.FromResult(new DashboardSummaryDto { TotalTasks = 5 }), CancellationToken.None);

        Assert.Equal(5, value.TotalTasks);
    }
}
