using System.ComponentModel.DataAnnotations;

namespace ETMS.Api.Common.Settings;

public class JwtSettings
{
    public const string SectionName = "JwtSettings";

    // HMAC-SHA256 requires a key of at least 256 bits.
    [Required, MinLength(32)]
    public string Secret { get; init; } = string.Empty;

    [Required]
    public string Issuer { get; init; } = string.Empty;

    [Required]
    public string Audience { get; init; } = string.Empty;

    // FR-AUTH-02: access tokens live for at most 15 minutes.
    [Range(1, 15)]
    public int AccessTokenExpirationMinutes { get; init; } = 15;

    [Range(1, 30)]
    public int RefreshTokenExpirationDays { get; init; } = 7;
}
