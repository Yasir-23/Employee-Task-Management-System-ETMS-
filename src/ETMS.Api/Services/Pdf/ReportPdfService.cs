using ETMS.Api.Services.Implementations;
using ETMS.Api.Services.Interfaces;
using QuestPDF.Fluent;

namespace ETMS.Api.Services.Pdf;

/// <summary>
/// Renders reports with QuestPDF (SDD §8). QuestPDF layout is synchronous and CPU-bound, so the document
/// is produced in memory; the department report is one row per department and stays small.
/// </summary>
public class ReportPdfService : IReportPdfService
{
    private readonly IReportService _reportService;

    public ReportPdfService(IReportService reportService)
    {
        _reportService = reportService;
    }

    public async Task<byte[]> GenerateDepartmentPerformancePdfAsync(DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken = default)
    {
        // Same window rules (and 400 on an invalid window) as GET /api/reports/department-performance.
        var (windowStart, windowEnd) = ReportService.ResolveWindow(fromUtc, toUtc);
        var rows = await _reportService.GetDepartmentPerformanceAsync(fromUtc, toUtc, cancellationToken);

        var model = new DepartmentPerformanceReportModel(windowStart, windowEnd, DateTime.UtcNow, rows);
        return new DepartmentPerformanceDocument(model).GeneratePdf();
    }
}
