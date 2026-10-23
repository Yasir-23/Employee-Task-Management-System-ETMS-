using ETMS.Api.Models.Entities;

namespace ETMS.Api.Services.Interfaces;

public interface IPasswordHasherService
{
    string HashPassword(User user, string password);
    bool VerifyPassword(User user, string hashedPassword, string providedPassword);
}
