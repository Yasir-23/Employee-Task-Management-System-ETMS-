namespace ETMS.Api.Common.Exceptions;

public class PayloadTooLargeException : AppException
{
    public PayloadTooLargeException(string message)
        : base("Payload Too Large", message, StatusCodes.Status413PayloadTooLarge)
    {
    }
}
