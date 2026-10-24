namespace ETMS.Api.Services.Pdf;

public interface IReportPdfService
{
    /// <summary>FR-DOC-03: the department performance report (same data as the JSON endpoint) rendered as a PDF.</summary>
    Task<byte[]> GenerateDepartmentPerformancePdfAsync(DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken = default);
}
