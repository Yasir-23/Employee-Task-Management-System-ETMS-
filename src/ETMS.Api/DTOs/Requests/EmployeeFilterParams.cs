using System.ComponentModel.DataAnnotations;
using ETMS.Api.Models.Enums;

namespace ETMS.Api.DTOs.Requests;

public record EmployeeFilterParams
{
    [Range(1, int.MaxValue)]
    public int PageNumber { get; init; } = 1;

    // NFR 5.2: list endpoints enforce a hard upper limit of 100.
    [Range(1, 100)]
    public int PageSize { get; init; } = 20;

    [MaxLength(256)]
    public string? Search { get; init; }

    public Guid? DepartmentId { get; init; }

    [EnumDataType(typeof(Role))]
    public Role? Role { get; init; }
}
