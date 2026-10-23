using ETMS.Api.Services.Interfaces;

namespace ETMS.Api.Services.Implementations;

/// <summary>Development stand-in for a real mail transport: writes each message to the log.</summary>
public class LoggingEmailService : IEmailService
{
    private readonly ILogger<LoggingEmailService> _logger;

    public LoggingEmailService(ILogger<LoggingEmailService> logger)
    {
        _logger = logger;
    }

    public Task SendEmailAsync(string toEmail, string subject, string body, CancellationToken ct = default)
    {
        _logger.LogInformation("[SIMULATED EMAIL] To: {Email} | Subject: {Subject} | Body: {Body}", toEmail, subject, body);
        return Task.CompletedTask;
    }
}
