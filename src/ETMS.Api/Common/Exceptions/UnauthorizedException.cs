namespace ETMS.Api.Common.Exceptions;

public class UnauthorizedException : AppException
{
    public UnauthorizedException(string message)
        : base("Unauthorized", message, StatusCodes.Status401Unauthorized)
    {
    }
}
