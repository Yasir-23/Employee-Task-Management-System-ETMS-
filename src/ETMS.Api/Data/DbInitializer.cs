using ETMS.Api.Models.Entities;
using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ETMS.Api.Data;

public static class DbInitializer
{
    private const string DefaultDepartmentName = "Executive";
    private const string DefaultAdminEmail = "admin@company.com";
    private const string DefaultAdminPassword = "Admin@123456";

    /// <summary>
    /// Applies pending migrations, then seeds a default department and Admin account
    /// when the Users table is empty.
    /// </summary>
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DbInitializer));

        await db.Database.MigrateAsync(cancellationToken);

        if (await db.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;

        var department = await db.Departments.SingleOrDefaultAsync(d => d.Name == DefaultDepartmentName, cancellationToken);
        if (department is null)
        {
            department = new Department
            {
                Id = Guid.NewGuid(),
                Name = DefaultDepartmentName,
                CreatedAtUtc = now
            };
            db.Departments.Add(department);
        }

        var admin = new User
        {
            Id = Guid.NewGuid(),
            FullName = "System Admin",
            Email = DefaultAdminEmail,
            Role = Role.Admin,
            DepartmentId = department.Id,
            CreatedAtUtc = now
        };
        admin.PasswordHash = passwordHasher.HashPassword(admin, DefaultAdminPassword);
        db.Users.Add(admin);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Seeded default department '{Department}' and admin user '{Email}'.",
            DefaultDepartmentName, DefaultAdminEmail);
    }
}
