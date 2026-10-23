using ETMS.Api.Models.Enums;

namespace ETMS.Api.DTOs.Responses;

/// <summary>Row shape for GET /api/employees (API.md §2.4).</summary>
public record EmployeeListDto
{
    public Guid Id { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public Role Role { get; init; }
    public DepartmentSummaryDto? Department { get; init; }
    public ManagerSummaryDto? Manager { get; init; }
    public int ActiveTasksCount { get; init; }
}

public record DepartmentSummaryDto(Guid Id, string Name);

public record ManagerSummaryDto(Guid Id, string FullName);
