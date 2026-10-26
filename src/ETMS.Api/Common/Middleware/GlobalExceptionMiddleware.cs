using ETMS.Api.Common.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace ETMS.Api.Common.Middleware;

/// <summary>
/// Converts exceptions into RFC 7807 ProblemDetails responses. Known <see cref="AppException"/>s
/// map to their own status code; anything else becomes a sanitized 500.
/// </summary>
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetailsService)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            ProblemDetails problem;

            if (ex is AppException appException)
            {
                _logger.LogInformation("Request {Method} {Path} rejected with {StatusCode}: {Message}",
                    context.Request.Method, context.Request.Path, appException.StatusCode, appException.Message);

                problem = new ProblemDetails
                {
                    Status = appException.StatusCode,
                    Title = appException.Title,
                    Detail = appException.Message
                };
            }
            else if (ex is BadHttpRequestException badRequest)
            {
                // Raised by the server itself, e.g. 413 when a body exceeds [RequestSizeLimit].
                _logger.LogInformation("Request {Method} {Path} rejected by the server with {StatusCode}: {Message}",
                    context.Request.Method, context.Request.Path, badRequest.StatusCode, badRequest.Message);

                problem = new ProblemDetails
                {
                    Status = badRequest.StatusCode,
                    Title = ReasonPhrases.GetReasonPhrase(badRequest.StatusCode),
                    Detail = badRequest.Message
                };
            }
            else
            {
                _logger.LogError(ex, "Unhandled exception processing {Method} {Path}",
                    context.Request.Method, context.Request.Path);

                problem = new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Internal Server Error",
                    // Never leak exception details outside Development.
                    Detail = _environment.IsDevelopment()
                        ? ex.Message
                        : "An unexpected error occurred. Please try again later."
                };
            }

            context.Response.StatusCode = problem.Status.Value;
            if (ex is ServiceUnavailableException unavailable)
            {
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(unavailable.RetryAfter.TotalSeconds)).ToString();
            }

            await problemDetailsService.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = problem,
                Exception = ex
            });
        }
    }
}
