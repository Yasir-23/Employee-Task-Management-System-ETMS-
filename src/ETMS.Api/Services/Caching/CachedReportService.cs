using System.Globalization;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Interfaces;

namespace ETMS.Api.Services.Caching;

/// <summary>
/// FR-RESIL-02: caching decorator over the database-backed <see cref="IReportService"/>. Keys are scoped to the
/// caller's visibility (all data for Admins, the manager's own team otherwise), so a cached report is never
/// served to a user who could not see it. The PDF report reuses the cached department dataset through this.
/// </summary>
public class CachedReportService : IReportService
{
    private const string NoParameters = "-";

    private readonly IReportService _inner;
    private readonly ReportCache _cache;

    public CachedReportService(IReportService inner, ReportCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    public Task<DashboardSummaryDto> GetSummaryAsync(Guid currentUserId, string role, CancellationToken cancellationToken = default) =>
        _cache.GetOrCreateAsync("summary", Scope(currentUserId, role), NoParameters,
            ct => _inner.GetSummaryAsync(currentUserId, role, ct), cancellationToken);

    public Task<IReadOnlyList<WorkloadReportDto>> GetWorkloadReportAsync(Guid currentUserId, string role, CancellationToken cancellationToken = default) =>
        _cache.GetOrCreateAsync("workload", Scope(currentUserId, role), NoParameters,
            ct => _inner.GetWorkloadReportAsync(currentUserId, role, ct), cancellationToken);

    // Admin-only endpoint, so the data scope is always system-wide.
    public Task<IReadOnlyList<DepartmentPerformanceDto>> GetDepartmentPerformanceAsync(DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken = default) =>
        _cache.GetOrCreateAsync("department-performance", "all", $"{Format(fromUtc)}_{Format(toUtc)}",
            ct => _inner.GetDepartmentPerformanceAsync(fromUtc, toUtc, ct), cancellationToken);

    /// <summary>"all" for Admins; otherwise per user, matching the role scoping inside <see cref="IReportService"/>.</summary>
    public static string Scope(Guid currentUserId, string role) =>
        role == nameof(Role.Admin) ? "all" : $"{role.ToLowerInvariant()}:{currentUserId:N}";

    private static string Format(DateTime? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture) ?? "default";
}
