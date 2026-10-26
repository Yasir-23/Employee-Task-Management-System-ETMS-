using ETMS.Api.Common.Settings;
using ETMS.Api.Services.Webhooks;
using Microsoft.Extensions.Options;

namespace ETMS.Api.BackgroundWorkers;

/// <summary>
/// SDD §10.3: relays the webhook outbox every few seconds. Each cycle runs in its own DI scope so it gets a
/// fresh AppDbContext.
/// </summary>
public class WebhookDeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebhookDeliveryWorker> _logger;
    private readonly WebhookSettings _settings;

    public WebhookDeliveryWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<WebhookDeliveryWorker> logger,
        IOptions<WebhookSettings> settings)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Webhook delivery worker is disabled.");
            return;
        }

        var interval = TimeSpan.FromSeconds(_settings.PollIntervalSeconds);
        _logger.LogInformation("Webhook delivery worker started; polling every {Interval}.", interval);

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
            int attempted;
            do
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var deliveries = scope.ServiceProvider.GetRequiredService<IWebhookDeliveryService>();
                attempted = await deliveries.DeliverDueAsync(stoppingToken);
            }
            // A full batch means more rows may be due; drain them before sleeping.
            while (attempted >= _settings.BatchSize && !stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down; claimed rows become due again when their lease expires.
        }
        catch (Exception ex)
        {
            // Keep the worker alive; undelivered rows stay in the outbox for the next cycle.
            _logger.LogError(ex, "Webhook delivery cycle failed.");
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
