namespace ETMS.Api.Services.Interfaces;

public interface IEmailService
{
    Task SendEmailAsync(string toEmail, string subject, string body, CancellationToken ct = default);
}
