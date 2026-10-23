using ETMS.Api.Models.Enums;

namespace ETMS.Api.DTOs.Responses;

public record UserDto
{
    public Guid Id { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public Role Role { get; init; }
    public Guid? DepartmentId { get; init; }
    public string? DepartmentName { get; init; }
    public Guid? ManagerId { get; init; }
    public string? ManagerName { get; init; }
}
