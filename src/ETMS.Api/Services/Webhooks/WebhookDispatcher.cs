using System.Text.Json;
using System.Text.Json.Serialization;
using ETMS.Api.Data;
using ETMS.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace ETMS.Api.Services.Webhooks;

public class WebhookDispatcher : IWebhookDispatcher
{
    // camelCase with enum names, matching the API's own JSON (API.md §8.5).
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AppDbContext _db;

    public WebhookDispatcher(AppDbContext db)
    {
        _db = db;
    }

    public async Task EnqueueAsync(string eventType, object data, CancellationToken cancellationToken = default)
    {
        var subscriptionIds = await _db.WebhookSubscriptions
            .AsNoTracking()
            .Where(s => s.IsActive && (s.EventType == eventType || s.EventType == WebhookEventTypes.Wildcard))
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        if (subscriptionIds.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        // Serialized once, so every subscription (and every retry) receives the identical body.
        var payload = JsonSerializer.Serialize(new WebhookEnvelope(Guid.NewGuid(), eventType, now, data), PayloadJsonOptions);

        foreach (var subscriptionId in subscriptionIds)
        {
            _db.WebhookDeliveryLogs.Add(new WebhookDeliveryLog
            {
                Id = Guid.NewGuid(),
                SubscriptionId = subscriptionId,
                EventType = eventType,
                Payload = payload,
                TimestampUtc = now,
                NextRetryUtc = now
            });
        }
    }
}
