namespace ETMS.Api.Common.Exceptions;

public class ConflictException : AppException
{
    public ConflictException(string message)
        : base("Conflict", message, StatusCodes.Status409Conflict)
    {
    }
}
