using ETMS.Api.Common.Exceptions;
using ETMS.Api.Common.Mappings;
using ETMS.Api.Data;
using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Models.Entities;
using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ETMS.Api.Services.Implementations;

public class EmployeeService : IEmployeeService
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasherService _passwordHasher;

    public EmployeeService(AppDbContext db, IPasswordHasherService passwordHasher)
    {
        _db = db;
        _passwordHasher = passwordHasher;
    }

    public async Task<UserDto> CreateEmployeeAsync(CreateEmployeeRequest request, CancellationToken cancellationToken = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        if (await _db.Users.AnyAsync(u => u.Email == email, cancellationToken))
        {
            throw new BadRequestException($"A user with email '{email}' already exists.");
        }

        Department? department = null;
        if (request.DepartmentId is { } departmentId)
        {
            department = await _db.Departments.FindAsync(new object[] { departmentId }, cancellationToken)
                ?? throw new BadRequestException($"Department '{departmentId}' does not exist.");
        }
        else if (request.Role != Role.Admin)
        {
            // SDD 3.2: DepartmentId is only nullable for administrators.
            throw new BadRequestException("DepartmentId is required for Manager and Employee users.");
        }

        User? manager = null;
        if (request.ManagerId is { } managerId)
        {
            manager = await _db.Users.FindAsync(new object[] { managerId }, cancellationToken);
            if (manager is null || manager.Role != Role.Manager)
            {
                throw new BadRequestException("ManagerId must reference an existing user with the Manager role.");
            }
        }
        else if (request.Role == Role.Employee)
        {
            // FR-ORG-03: every Employee must report to a Manager.
            throw new BadRequestException("ManagerId is required for users with the Employee role.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            FullName = request.FullName.Trim(),
            Email = email,
            Role = request.Role,
            DepartmentId = department?.Id,
            ManagerId = manager?.Id,
            CreatedAtUtc = DateTime.UtcNow,
            Department = department,
            Manager = manager
        };
        user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);

        return user.ToDto();
    }

    public async Task<PagedResult<EmployeeListDto>> GetEmployeesAsync(EmployeeFilterParams filter, CancellationToken cancellationToken = default)
    {
        var query = _db.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pattern = $"%{EscapeLikePattern(filter.Search.Trim())}%";
            query = query.Where(u =>
                EF.Functions.ILike(u.FullName, pattern, LikeEscapeCharacter)
                || EF.Functions.ILike(u.Email, pattern, LikeEscapeCharacter));
        }

        if (filter.DepartmentId is { } departmentId)
        {
            query = query.Where(u => u.DepartmentId == departmentId);
        }

        if (filter.Role is { } role)
        {
            query = query.Where(u => u.Role == role);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        // Deterministic ordering: Id breaks ties between identical names.
        var items = await query
            .OrderBy(u => u.FullName)
            .ThenBy(u => u.Id)
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(u => new EmployeeListDto
            {
                Id = u.Id,
                FullName = u.FullName,
                Email = u.Email,
                Role = u.Role,
                Department = u.Department != null ? new DepartmentSummaryDto(u.Department.Id, u.Department.Name) : null,
                Manager = u.Manager != null ? new ManagerSummaryDto(u.Manager.Id, u.Manager.FullName) : null,
                ActiveTasksCount = u.AssignedTasks.Count(t => t.Status != TaskLifecycleStatus.Completed)
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<EmployeeListDto>
        {
            Items = items,
            PageNumber = filter.PageNumber,
            PageSize = filter.PageSize,
            TotalCount = totalCount
        };
    }

    // Npgsql emits ESCAPE '' unless an escape character is passed explicitly.
    private const string LikeEscapeCharacter = "\\";

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
