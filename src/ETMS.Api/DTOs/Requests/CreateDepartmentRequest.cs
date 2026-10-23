using System.ComponentModel.DataAnnotations;

namespace ETMS.Api.DTOs.Requests;

public record CreateDepartmentRequest
{
    [Required, MaxLength(100)]
    public string Name { get; init; } = string.Empty;
}
