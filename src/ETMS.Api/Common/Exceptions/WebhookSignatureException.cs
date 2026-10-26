namespace ETMS.Api.Common.Exceptions;

/// <summary>BR-08: an inbound webhook failed authentication and was rejected before any processing.</summary>
public class WebhookSignatureException : AppException
{
    private WebhookSignatureException(string title, string message, int statusCode)
        : base(title, message, statusCode)
    {
    }

    /// <summary>400: the signature header is missing or not in the "t=...,v1=..." format.</summary>
    public static WebhookSignatureException Malformed() => new(
        "Bad Request", "The Stripe-Signature header is missing or malformed.", StatusCodes.Status400BadRequest);

    /// <summary>401: the signature does not match the body, or its timestamp is outside the tolerance.</summary>
    public static WebhookSignatureException Invalid() => new(
        "Unauthorized", "The webhook signature could not be verified.", StatusCodes.Status401Unauthorized);
}
