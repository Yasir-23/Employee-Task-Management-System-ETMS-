using ETMS.Api.Common.Exceptions;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ETMS.Api.Common.Filters;

/// <summary>
/// Reads the form before model binding so a body over [RequestSizeLimit] surfaces as 413 Payload Too Large.
/// Otherwise MVC records the read failure as a model-state error and the request gets a generic 400.
/// The parsed form is cached on the request, so model binding does not read the body again.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RejectOversizedFormAttribute : Attribute, IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (request.HasFormContentType)
        {
            try
            {
                await request.ReadFormAsync(context.HttpContext.RequestAborted);
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                throw new PayloadTooLargeException("BR-06: the request body exceeds the maximum upload size (10 MB per file).");
            }
        }

        await next();
    }
}
