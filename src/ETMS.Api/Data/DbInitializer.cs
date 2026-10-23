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

    // Demo accounts (Development only) mirror the examples in docs/API.md.
    private const string DemoDepartmentName = "Software Engineering";
    private const string DemoManagerEmail = "manager.sarah@company.com";
    private const string DemoManagerPassword = "SecurePassword123!";
    private const string DemoEmployeeEmail = "alex.mercer@company.com";
    private const string DemoEmployeePassword = "InitialPassword123!";

    /// <summary>
    /// Applies pending migrations, then, when the Users table is empty, seeds a default department and
    /// Admin account, plus a demo Manager and Employee when <paramref name="includeDemoUsers"/> is set.
    /// </summary>
    public static async Task SeedAsync(IServiceProvider services, bool includeDemoUsers, CancellationToken cancellationToken = default)
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

        var executive = await GetOrAddDepartmentAsync(db, DefaultDepartmentName, now, cancellationToken);
        AddUser(db, passwordHasher, "System Admin", DefaultAdminEmail, DefaultAdminPassword, Role.Admin, executive.Id, null, now);

        if (includeDemoUsers)
        {
            var engineering = await GetOrAddDepartmentAsync(db, DemoDepartmentName, now, cancellationToken);
            var manager = AddUser(db, passwordHasher, "Sarah Jenkins", DemoManagerEmail, DemoManagerPassword,
                Role.Manager, engineering.Id, null, now);
            AddUser(db, passwordHasher, "Alex Mercer", DemoEmployeeEmail, DemoEmployeePassword,
                Role.Employee, engineering.Id, manager.Id, now);
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Seeded default admin '{Email}'{Demo}.", DefaultAdminEmail,
            includeDemoUsers ? $" and demo users '{DemoManagerEmail}', '{DemoEmployeeEmail}'" : string.Empty);
    }

    private static async Task<Department> GetOrAddDepartmentAsync(AppDbContext db, string name, DateTime now, CancellationToken cancellationToken)
    {
        var department = await db.Departments.SingleOrDefaultAsync(d => d.Name == name, cancellationToken);
        if (department is null)
        {
            department = new Department { Id = Guid.NewGuid(), Name = name, CreatedAtUtc = now };
            db.Departments.Add(department);
        }

        return department;
    }

    private static User AddUser(
        AppDbContext db,
        IPasswordHasherService passwordHasher,
        string fullName,
        string email,
        string password,
        Role role,
        Guid departmentId,
        Guid? managerId,
        DateTime now)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            FullName = fullName,
            Email = email,
            Role = role,
            DepartmentId = departmentId,
            ManagerId = managerId,
            CreatedAtUtc = now
        };
        user.PasswordHash = passwordHasher.HashPassword(user, password);
        db.Users.Add(user);
        return user;
    }
}
