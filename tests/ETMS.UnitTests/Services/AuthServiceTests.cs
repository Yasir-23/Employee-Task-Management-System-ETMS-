using ETMS.Api.Common.Exceptions;
using ETMS.Api.Common.Settings;
using ETMS.Api.DTOs.Requests;
using ETMS.Api.Models.Entities;
using ETMS.Api.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ETMS.UnitTests.Services;

public sealed class AuthServiceTests : IDisposable
{
    private readonly TaskServiceTestContext _ctx = new();
    private readonly TokenService _tokenService;
    private readonly PasswordHasherService _passwordHasher = new();
    private readonly AuthService _service;

    public AuthServiceTests()
    {
        _tokenService = new TokenService(Options.Create(new JwtSettings
        {
            Secret = "unit-test-secret-key-that-is-at-least-32-bytes",
            Issuer = "ETMS.Api",
            Audience = "ETMS.Client",
            AccessTokenExpirationMinutes = 15,
            RefreshTokenExpirationDays = 7
        }));
        _service = new AuthService(_ctx.Db, _passwordHasher, _tokenService);
    }

    public void Dispose() => _ctx.Dispose();

    // ---------------------------------------------------------------------
    // FR-AUTH-05: revocation / logout
    // ---------------------------------------------------------------------

    [Fact]
    public async Task RevokeToken_ActiveToken_IsMarkedRevoked()
    {
        var (plainToken, stored) = AddRefreshToken(_ctx.Employee.Id);

        await _service.RevokeTokenAsync(plainToken);

        Assert.True((await _ctx.Db.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == stored.Id)).IsRevoked);
    }

    [Fact]
    public async Task RevokeToken_OnlyAffectsTheGivenToken()
    {
        var (plainToken, _) = AddRefreshToken(_ctx.Employee.Id);
        var (_, otherSession) = AddRefreshToken(_ctx.Employee.Id);

        await _service.RevokeTokenAsync(plainToken);

        Assert.False((await _ctx.Db.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == otherSession.Id)).IsRevoked);
    }

    [Fact]
    public async Task RevokeToken_UnknownToken_ThrowsBadRequest()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _service.RevokeTokenAsync(_tokenService.GenerateRefreshToken()));
    }

    [Fact]
    public async Task RevokeToken_AlreadyRevoked_IsIdempotent()
    {
        var (plainToken, stored) = AddRefreshToken(_ctx.Employee.Id, isRevoked: true);

        await _service.RevokeTokenAsync(plainToken);

        Assert.True((await _ctx.Db.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == stored.Id)).IsRevoked);
    }

    [Fact]
    public async Task Login_ThenRevoke_RevokesTheIssuedRefreshToken()
    {
        SetPassword(_ctx.Employee, "Correct-Horse-1");

        var auth = await _service.LoginAsync(new LoginRequest { Email = _ctx.Employee.Email, Password = "Correct-Horse-1" });
        await _service.RevokeTokenAsync(auth.RefreshToken);

        var stored = await _ctx.Db.RefreshTokens.AsNoTracking().SingleAsync();
        Assert.Equal(_tokenService.HashToken(auth.RefreshToken), stored.TokenHash);
        Assert.True(stored.IsRevoked);
    }

    // ---------------------------------------------------------------------
    // Login (FR-AUTH-01 to 03)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Login_WithValidCredentials_StoresOnlyTheTokenHash()
    {
        SetPassword(_ctx.Employee, "Correct-Horse-1");

        var auth = await _service.LoginAsync(new LoginRequest { Email = _ctx.Employee.Email.ToUpperInvariant(), Password = "Correct-Horse-1" });

        var stored = await _ctx.Db.RefreshTokens.AsNoTracking().SingleAsync();
        Assert.NotEqual(auth.RefreshToken, stored.TokenHash);
        Assert.Equal(_ctx.Employee.Id, stored.UserId);
        Assert.InRange(stored.ExpiresAtUtc, DateTime.UtcNow.AddDays(7).AddMinutes(-1), DateTime.UtcNow.AddDays(7));
        Assert.InRange(auth.ExpiresAt, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(15));
        Assert.Equal(_ctx.Employee.Id, auth.User.Id);
    }

    [Fact]
    public async Task Login_WithWrongPassword_ThrowsUnauthorized()
    {
        SetPassword(_ctx.Employee, "Correct-Horse-1");

        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _service.LoginAsync(new LoginRequest { Email = _ctx.Employee.Email, Password = "wrong" }));
        Assert.Empty(_ctx.Db.RefreshTokens);
    }

    private (string PlainToken, RefreshToken Stored) AddRefreshToken(Guid userId, bool isRevoked = false)
    {
        var plainToken = _tokenService.GenerateRefreshToken();
        var stored = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = _tokenService.HashToken(plainToken),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            IsRevoked = isRevoked
        };
        _ctx.Db.RefreshTokens.Add(stored);
        _ctx.Db.SaveChanges();
        return (plainToken, stored);
    }

    private void SetPassword(User user, string password)
    {
        user.PasswordHash = _passwordHasher.HashPassword(user, password);
        _ctx.Db.SaveChanges();
    }
}
