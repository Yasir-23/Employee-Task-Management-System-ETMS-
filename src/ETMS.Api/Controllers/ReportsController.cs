using ETMS.Api.Common.Extensions;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Services.Interfaces;
using ETMS.Api.Services.Pdf;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ETMS.Api.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize]
[Produces("application/json")]
public class ReportsController : ControllerBase
{
    private readonly IReportService _reportService;
    private readonly IReportPdfService _reportPdfService;

    public ReportsController(IReportService reportService, IReportPdfService reportPdfService)
    {
        _reportService = reportService;
        _reportPdfService = reportPdfService;
    }

    /// <summary>Task counts by status plus overdue count; organization-wide for Admins, team-scoped for Managers.</summary>
    [HttpGet("summary")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(DashboardSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DashboardSummaryDto>> GetSummary(CancellationToken cancellationToken)
    {
        return Ok(await _reportService.GetSummaryAsync(User.GetUserId(), User.GetRole(), cancellationToken));
    }

    /// <summary>Active and completed task counts per employee; all employees for Admins, direct reports for Managers.</summary>
    [HttpGet("workload")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(IReadOnlyList<WorkloadReportDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<WorkloadReportDto>>> GetWorkload(CancellationToken cancellationToken)
    {
        return Ok(await _reportService.GetWorkloadReportAsync(User.GetUserId(), User.GetRole(), cancellationToken));
    }

    /// <summary>Completion rate and average turnaround per department for tasks created in [from, to].</summary>
    /// <param name="from">Window start (UTC). Defaults to 30 days before <paramref name="to"/>.</param>
    /// <param name="to">Window end (UTC). A date without a time includes that whole day. Defaults to now.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet("department-performance")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(IReadOnlyList<DepartmentPerformanceDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<DepartmentPerformanceDto>>> GetDepartmentPerformance(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken cancellationToken)
    {
        return Ok(await _reportService.GetDepartmentPerformanceAsync(from, to, cancellationToken));
    }

    /// <summary>The department performance report as a downloadable PDF (FR-DOC-03). Same window rules as the JSON report.</summary>
    /// <param name="from">Window start (UTC). Defaults to 30 days before <paramref name="to"/>.</param>
    /// <param name="to">Window end (UTC). A date without a time includes that whole day. Defaults to now.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet("department-performance/pdf")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, "application/pdf")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetDepartmentPerformancePdf(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken cancellationToken)
    {
        var pdfBytes = await _reportPdfService.GenerateDepartmentPerformancePdfAsync(from, to, cancellationToken);
        return File(pdfBytes, "application/pdf", "department-performance-report.pdf");
    }
}
