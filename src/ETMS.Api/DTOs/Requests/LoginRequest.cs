using System.ComponentModel.DataAnnotations;

namespace ETMS.Api.DTOs.Requests;

public record LoginRequest
{
    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required]
    public string Password { get; init; } = string.Empty;
}
