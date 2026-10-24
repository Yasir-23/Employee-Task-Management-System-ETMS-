namespace ETMS.Api.Common.Exceptions;

public class UnsupportedMediaTypeException : AppException
{
    public UnsupportedMediaTypeException(string message)
        : base("Unsupported Media Type", message, StatusCodes.Status415UnsupportedMediaType)
    {
    }
}
