using ETMS.Api.Common.Settings;
using ETMS.Api.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace ETMS.Api.BackgroundWorkers;

/// <summary>
/// SDD §6: periodically dispatches 24-hour due-date reminders and overdue escalations.
/// Each cycle runs in its own DI scope so it gets a fresh AppDbContext.
/// </summary>
public class TaskNotificationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TaskNotificationWorker> _logger;
    private readonly NotificationWorkerSettings _settings;

    public TaskNotificationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<TaskNotificationWorker> logger,
        IOptions<NotificationWorkerSettings> settings)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Task notification worker is disabled.");
            return;
        }

        var interval = TimeSpan.FromSeconds(_settings.IntervalSeconds);
        _logger.LogInformation("Task notification worker started; running every {Interval}.", interval);

        using var timer = new PeriodicTimer(interval);
        do
        {
            await RunCycleAsync(stoppingToken);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

            var reminders = await notifications.SendDueDateRemindersAsync(stoppingToken);
            var overdueAlerts = await notifications.SendOverdueAlertsAsync(stoppingToken);

            if (reminders > 0 || overdueAlerts > 0)
            {
                _logger.LogInformation("Notification cycle sent {Reminders} reminder(s) and {OverdueAlerts} overdue alert(s).",
                    reminders, overdueAlerts);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down.
        }
        catch (Exception ex)
        {
            // Keep the worker alive; the next cycle retries anything not yet flagged as sent.
            _logger.LogError(ex, "Task notification cycle failed.");
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
