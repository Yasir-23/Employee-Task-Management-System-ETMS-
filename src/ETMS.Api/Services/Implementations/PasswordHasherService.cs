using ETMS.Api.Models.Entities;
using ETMS.Api.Services.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace ETMS.Api.Services.Implementations;

/// <summary>
/// ASP.NET Core Identity hasher: PBKDF2 with HMAC-SHA512, 128-bit salt, 100,000 iterations (NFR 5.1).
/// </summary>
public class PasswordHasherService : IPasswordHasherService
{
    private readonly PasswordHasher<User> _hasher = new();

    public string HashPassword(User user, string password) => _hasher.HashPassword(user, password);

    public bool VerifyPassword(User user, string hashedPassword, string providedPassword) =>
        _hasher.VerifyHashedPassword(user, hashedPassword, providedPassword) != PasswordVerificationResult.Failed;
}
