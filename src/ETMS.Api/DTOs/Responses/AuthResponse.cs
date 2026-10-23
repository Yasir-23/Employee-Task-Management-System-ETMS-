namespace ETMS.Api.DTOs.Responses;

public record AuthResponse
{
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;

    // Expiry of the access token (UTC).
    public DateTime ExpiresAt { get; init; }

    public UserDto User { get; init; } = null!;
}
