using System.Security.Cryptography;
using System.Text;

namespace MeetingRecorder.Application.Services;

/// <summary>
/// Cryptographically secure OTP + reset-authorization primitives.
/// OTPs are 6 digits from <see cref="RandomNumberGenerator"/> (never
/// System.Random or timestamps); stored values are SHA-256 hashes compared
/// in constant time.
/// </summary>
public interface IOtpService
{
    /// <summary>Generate a cryptographically secure 6-digit OTP (000000-999999).</summary>
    string GenerateOtp();

    /// <summary>SHA-256 hex digest of the OTP — the only form stored.</summary>
    string HashOtp(string otp);

    /// <summary>Constant-time comparison of a submitted OTP against a stored hash.</summary>
    bool VerifyOtp(string otp, string expectedHash);

    /// <summary>Short-lived reset authorization (single-use, purpose-bound).</summary>
    string GenerateResetToken();
}

public class OtpService : IOtpService
{
    public string GenerateOtp()
    {
        // Cryptographic RNG; ToString("D6") guarantees exactly 6 digits.
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    }

    public string HashOtp(string otp)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(otp));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public bool VerifyOtp(string otp, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
            return false;

        var actual = Encoding.UTF8.GetBytes(HashOtp(otp));
        var expected = Encoding.UTF8.GetBytes(expectedHash.ToLowerInvariant());

        return actual.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public string GenerateResetToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
