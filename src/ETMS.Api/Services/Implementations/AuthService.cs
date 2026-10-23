using ETMS.Api.Common.Exceptions;
using ETMS.Api.Common.Mappings;
using ETMS.Api.Data;
using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Models.Entities;
using ETMS.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ETMS.Api.Services.Implementations;

public class AuthService : IAuthService
{
    private const string InvalidCredentialsMessage = "Invalid email or password.";
    private const string InvalidRefreshTokenMessage = "The refresh token is invalid, expired, or has been revoked.";

    private readonly AppDbContext _db;
    private readonly IPasswordHasherService _passwordHasher;
    private readonly ITokenService _tokenService;

    public AuthService(AppDbContext db, IPasswordHasherService passwordHasher, ITokenService tokenService)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        var user = await _db.Users
            .Include(u => u.Department)
            .Include(u => u.Manager)
            .SingleOrDefaultAsync(u => u.Email == email, cancellationToken);

        if (user is null || !_passwordHasher.VerifyPassword(user, user.PasswordHash, request.Password))
        {
            throw new UnauthorizedException(InvalidCredentialsMessage);
        }

        var now = DateTime.UtcNow;
        var expiredTokens = await _db.RefreshTokens
            .Where(t => t.UserId == user.Id && !t.IsRevoked && t.ExpiresAtUtc <= now)
            .ToListAsync(cancellationToken);

        foreach (var expired in expiredTokens)
        {
            expired.IsRevoked = true;
        }

        return await IssueTokensAsync(user, cancellationToken);
    }

    public async Task<AuthResponse> RefreshTokenAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default)
    {
        var tokenHash = _tokenService.HashToken(request.RefreshToken);
        var now = DateTime.UtcNow;

        var stored = await _db.RefreshTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        if (stored is null || stored.IsRevoked || stored.ExpiresAtUtc <= now)
        {
            throw new UnauthorizedException(InvalidRefreshTokenMessage);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // FR-AUTH-04: atomically claim the token so two concurrent refreshes cannot both rotate it.
        var claimed = await _db.RefreshTokens
            .Where(t => t.Id == stored.Id && !t.IsRevoked)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsRevoked, true), cancellationToken);

        if (claimed == 0)
        {
            throw new UnauthorizedException(InvalidRefreshTokenMessage);
        }

        var user = await _db.Users
            .Include(u => u.Department)
            .Include(u => u.Manager)
            .SingleOrDefaultAsync(u => u.Id == stored.UserId, cancellationToken)
            ?? throw new UnauthorizedException(InvalidRefreshTokenMessage);

        var response = await IssueTokensAsync(user, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return response;
    }

    private async Task<AuthResponse> IssueTokensAsync(User user, CancellationToken cancellationToken)
    {
        var (accessToken, expiresAtUtc) = _tokenService.GenerateAccessToken(user);
        var refreshToken = _tokenService.GenerateRefreshToken();

        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = _tokenService.HashToken(refreshToken),
            ExpiresAtUtc = _tokenService.GetRefreshTokenExpiryUtc(),
            IsRevoked = false
        });

        await _db.SaveChangesAsync(cancellationToken);

        return new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAtUtc,
            User = user.ToDto()
        };
    }
}
