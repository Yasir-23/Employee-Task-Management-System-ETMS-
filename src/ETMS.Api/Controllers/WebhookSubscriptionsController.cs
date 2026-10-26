using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Services.Webhooks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ETMS.Api.Controllers;

/// <summary>FR-HOOK-02 outbound subscription registry. Admin only (SRS §2 permission matrix, API.md §8.2–8.4).</summary>
[ApiController]
[Route("api/webhooks/subscriptions")]
[Authorize(Roles = "Admin")]
[Produces("application/json")]
public class WebhookSubscriptionsController : ControllerBase
{
    private readonly IWebhookSubscriptionService _subscriptionService;

    public WebhookSubscriptionsController(IWebhookSubscriptionService subscriptionService)
    {
        _subscriptionService = subscriptionService;
    }

    /// <summary>Registers a receiver. The generated signing secret is returned only in this response.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(WebhookSubscriptionCreatedDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WebhookSubscriptionCreatedDto>> Create(CreateWebhookSubscriptionRequest request, CancellationToken cancellationToken)
    {
        var created = await _subscriptionService.CreateAsync(request, cancellationToken);
        return Created($"/api/webhooks/subscriptions/{created.Id}", created);
    }

    /// <summary>Lists subscriptions with a masked secret and the outcome of their latest delivery.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<WebhookSubscriptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResult<WebhookSubscriptionDto>>> GetAll([FromQuery] WebhookSubscriptionFilterParams filter, CancellationToken cancellationToken)
    {
        return Ok(await _subscriptionService.GetSubscriptionsAsync(filter, cancellationToken));
    }

    /// <summary>Deactivates a subscription (soft delete, idempotent); its delivery history is kept.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _subscriptionService.DeactivateAsync(id, cancellationToken);
        return NoContent();
    }
}
