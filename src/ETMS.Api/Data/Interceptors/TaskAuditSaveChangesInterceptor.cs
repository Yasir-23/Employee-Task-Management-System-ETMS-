using System.Globalization;
using ETMS.Api.Models.Entities;
using ETMS.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ETMS.Api.Data.Interceptors;

/// <summary>
/// SDD §5 / FR-TASK-04: records an <see cref="AuditLog"/> row for every change to a task's
/// Status, AssigneeId or DueDateUtc, inside the same SaveChanges (and therefore the same transaction).
/// </summary>
public class TaskAuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private static readonly string[] AuditedProperties =
    {
        nameof(TaskItem.Status),
        nameof(TaskItem.AssigneeId),
        nameof(TaskItem.DueDateUtc)
    };

    private readonly ICurrentUserService _currentUser;

    public TaskAuditSaveChangesInterceptor(ICurrentUserService currentUser)
    {
        _currentUser = currentUser;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        AddAuditLogs(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        AddAuditLogs(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void AddAuditLogs(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var auditLogs = new List<AuditLog>();

        foreach (var entry in context.ChangeTracker.Entries<TaskItem>().Where(e => e.State == EntityState.Modified))
        {
            foreach (var propertyName in AuditedProperties)
            {
                var property = entry.Property(propertyName);
                if (!property.IsModified || Equals(property.OriginalValue, property.CurrentValue))
                {
                    continue;
                }

                auditLogs.Add(new AuditLog
                {
                    TaskId = entry.Entity.Id,
                    ChangedByUserId = _currentUser.UserId
                        ?? throw new InvalidOperationException(
                            $"Cannot change Task.{propertyName} without an authenticated user to attribute the audit entry to."),
                    PropertyName = property.Metadata.Name,
                    OldValue = FormatValue(property.OriginalValue),
                    NewValue = FormatValue(property.CurrentValue),
                    TimestampUtc = now
                });
            }
        }

        if (auditLogs.Count > 0)
        {
            context.Set<AuditLog>().AddRange(auditLogs);
        }
    }

    private static string? FormatValue(object? value) => value switch
    {
        null => null,
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}
