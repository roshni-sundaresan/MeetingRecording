using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingRecorder.Application.Services;

/// <summary>Email delivery used by the password-reset flow. SMTP is configured
/// through the "Email:Smtp" section; when the host is empty (e.g. local dev
/// without a mail server) delivery is skipped with a logged warning — the
/// request still returns success to avoid account enumeration.</summary>
public interface IEmailService
{
    Task SendPasswordResetOtpAsync(string toEmail, string otp, int lifetimeMinutes, CancellationToken ct = default);
}

public class SmtpOptions
{
    public const string SectionName = "Email:Smtp";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string From { get; set; } = "no-reply@meetingrecorder.dev";
    public bool UseSsl { get; set; } = true;
}

public class SmtpEmailService : IEmailService
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IOptions<SmtpOptions> options, ILogger<SmtpEmailService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendPasswordResetOtpAsync(string toEmail, string otp, int lifetimeMinutes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            // No mail server configured — never log the OTP itself.
            _logger.LogWarning("SMTP not configured; OTP email to {Email} skipped (OTP not logged).", toEmail);
            return;
        }

        var body = $"""
            Your OTP for resetting your password is:

            {otp}

            This OTP is valid for {lifetimeMinutes} minutes.
            If you did not request a password reset, please ignore this email.
            """;

        using var message = new MailMessage
        {
            From = new MailAddress(_options.From),
            Subject = "Password Reset OTP",
            Body = body,
        };
        message.To.Add(toEmail);

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseSsl,
            Credentials = string.IsNullOrEmpty(_options.Username)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(_options.Username, _options.Password),
        };

        try
        {
            await client.SendMailAsync(message, ct);
            _logger.LogInformation("Password-reset OTP email sent to {Email}.", toEmail);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or SocketException)
        {
            // Delivery failure must not leak account existence to the caller;
            // log for ops visibility (without the OTP).
            _logger.LogWarning(ex, "Failed to send password-reset OTP email to {Email} (OTP not logged).", toEmail);
        }
    }
}
