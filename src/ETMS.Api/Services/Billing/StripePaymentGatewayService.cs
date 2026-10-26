using ETMS.Api.Common.Exceptions;
using ETMS.Api.Models.Entities;
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

    private readonly IStripeClient _stripeClient;
    private readonly ILogger<StripePaymentGatewayService> _logger;

    public StripePaymentGatewayService(IStripeClient stripeClient, ILogger<StripePaymentGatewayService> logger)
    {
        _stripeClient = stripeClient;
        _logger = logger;
    }

    public async Task<CheckoutSessionResult> CreateCheckoutSessionAsync(TaskBilling billing, TaskItem task, string successUrl, string cancelUrl, string idempotencyKey, CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>
        {
            ["TaskId"] = task.Id.ToString(),
            ["BillingId"] = billing.Id.ToString()
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
