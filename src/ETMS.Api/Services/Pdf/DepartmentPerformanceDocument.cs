using System.Globalization;
using ETMS.Api.DTOs.Responses;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ETMS.Api.Services.Pdf;

/// <param name="WindowStartUtc">Inclusive start of the reporting window.</param>
/// <param name="WindowEndUtc">Exclusive end of the reporting window.</param>
/// <param name="GeneratedAtUtc">When the report was rendered.</param>
/// <param name="Rows">One row per department, as returned by the JSON report.</param>
public sealed record DepartmentPerformanceReportModel(
    DateTime WindowStartUtc,
    DateTime WindowEndUtc,
    DateTime GeneratedAtUtc,
    IReadOnlyList<DepartmentPerformanceDto> Rows);

/// <summary>QuestPDF template for the department performance report (SDD §8.2). Pure layout: no I/O.</summary>
public sealed class DepartmentPerformanceDocument : IDocument
{
    private const string PrimaryColor = "#1F3A5F";
    private const string AccentColor = "#2E7D32";

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private readonly DepartmentPerformanceReportModel _model;

    public DepartmentPerformanceDocument(DepartmentPerformanceReportModel model)
    {
        _model = model;
    }

    public DocumentMetadata GetMetadata() => new()
    {
        Title = "Department Performance Report",
        Author = "Employee Task Management System",
        CreationDate = new DateTimeOffset(_model.GeneratedAtUtc, TimeSpan.Zero)
    };

