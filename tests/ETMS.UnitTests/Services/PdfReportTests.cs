using System.Security.Claims;
using System.Text;
using ETMS.Api.Common.Exceptions;
using ETMS.Api.Controllers;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Models.Entities;
using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Implementations;
using ETMS.Api.Services.Interfaces;
using ETMS.Api.Services.Pdf;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace ETMS.UnitTests.Services;

public sealed class PdfReportTests : IDisposable
{
    private static readonly byte[] PdfHeader = Encoding.ASCII.GetBytes("%PDF-");

    private readonly TaskServiceTestContext _ctx = new();
    private readonly ReportPdfService _service;

    static PdfReportTests()
    {
        // Program.cs sets this at startup; QuestPDF refuses to render without a license choice.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public PdfReportTests()
    {
        _service = new ReportPdfService(new ReportService(_ctx.Db));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task DepartmentPerformancePdf_WithTasks_ReturnsValidPdfBytes()
    {
        _ctx.AddTask(_ctx.Employee.Id, TaskLifecycleStatus.Completed);
        _ctx.AddTask(_ctx.Teammate.Id, TaskLifecycleStatus.InProgress);
        _ctx.AddTask(_ctx.ForeignEmployee.Id, TaskLifecycleStatus.Pending, _ctx.OtherManager.Id);

        var pdf = await _service.GenerateDepartmentPerformancePdfAsync(null, null);

        AssertIsPdf(pdf);
    }

    [Fact]
    public async Task DepartmentPerformancePdf_WithNoTasksInWindow_StillRendersPdf()
    {
        var pdf = await _service.GenerateDepartmentPerformancePdfAsync(
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2020, 1, 31, 0, 0, 0, DateTimeKind.Utc));

        AssertIsPdf(pdf);
    }

    [Fact]
    public async Task DepartmentPerformancePdf_InvalidWindow_ThrowsBadRequest()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _service.GenerateDepartmentPerformancePdfAsync(
            new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void DepartmentPerformanceDocument_SpanningSeveralPages_RendersWithoutLayoutErrors()
    {
        // Enough rows to overflow one A4 page, so the repeated table header and page footer are exercised.
        var rows = Enumerable.Range(1, 150).Select(i => new DepartmentPerformanceDto
        {
            DepartmentId = Guid.NewGuid(),
            DepartmentName = $"Department {i:000}",
            TotalTasks = i,
            CompletedTasks = i / 2,
            CompletionRatePercentage = Math.Round(100.0 * (i / 2) / i, 2),
            AverageTurnaroundHours = i % 3 == 0 ? null : i * 1.5
        }).ToList();
        rows.Add(new DepartmentPerformanceDto { DepartmentId = Guid.NewGuid(), DepartmentName = "Complete", TotalTasks = 4, CompletedTasks = 4, CompletionRatePercentage = 100 });

        var model = new DepartmentPerformanceReportModel(
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc),
            DateTime.UtcNow,
            rows);

        AssertIsPdf(new DepartmentPerformanceDocument(model).GeneratePdf());
    }

    [Fact]
    public async Task Controller_DepartmentPerformancePdf_ReturnsPdfFileDownload()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7 stub");
        var pdfService = new Mock<IReportPdfService>();
        pdfService.Setup(s => s.GenerateDepartmentPerformancePdfAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(bytes);
        var controller = new ReportsController(Mock.Of<IReportService>(), pdfService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, nameof(Role.Admin))], "Test"))
                }
            }
        };

        var response = await controller.GetDepartmentPerformancePdf(null, null, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(response);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("department-performance-report.pdf", file.FileDownloadName);
        Assert.Same(bytes, file.FileContents);
    }

    private static void AssertIsPdf(byte[] pdf)
    {
        Assert.True(pdf.Length > PdfHeader.Length, "PDF output is empty.");
        Assert.Equal(PdfHeader, pdf.Take(PdfHeader.Length).ToArray());
    }
}
