using System.Globalization;
using ETMS.Api.Data;
using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ETMS.Api.Services.Implementations;

public class NotificationService : INotificationService
{
    private static readonly TimeSpan ReminderWindowStart = TimeSpan.FromHours(23);
    private static readonly TimeSpan ReminderWindowEnd = TimeSpan.FromHours(24);

    private readonly AppDbContext _db;
    private readonly IEmailService _emailService;

    public NotificationService(AppDbContext db, IEmailService emailService)
    {
        _db = db;
        _emailService = emailService;
    }

    public async Task<int> SendDueDateRemindersAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var windowStart = now.Add(ReminderWindowStart);
        var windowEnd = now.Add(ReminderWindowEnd);

        var tasks = await _db.Tasks
            .Include(t => t.Assignee)
            .Where(t => t.Status != TaskLifecycleStatus.Completed
                && !t.ReminderSent
                && t.AssigneeId != null
                && t.DueDateUtc >= windowStart
                && t.DueDateUtc <= windowEnd)
            .ToListAsync(cancellationToken);

        foreach (var task in tasks)
        {
            await _emailService.SendEmailAsync(
                task.Assignee!.Email,
                $"Reminder: \"{task.Title}\" is due in 24 hours",
                $"Hi {task.Assignee.FullName}, your task \"{task.Title}\" is due on {FormatUtc(task.DueDateUtc)}. Current status: {task.Status}.",
                cancellationToken);

            // Persist per task so a failure mid-batch does not cause already-sent emails to be resent.
            task.ReminderSent = true;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return tasks.Count;
    }

    public async Task<int> SendOverdueAlertsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var tasks = await _db.Tasks
            .Include(t => t.Creator)
            .Include(t => t.Assignee)
            .Where(t => t.Status != TaskLifecycleStatus.Completed
                && !t.OverdueAlertSent
                && t.DueDateUtc < now)
            .ToListAsync(cancellationToken);

        foreach (var task in tasks)
        {
            var assignee = task.Assignee?.FullName ?? "Unassigned";

            await _emailService.SendEmailAsync(
                task.Creator.Email,
                $"Overdue: \"{task.Title}\"",
                $"Hi {task.Creator.FullName}, the task \"{task.Title}\" assigned to {assignee} was due on {FormatUtc(task.DueDateUtc)} and is still {task.Status}.",
                cancellationToken);

            task.OverdueAlertSent = true;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return tasks.Count;
    }

    private static string FormatUtc(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
