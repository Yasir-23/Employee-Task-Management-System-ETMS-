using System.ComponentModel.DataAnnotations;
using ETMS.Api.Models.Enums;

namespace ETMS.Api.DTOs.Requests;

public record CreateEmployeeRequest
{
    [Required, MaxLength(150)]
    public string FullName { get; init; } = string.Empty;

    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required, MinLength(8), MaxLength(128)]
    public string Password { get; init; } = string.Empty;

    [EnumDataType(typeof(Role))]
    public Role Role { get; init; }

    public Guid? DepartmentId { get; init; }

    public Guid? ManagerId { get; init; }
}
