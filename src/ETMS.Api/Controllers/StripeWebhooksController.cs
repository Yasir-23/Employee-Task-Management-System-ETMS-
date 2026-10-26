using System.Text;
using ETMS.Api.Common.Exceptions;
using ETMS.Api.Services.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ETMS.Api.Controllers;

/// <summary>
/// FR-HOOK-01 inbound Stripe events. Authenticated by signature only (BR-08), so it is the one controller that
/// calls the payment gateway directly (SDD §10.1).
/// </summary>
[ApiController]
[Route("api/webhooks/stripe")]
[AllowAnonymous]
[Produces("application/json")]
public class StripeWebhooksController : ControllerBase
{
    public const int MaxBodyBytes = 256 * 1024;
    public const string SignatureHeader = "Stripe-Signature";

    private readonly IPaymentGatewayService _paymentGateway;
    private readonly IBillingService _billingService;

    public StripeWebhooksController(IPaymentGatewayService paymentGateway, IBillingService billingService)
    {
        _paymentGateway = paymentGateway;
        _billingService = billingService;
    }

    /// <summary>
    /// Receives a Stripe event. The body is read raw (no model binding) so the signature is checked against the
    /// exact bytes Stripe signed; nothing is parsed or stored until that check passes.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        // Kestrel enforces [RequestSizeLimit] while streaming; this rejects a declared oversize body up front.
        if (Request.ContentLength > MaxBodyBytes)
        {
            throw new PayloadTooLargeException($"Webhook bodies are limited to {MaxBodyBytes / 1024} KB.");
        }

        string rawBody;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            rawBody = await reader.ReadToEndAsync(cancellationToken);
        }

        // BR-08: throws 400/401 before any deserialization or database access.
        var webhookEvent = _paymentGateway.ParseWebhookEvent(rawBody, Request.Headers[SignatureHeader].FirstOrDefault());

        await _billingService.HandleWebhookEventAsync(webhookEvent, cancellationToken);
        return Ok(new { received = true });
    }
}
