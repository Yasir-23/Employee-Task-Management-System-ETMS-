using ETMS.Api.DTOs.Responses;

namespace ETMS.Api.Services.Interfaces;

public interface IReportService
{
    Task<DashboardSummaryDto> GetSummaryAsync(Guid currentUserId, string role, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkloadReportDto>> GetWorkloadReportAsync(Guid currentUserId, string role, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DepartmentPerformanceDto>> GetDepartmentPerformanceAsync(DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken = default);
}
