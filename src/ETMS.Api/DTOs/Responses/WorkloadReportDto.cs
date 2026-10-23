namespace ETMS.Api.DTOs.Responses;

public record WorkloadReportDto
{
    public Guid EmployeeId { get; init; }
    public string EmployeeName { get; init; } = string.Empty;
    public string? DepartmentName { get; init; }
    public int ActiveTasksCount { get; init; }
    public int CompletedTasksCount { get; init; }
}
