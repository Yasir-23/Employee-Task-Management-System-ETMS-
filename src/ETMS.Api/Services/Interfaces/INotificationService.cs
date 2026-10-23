namespace ETMS.Api.Services.Interfaces;

public interface INotificationService
{
    /// <summary>FR-NOTIF-03: emails assignees of active tasks due in [now + 23h, now + 24h]. Returns emails sent.</summary>
    Task<int> SendDueDateRemindersAsync(CancellationToken cancellationToken = default);

    /// <summary>FR-NOTIF-04: emails creators of active tasks that are past due. Returns emails sent.</summary>
    Task<int> SendOverdueAlertsAsync(CancellationToken cancellationToken = default);
}
