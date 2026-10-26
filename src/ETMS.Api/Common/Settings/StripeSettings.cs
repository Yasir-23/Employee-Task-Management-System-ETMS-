using System.ComponentModel.DataAnnotations;

namespace ETMS.Api.Common.Settings;

public class StripeSettings
{
    public const string SectionName = "Stripe";

    /// <summary>Secret API key. Test-mode (sk_test_...) outside Production; supply via env Stripe__ApiKey.</summary>
    [Required]
    public string ApiKey { get; init; } = string.Empty;

    [Required]
    public string PublishableKey { get; init; } = string.Empty;

    /// <summary>Endpoint signing secret for inbound webhooks (Milestone 8).</summary>
    [Required]
    public string WebhookSecret { get; init; } = string.Empty;

    /// <summary>Default return URL after payment. Stripe substitutes {CHECKOUT_SESSION_ID}.</summary>
    [Required, Url]
    public string SuccessUrl { get; init; } = string.Empty;

    /// <summary>Default return URL when the payer abandons checkout.</summary>
    [Required, Url]
    public string CancelUrl { get; init; } = string.Empty;
}
