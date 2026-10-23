namespace ETMS.Api.Common.Exceptions;

/// <summary>
/// Base type for expected business/application failures. The global exception middleware
/// translates these into RFC 7807 ProblemDetails responses using <see cref="StatusCode"/>.
/// </summary>
public abstract class AppException : Exception
{
    protected AppException(string title, string message, int statusCode) : base(message)
    {
        Title = title;
        StatusCode = statusCode;
    }

    public string Title { get; }
    public int StatusCode { get; }
}
