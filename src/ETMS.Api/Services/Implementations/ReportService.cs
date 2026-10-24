using ETMS.Api.Common.Exceptions;
using ETMS.Api.Common.Extensions;
using ETMS.Api.Data;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ETMS.Api.Services.Implementations;

public class ReportService : IReportService
{
    private static readonly TimeSpan DefaultPerformanceWindow = TimeSpan.FromDays(30);

    private readonly AppDbContext _db;

    public ReportService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<DashboardSummaryDto> GetSummaryAsync(Guid currentUserId, string role, CancellationToken cancellationToken = default)
    {
        EnsureReportingRole(role);

        var now = DateTime.UtcNow;
        var tasks = _db.Tasks.AsNoTracking().ScopeToUser(currentUserId, role);

        var countsByStatus = await tasks
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);

        var overdue = await tasks.CountAsync(
            t => t.Status != TaskLifecycleStatus.Completed && t.DueDateUtc < now,
            cancellationToken);

        int CountOf(TaskLifecycleStatus status) => countsByStatus.GetValueOrDefault(status);

        return new DashboardSummaryDto
        {
            TotalTasks = countsByStatus.Values.Sum(),
            PendingTasks = CountOf(TaskLifecycleStatus.Pending),
            InProgressTasks = CountOf(TaskLifecycleStatus.InProgress),
            InReviewTasks = CountOf(TaskLifecycleStatus.InReview),
            CompletedTasks = CountOf(TaskLifecycleStatus.Completed),
            OverdueTasks = overdue
        };
    }

    public async Task<IReadOnlyList<WorkloadReportDto>> GetWorkloadReportAsync(Guid currentUserId, string role, CancellationToken cancellationToken = default)
    {
        var parsedRole = EnsureReportingRole(role);

        var employees = _db.Users.AsNoTracking().Where(u => u.Role == Role.Employee);
        if (parsedRole == Role.Manager)
        {
            employees = employees.Where(u => u.ManagerId == currentUserId);
        }

        // FR-DASH-02: active (non-Completed) task count per employee.
        return await employees
            .OrderBy(u => u.FullName)
            .ThenBy(u => u.Id)
            .Select(u => new WorkloadReportDto
            {
                EmployeeId = u.Id,
                EmployeeName = u.FullName,
                DepartmentName = u.Department != null ? u.Department.Name : null,
                ActiveTasksCount = u.AssignedTasks.Count(t => t.Status != TaskLifecycleStatus.Completed),
                CompletedTasksCount = u.AssignedTasks.Count(t => t.Status == TaskLifecycleStatus.Completed)
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DepartmentPerformanceDto>> GetDepartmentPerformanceAsync(DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken = default)
    {
        var (windowStart, windowEnd) = ResolveWindow(fromUtc, toUtc);

        // FR-DASH-03: tasks created in the window, attributed to the assignee's department.
        var tasks = await _db.Tasks
            .AsNoTracking()
            .Where(t => t.CreatedAtUtc >= windowStart
                && t.CreatedAtUtc < windowEnd
                && t.Assignee != null
                && t.Assignee.DepartmentId != null)
            .Select(t => new
            {
                DepartmentId = t.Assignee!.DepartmentId!.Value,
                t.Status,
                t.CreatedAtUtc,
                t.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var departments = await _db.Departments
            .AsNoTracking()
            .OrderBy(d => d.Name)
            .Select(d => new { d.Id, d.Name })
            .ToListAsync(cancellationToken);

        var tasksByDepartment = tasks.ToLookup(t => t.DepartmentId);

        return departments.Select(department =>
        {
            var departmentTasks = tasksByDepartment[department.Id].ToList();
            var completed = departmentTasks.Where(t => t.Status == TaskLifecycleStatus.Completed).ToList();

            return new DepartmentPerformanceDto
            {
                DepartmentId = department.Id,
                DepartmentName = department.Name,
                TotalTasks = departmentTasks.Count,
                CompletedTasks = completed.Count,
                CompletionRatePercentage = departmentTasks.Count == 0
                    ? 0
                    : Math.Round(100.0 * completed.Count / departmentTasks.Count, 2),
                // Completed is terminal (BR-02), so UpdatedAtUtc of a completed task is its completion time.
                AverageTurnaroundHours = completed.Count == 0
                    ? null
                    : Math.Round(completed.Average(t => (t.UpdatedAtUtc - t.CreatedAtUtc).TotalHours), 2)
            };
        }).ToList();
    }

    private static Role EnsureReportingRole(string role)
    {
        var parsedRole = TaskQueryExtensions.ParseRole(role);
        if (parsedRole is not (Role.Admin or Role.Manager))
        {
            throw new ForbiddenException("Reports are available to Admins and Managers only.");
        }

        return parsedRole;
    }

    /// <summary>
    /// Returns a [start, end) window. Defaults to the last 30 days. A date-only <paramref name="toUtc"/>
    /// (e.g. 2026-10-23) includes that whole day.
    /// </summary>
    public static (DateTime Start, DateTime End) ResolveWindow(DateTime? fromUtc, DateTime? toUtc)
    {
        var end = toUtc is { } to
            ? (to.TimeOfDay == TimeSpan.Zero ? AsUtc(to).AddDays(1) : AsUtc(to))
            : DateTime.UtcNow;
        var start = fromUtc is { } from ? AsUtc(from) : end - DefaultPerformanceWindow;

        if (start >= end)
        {
            throw new BadRequestException("'from' must be earlier than 'to'.");
        }

        return (start, end);
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
