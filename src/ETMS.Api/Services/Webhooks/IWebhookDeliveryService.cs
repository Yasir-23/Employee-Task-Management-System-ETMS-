namespace ETMS.Api.Services.Webhooks;

/// <summary>FR-HOOK-04: relays due outbox rows to their receivers.</summary>
public interface IWebhookDeliveryService
{
    /// <summary>Attempts one batch of due deliveries.</summary>
    /// <returns>The number of deliveries attempted.</returns>
    Task<int> DeliverDueAsync(CancellationToken cancellationToken = default);
}
