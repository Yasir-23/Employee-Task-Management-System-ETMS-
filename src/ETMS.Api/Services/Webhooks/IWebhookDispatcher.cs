namespace ETMS.Api.Services.Webhooks;

/// <summary>SDD §10.2: queues outbound events in the transactional outbox.</summary>
public interface IWebhookDispatcher
{
    /// <summary>
    /// Adds one WebhookDeliveryLog per matching active subscription to the current DbContext.
    /// Does NOT call SaveChanges: rows commit atomically with the caller's task mutation (outbox).
    /// </summary>
    Task EnqueueAsync(string eventType, object data, CancellationToken cancellationToken = default);
}
