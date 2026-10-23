using System.Security.Claims;
using ETMS.Api.Services.Interfaces;

namespace ETMS.Api.Services.Implementations;

public class HttpContextCurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? UserId =>
        Guid.TryParse(_httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : null;
}
