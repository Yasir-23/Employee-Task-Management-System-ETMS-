using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;

namespace ETMS.Api.Services.Webhooks;

/// <summary>FR-HOOK-02: outbound subscription registry.</summary>
public interface IWebhookSubscriptionService
{
    /// <summary>Registers a receiver and generates its signing secret, which is returned only here.</summary>
    Task<WebhookSubscriptionCreatedDto> CreateAsync(CreateWebhookSubscriptionRequest request, CancellationToken cancellationToken = default);

    Task<PagedResult<WebhookSubscriptionDto>> GetSubscriptionsAsync(WebhookSubscriptionFilterParams filter, CancellationToken cancellationToken = default);

    /// <summary>Soft delete (IsActive = false); idempotent. Pending deliveries are no longer attempted.</summary>
    Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default);
}
