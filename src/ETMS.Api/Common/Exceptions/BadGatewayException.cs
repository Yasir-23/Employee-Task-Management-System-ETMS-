namespace ETMS.Api.Common.Exceptions;

/// <summary>An upstream dependency (e.g. Stripe) failed or returned an error.</summary>
public class BadGatewayException : AppException
{
    public BadGatewayException(string message)
        : base("Bad Gateway", message, StatusCodes.Status502BadGateway)
    {
    }
}
