namespace MeetingRecorder.Application.Services;

/// <summary>Binds the "PasswordReset" configuration section.</summary>
public class PasswordResetOptions
{
    public const string SectionName = "PasswordReset";

    public int OtpLifetimeMinutes { get; set; } = 5;
    public int MaxOtpAttempts { get; set; } = 5;
    public int ResendCooldownSeconds { get; set; } = 60;
    public int ResetTokenLifetimeMinutes { get; set; } = 10;

    /// <summary>
    /// DEV ONLY — when true, the OTP is echoed back in the request/resend
    /// response as <c>dev_otp</c> so flows can be tested without SMTP.
    /// MUST be false in production (spec: no OTP in API responses).
    /// Set via appsettings.Development.json; never in production config.
    /// </summary>
    public bool DevOtpExposure { get; set; }
}
