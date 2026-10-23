using ETMS.Api.Common.Exceptions;
using ETMS.Api.Data;
using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Models.Entities;
using ETMS.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ETMS.Api.Services.Implementations;

public class DepartmentService : IDepartmentService
{
    private readonly AppDbContext _db;

    public DepartmentService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<DepartmentDto> CreateDepartmentAsync(CreateDepartmentRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim();
        if (name.Length == 0)
        {
            throw new BadRequestException("Department name is required.");
        }

        var normalizedName = name.ToLower();
        var exists = await _db.Departments.AnyAsync(d => d.Name.ToLower() == normalizedName, cancellationToken);
        if (exists)
        {
            throw new ConflictException($"A department named '{name}' already exists.");
        }

        var department = new Department
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.Departments.Add(department);
        await _db.SaveChangesAsync(cancellationToken);

        return ToDto(department);
    }

    public async Task<IReadOnlyList<DepartmentDto>> GetAllDepartmentsAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Departments
            .AsNoTracking()
            .OrderBy(d => d.Name)
            .Select(d => new DepartmentDto
            {
                Id = d.Id,
                Name = d.Name,
                CreatedAtUtc = d.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);
    }

    private static DepartmentDto ToDto(Department department) => new()
    {
        Id = department.Id,
        Name = department.Name,
        CreatedAtUtc = department.CreatedAtUtc
    };
}
