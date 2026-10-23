using System.Linq.Expressions;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Models.Entities;

namespace ETMS.Api.Common.Mappings;

public static class UserMappings
{
    // Translatable to SQL for list projections; null-safe so it can also run in memory.
    public static readonly Expression<Func<User, UserDto>> ToDtoExpression = u => new UserDto
    {
        Id = u.Id,
        FullName = u.FullName,
        Email = u.Email,
        Role = u.Role,
        DepartmentId = u.DepartmentId,
        DepartmentName = u.Department != null ? u.Department.Name : null,
        ManagerId = u.ManagerId,
        ManagerName = u.Manager != null ? u.Manager.FullName : null
    };

    private static readonly Func<User, UserDto> ToDtoCompiled = ToDtoExpression.Compile();

    /// <summary>Maps a user whose Department and Manager navigations are loaded (when set).</summary>
    public static UserDto ToDto(this User user) => ToDtoCompiled(user);
}
