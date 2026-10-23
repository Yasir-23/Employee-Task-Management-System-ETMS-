using ETMS.Api.Common.Exceptions;
using ETMS.Api.Models.Entities;
using ETMS.Api.Models.Enums;

namespace ETMS.Api.Common.Extensions;

public static class TaskQueryExtensions
{
    /// <summary>
    /// Restricts tasks to those the user may see (API.md §3.2): Admin sees all, a Manager sees tasks
    /// they created or that are assigned to their direct reports, an Employee sees tasks assigned to them.
    /// </summary>
    public static IQueryable<TaskItem> ScopeToUser(this IQueryable<TaskItem> query, Guid currentUserId, string role) =>
        ParseRole(role) switch
        {
            Role.Admin => query,
            Role.Manager => query.Where(t =>
                t.CreatedByUserId == currentUserId
                || (t.Assignee != null && t.Assignee.ManagerId == currentUserId)),
            Role.Employee => query.Where(t => t.AssigneeId == currentUserId),
            _ => throw new ForbiddenException("Unrecognized role.")
        };

    public static Role ParseRole(string role) =>
        Enum.TryParse<Role>(role, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ForbiddenException("Unrecognized role.");
}
