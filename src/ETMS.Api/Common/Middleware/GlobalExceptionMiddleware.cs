using ETMS.Api.Common.Exceptions;
using Microsoft.AspNetCore.Mvc;

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
            await problemDetailsService.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = problem,
                Exception = ex
            });
        }
    }
}
