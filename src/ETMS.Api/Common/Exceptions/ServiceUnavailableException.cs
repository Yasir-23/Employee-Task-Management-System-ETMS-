namespace ETMS.Api.Common.Exceptions;

/// <summary>A dependency is temporarily refusing calls (e.g. an open circuit breaker). Sent with Retry-After.</summary>
public class ServiceUnavailableException : AppException
{
    public ServiceUnavailableException(string message, TimeSpan retryAfter)
        : base("Service Unavailable", message, StatusCodes.Status503ServiceUnavailable)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan RetryAfter { get; }
}
