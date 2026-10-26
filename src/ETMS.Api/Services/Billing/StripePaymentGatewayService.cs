using ETMS.Api.Common.Exceptions;
using ETMS.Api.Common.Settings;
using ETMS.Api.Models.Entities;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace ETMS.Api.Services.Billing;

/// <summary>
/// Stripe Checkout implementation (SDD §9.2). Uses an injected <see cref="IStripeClient"/> configured with the
/// API key, rather than the process-wide StripeConfiguration.ApiKey, so the key and HTTP client are scoped to DI.
/// </summary>
public class StripePaymentGatewayService : IPaymentGatewayService
{
    private const int MaxProductNameLength = 250;
    private const string TaskIdMetadataKey = "TaskId";
    private const string BillingIdMetadataKey = "BillingId";

    /// <summary>SRS FR-HOOK-01: signatures older than five minutes are rejected (replay protection).</summary>
    public const long WebhookToleranceSeconds = 300;

    private readonly IStripeClient _stripeClient;
    private readonly string _webhookSecret;
    private readonly ILogger<StripePaymentGatewayService> _logger;

    public StripePaymentGatewayService(IStripeClient stripeClient, IOptions<StripeSettings> stripeSettings, ILogger<StripePaymentGatewayService> logger)
    {
        _stripeClient = stripeClient;
        _webhookSecret = stripeSettings.Value.WebhookSecret;
        _logger = logger;
    }

    public PaymentWebhookEvent ParseWebhookEvent(string rawBody, string? signatureHeader)
    {
        // BR-08: reject before Stripe parses anything. A usable header has a numeric t= and at least one v1=.
        if (!IsWellFormedSignatureHeader(signatureHeader))
        {
            throw WebhookSignatureException.Malformed();
        }

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(rawBody, signatureHeader, _webhookSecret,
                tolerance: WebhookToleranceSeconds, throwOnApiVersionMismatch: false);
        }
        catch (StripeException ex)
        {
            // Signature mismatch or timestamp outside the tolerance; Stripe checks both before parsing the body.
            _logger.LogWarning("Rejected inbound Stripe webhook: {Reason}", ex.Message);
            throw WebhookSignatureException.Invalid();
        }
        catch (Newtonsoft.Json.JsonException)
        {
            // Authentic but not a parseable event.
            throw new BadRequestException("The webhook body is not a valid Stripe event.");
        }

        return stripeEvent.Data?.Object switch
        {
            Session session => new PaymentWebhookEvent(
                stripeEvent.Id, stripeEvent.Type, session.Id, session.PaymentIntentId, session.PaymentStatus,
                ParseBillingId(session.Metadata) ?? (Guid.TryParse(session.ClientReferenceId, out var referenceId) ? referenceId : null)),
            PaymentIntent intent => new PaymentWebhookEvent(
                stripeEvent.Id, stripeEvent.Type, SessionId: null, intent.Id, intent.Status, ParseBillingId(intent.Metadata)),
            _ => new PaymentWebhookEvent(stripeEvent.Id, stripeEvent.Type, null, null, null, null)
        };
    }

    private static bool IsWellFormedSignatureHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        var hasTimestamp = false;
        var hasSignature = false;
        foreach (var part in header.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || pair[1].Length == 0)
            {
                return false;
            }

            switch (pair[0].Trim())
            {
                case "t":
                    hasTimestamp = long.TryParse(pair[1], out _);
                    break;
                case "v1":
                    hasSignature = true;
                    break;
            }
        }

        return hasTimestamp && hasSignature;
    }

    private static Guid? ParseBillingId(IDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue(BillingIdMetadataKey, out var value) && Guid.TryParse(value, out var id)
            ? id
            : null;

    public async Task<CheckoutSessionResult> CreateCheckoutSessionAsync(TaskBilling billing, TaskItem task, string successUrl, string cancelUrl, string idempotencyKey, CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>
        {
            [TaskIdMetadataKey] = task.Id.ToString(),
            [BillingIdMetadataKey] = billing.Id.ToString()
        };

        var options = new SessionCreateOptions
        {
            Mode = "payment",
            ClientReferenceId = billing.Id.ToString(),
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            LineItems =
            [
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = billing.Currency,
                        UnitAmount = billing.AmountCents,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = task.Title.Length > MaxProductNameLength ? task.Title[..MaxProductNameLength] : task.Title
                        }
                    }
                }
            ],
            Metadata = metadata,
            // Copied onto the PaymentIntent so payment_intent.* webhooks (Milestone 8) can be correlated too.
            PaymentIntentData = new SessionPaymentIntentDataOptions { Metadata = new Dictionary<string, string>(metadata) }
        };

        try
        {
            var session = await new SessionService(_stripeClient).CreateAsync(options, new RequestOptions { IdempotencyKey = idempotencyKey }, ct);
            _logger.LogInformation("Created Stripe checkout session {SessionId} for billing {BillingId}.", session.Id, billing.Id);
            return Map(session);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe rejected checkout creation for billing {BillingId}: {StripeError}.", billing.Id, ex.StripeError?.Type);
            throw new BadGatewayException("The payment provider could not create a checkout session. Please try again later.");
        }
    }

    public async Task<CheckoutSessionResult> GetCheckoutSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            return Map(await new SessionService(_stripeClient).GetAsync(sessionId, cancellationToken: ct));
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe lookup of checkout session {SessionId} failed: {StripeError}.", sessionId, ex.StripeError?.Type);
            throw new BadGatewayException("The payment provider could not be reached. Please try again later.");
        }
    }

    private static CheckoutSessionResult Map(Session session) => new(
        session.Id,
        session.Url,
        session.Status switch
        {
            "open" => CheckoutSessionState.Open,
            "complete" => CheckoutSessionState.Complete,
            _ => CheckoutSessionState.Expired
        },
        session.ExpiresAt == default ? null : DateTime.SpecifyKind(session.ExpiresAt, DateTimeKind.Utc));
}
