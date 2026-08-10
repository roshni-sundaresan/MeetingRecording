namespace MeetingRecorder.Domain.Entities;

/// <summary>
/// Server-side record of a password-reset OTP flow.
///
/// Only hashes are stored (OtpHash / ResetTokenHash) — the plaintext OTP and
/// the reset authorization are returned to the client once (and the OTP is
/// emailed, never placed in API responses in production).
/// </summary>
public class PasswordResetRequest
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>SHA-256 hex digest of the 6-digit OTP.</summary>
    public string OtpHash { get; set; } = string.Empty;

    /// <summary>Purpose discriminator (only PASSWORD_RESET is used today).</summary>
    public string Purpose { get; set; } = "PASSWORD_RESET";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    /// <summary>Failed verification attempts against this OTP.</summary>
    public int AttemptCount { get; set; }

    /// <summary>True once the OTP has been consumed (verified) or the flow closed.</summary>
    public bool IsUsed { get; set; }

    /// <summary>Earliest allowed resend time (cooldown guard).</summary>
    public DateTime? ResendAt { get; set; }

    /// <summary>SHA-256 hex digest of the short-lived reset authorization issued after OTP verification.</summary>
    public string? ResetTokenHash { get; set; }
    public DateTime? ResetTokenExpiresAt { get; set; }
}
