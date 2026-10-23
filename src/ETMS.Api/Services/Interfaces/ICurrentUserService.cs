namespace ETMS.Api.Services.Interfaces;

public interface ICurrentUserService
{
    /// <summary>The authenticated user's id, or null outside an authenticated HTTP request.</summary>
    Guid? UserId { get; }
}
