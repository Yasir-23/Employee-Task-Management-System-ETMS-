using ETMS.Api.Common.Extensions;
using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Services.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ETMS.Api.Controllers;

[ApiController]
[Route("api/tasks/{id:guid}/billing")]
[Authorize]
[Produces("application/json")]
public class TaskBillingController : ControllerBase
{
    private readonly IBillingService _billingService;

    public TaskBillingController(IBillingService billingService)
    {
        _billingService = billingService;
    }

    /// <summary>Makes a task billable or changes its amount while Unpaid/Failed (FR-PAY-01).</summary>
    [HttpPut]
    [Authorize(Roles = "Manager,Admin")]
    [ProducesResponseType(typeof(TaskBillingResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(TaskBillingResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TaskBillingResponseDto>> SetBilling(Guid id, SetBillingRequest request, CancellationToken cancellationToken)
    {
        var result = await _billingService.SetTaskBillingAsync(User.GetUserId(), User.GetRole(), id, request, cancellationToken);
        return result.Created
            ? CreatedAtAction(nameof(GetBilling), new { id }, result.Billing)
            : Ok(result.Billing);
    }

    /// <summary>
    /// Starts a Stripe Checkout session (BR-07). Requires an Idempotency-Key UUID header; retries return the
    /// same open session (200) instead of creating another (201).
    /// </summary>
    /// <param name="id">Task ID.</param>
    /// <param name="idempotencyKey">Client-generated UUID, reused verbatim when retrying the same checkout.</param>
    /// <param name="request">Optional return URLs; defaults come from the Stripe settings.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpPost("checkout")]
    [ProducesResponseType(typeof(CheckoutSessionResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(CheckoutSessionResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<CheckoutSessionResponseDto>> CreateCheckout(
        Guid id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateCheckoutRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _billingService.CreateCheckoutSessionAsync(User.GetUserId(), User.GetRole(), id, request, idempotencyKey, cancellationToken);
        return result.IsNewSession
            ? CreatedAtAction(nameof(GetBilling), new { id }, result.Session)
            : Ok(result.Session);
    }

    /// <summary>Billing amount, status and payment time for a task the caller can see.</summary>
    [HttpGet]
    [HttpGet("status")] // API.md §7.3 path
    [ProducesResponseType(typeof(TaskBillingResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TaskBillingResponseDto>> GetBilling(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await _billingService.GetBillingStatusAsync(User.GetUserId(), User.GetRole(), id, cancellationToken));
    }
}
