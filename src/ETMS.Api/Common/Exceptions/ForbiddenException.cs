namespace ETMS.Api.Common.Exceptions;

public class ForbiddenException : AppException
{
    public ForbiddenException(string message)
        : base("Forbidden", message, StatusCodes.Status403Forbidden)
    {
    }
}
