namespace ETMS.Api.Common.Exceptions;

public class BadRequestException : AppException
{
    public BadRequestException(string message)
        : base("Bad Request", message, StatusCodes.Status400BadRequest)
    {
    }
}
