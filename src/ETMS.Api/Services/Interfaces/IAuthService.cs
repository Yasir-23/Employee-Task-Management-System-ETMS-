using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;

namespace ETMS.Api.Services.Interfaces;

public interface IAuthService
{
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
    Task<AuthResponse> RefreshTokenAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>FR-AUTH-05: revokes a refresh token (logout). Idempotent for already-revoked tokens.</summary>
    Task RevokeTokenAsync(string refreshToken, CancellationToken cancellationToken = default);
}
