namespace ETMS.Api.DTOs.Responses;

public record DepartmentPerformanceDto
{
    public Guid DepartmentId { get; init; }
    public string DepartmentName { get; init; } = string.Empty;

    /// <summary>Tasks created in the window and assigned to an employee of this department.</summary>
    public int TotalTasks { get; init; }

    public int CompletedTasks { get; init; }
    public double CompletionRatePercentage { get; init; }

    /// <summary>Mean hours from creation to completion of the completed tasks; null when none completed.</summary>
    public double? AverageTurnaroundHours { get; init; }
}