    public DocumentSettings GetSettings() => DocumentSettings.Default;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1.5f, Unit.Centimetre);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(style => style.FontSize(10).FontColor(Colors.Grey.Darken4));

            page.Header().Element(ComposeHeader);
            page.Content().PaddingVertical(12).Element(ComposeContent);
            page.Footer().AlignCenter().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(8).FontColor(Colors.Grey.Darken1));
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        });
    }

    private void ComposeHeader(IContainer container)
    {
        container.BorderBottom(2).BorderColor(PrimaryColor).PaddingBottom(8).Row(row =>
        {
            row.RelativeItem().Column(column =>
            {
                column.Item().Text("EMPLOYEE TASK MANAGEMENT SYSTEM").FontSize(8).SemiBold().FontColor(Colors.Grey.Darken1);
                column.Item().Text("Department Performance Report").FontSize(18).SemiBold().FontColor(PrimaryColor);
                column.Item().Text($"Reporting window: {FormatWindow()}").FontColor(Colors.Grey.Darken2);
            });

            row.ConstantItem(140).AlignRight().AlignBottom()
                .Text($"Generated {_model.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm", Culture)} UTC")
                .FontSize(8).FontColor(Colors.Grey.Darken1);
        });
    }

    private void ComposeContent(IContainer container)
    {
        container.Column(column =>
        {
            column.Spacing(14);
            column.Item().Element(ComposeSummary);

            if (_model.Rows.Count == 0)
            {
                column.Item().Text("No departments are configured.").Italic().FontColor(Colors.Grey.Darken1);
                return;
            }

            column.Item().Element(ComposeTable);

            if (_model.Rows.Sum(r => r.TotalTasks) == 0)
            {
                column.Item().Text("No tasks were created in the selected window.").Italic().FontColor(Colors.Grey.Darken1);
            }
        });
    }

    private void ComposeSummary(IContainer container)
    {
        var totals = Totals.From(_model.Rows);

        container.Row(row =>
        {
            row.Spacing(8);
            MetricBox(row.RelativeItem(), "Departments", _model.Rows.Count.ToString(Culture));
            MetricBox(row.RelativeItem(), "Tasks created", totals.TotalTasks.ToString(Culture));
            MetricBox(row.RelativeItem(), "Completed", totals.CompletedTasks.ToString(Culture));
            MetricBox(row.RelativeItem(), "Completion rate", FormatPercentage(totals.CompletionRatePercentage));
        });
    }

    private static void MetricBox(IContainer container, string label, string value)
    {
        container.Border(1).BorderColor(Colors.Grey.Lighten2).Background(Colors.Grey.Lighten5).Padding(8).Column(column =>
        {
            column.Item().Text(label.ToUpperInvariant()).FontSize(7).SemiBold().FontColor(Colors.Grey.Darken1);
            column.Item().Text(value).FontSize(16).SemiBold().FontColor(PrimaryColor);
        });
    }

    private void ComposeTable(IContainer container)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(3);
                columns.RelativeColumn(1.2f);
                columns.RelativeColumn(1.4f);
                columns.RelativeColumn(3);
                columns.RelativeColumn(1.8f);
            });

            // Repeated at the top of every page the table spans.
            table.Header(header =>
            {
                HeaderCell(header.Cell(), "Department", alignRight: false);
                HeaderCell(header.Cell(), "Total", alignRight: true);
                HeaderCell(header.Cell(), "Completed", alignRight: true);
                HeaderCell(header.Cell(), "Completion rate", alignRight: false);
                HeaderCell(header.Cell(), "Avg turnaround (h)", alignRight: true);
            });

            for (var i = 0; i < _model.Rows.Count; i++)
            {
                var row = _model.Rows[i];
                var background = i % 2 == 0 ? Colors.White : Colors.Grey.Lighten4; // zebra striping

                BodyCell(table.Cell(), background).Text(row.DepartmentName);
                BodyCell(table.Cell(), background).AlignRight().Text(row.TotalTasks.ToString(Culture));
                BodyCell(table.Cell(), background).AlignRight().Text(row.CompletedTasks.ToString(Culture));
                CompletionCell(BodyCell(table.Cell(), background), row.CompletionRatePercentage);
                BodyCell(table.Cell(), background).AlignRight().Text(FormatHours(row.AverageTurnaroundHours));
            }

            var totals = Totals.From(_model.Rows);
            TotalCell(table.Cell()).Text("All departments").SemiBold();
            TotalCell(table.Cell()).AlignRight().Text(totals.TotalTasks.ToString(Culture)).SemiBold();
            TotalCell(table.Cell()).AlignRight().Text(totals.CompletedTasks.ToString(Culture)).SemiBold();
            CompletionCell(TotalCell(table.Cell()), totals.CompletionRatePercentage);
            TotalCell(table.Cell()).AlignRight().Text(FormatHours(totals.AverageTurnaroundHours)).SemiBold();
        });
    }

    private static void HeaderCell(IContainer container, string text, bool alignRight)
    {
        var cell = container.Background(PrimaryColor).PaddingVertical(6).PaddingHorizontal(5);
        (alignRight ? cell.AlignRight() : cell).Text(text).FontSize(9).SemiBold().FontColor(Colors.White);
    }

    private static IContainer BodyCell(IContainer container, string background) =>
        container.Background(background).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(5).PaddingHorizontal(5);

    private static IContainer TotalCell(IContainer container) =>
        container.BorderTop(1.5f).BorderColor(PrimaryColor).Background(Colors.Grey.Lighten3).PaddingVertical(6).PaddingHorizontal(5);

    private static void CompletionCell(IContainer container, double percentage)
    {
        var filled = (float)Math.Clamp(percentage, 0, 100);

        container.Row(row =>
        {
            row.Spacing(6);
            row.RelativeItem().AlignMiddle().Height(7).Background(Colors.Grey.Lighten2).Row(bar =>
            {
                // RelativeItem requires a positive weight, so empty segments are skipped.
                if (filled > 0)
                {
                    bar.RelativeItem(filled).Height(7).Background(AccentColor);
                }

                if (filled < 100)
                {
                    bar.RelativeItem(100 - filled).Height(7);
                }
            });
            row.ConstantItem(42).AlignRight().Text(FormatPercentage(percentage));
        });
    }

    private string FormatWindow()
    {
        var start = _model.WindowStartUtc;
        var end = _model.WindowEndUtc;

        // Whole-day windows read as inclusive dates; anything else shows exact times.
        return start.TimeOfDay == TimeSpan.Zero && end.TimeOfDay == TimeSpan.Zero
            ? $"{start.ToString("yyyy-MM-dd", Culture)} to {end.AddDays(-1).ToString("yyyy-MM-dd", Culture)}"
            : $"{start.ToString("yyyy-MM-dd HH:mm", Culture)} to {end.ToString("yyyy-MM-dd HH:mm", Culture)} UTC";
    }

    private static string FormatPercentage(double value) => value.ToString("0.0", Culture) + "%";

    private static string FormatHours(double? hours) => hours?.ToString("0.0", Culture) ?? "-";

    private sealed record Totals(int TotalTasks, int CompletedTasks, double CompletionRatePercentage, double? AverageTurnaroundHours)
    {
        public static Totals From(IReadOnlyList<DepartmentPerformanceDto> rows)
        {
            var total = rows.Sum(r => r.TotalTasks);
            var completed = rows.Sum(r => r.CompletedTasks);
            var withTurnaround = rows.Where(r => r.AverageTurnaroundHours.HasValue && r.CompletedTasks > 0).ToList();
            var weight = withTurnaround.Sum(r => r.CompletedTasks);

            return new Totals(
                total,
                completed,
                total == 0 ? 0 : Math.Round(100.0 * completed / total, 2),
                // Weighted by completed tasks so large departments count proportionally.
                weight == 0 ? null : Math.Round(withTurnaround.Sum(r => r.AverageTurnaroundHours!.Value * r.CompletedTasks) / weight, 2));
        }
    }
}
