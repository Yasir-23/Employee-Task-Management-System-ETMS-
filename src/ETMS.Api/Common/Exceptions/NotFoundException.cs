namespace ETMS.Api.Common.Exceptions;

public class NotFoundException : AppException
{
    public NotFoundException(string message)
        : base("Not Found", message, StatusCodes.Status404NotFound)
    {
    }
}
