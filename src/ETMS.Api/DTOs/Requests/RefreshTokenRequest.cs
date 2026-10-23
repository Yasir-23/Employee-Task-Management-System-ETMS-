using System.ComponentModel.DataAnnotations;

namespace ETMS.Api.DTOs.Requests;

public record RefreshTokenRequest
{
    [Required]
    public string RefreshToken { get; init; } = string.Empty;
}
