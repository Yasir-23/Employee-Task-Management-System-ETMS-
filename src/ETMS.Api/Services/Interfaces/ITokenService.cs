using ETMS.Api.Models.Entities;

namespace ETMS.Api.Services.Interfaces;

public interface ITokenService
{
    (string Token, DateTime ExpiresAtUtc) GenerateAccessToken(User user);

    /// <summary>Returns a cryptographically random, URL-safe refresh token (plaintext, never stored).</summary>
    string GenerateRefreshToken();

    /// <summary>SHA-256 hex digest used to store and look up refresh tokens.</summary>
    string HashToken(string token);

    DateTime GetRefreshTokenExpiryUtc();
}
