using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace MeetingRecorder.WebApi.Services;

/// <summary>
/// HMAC-SHA256 signed URLs for media streaming. A URL signed here can be
/// handed to a media player that cannot attach an Authorization header; the
/// stream endpoint validates the signature + expiry before serving bytes.
/// Signatures are single-format (v1) and time-limited (default 10 minutes).
/// </summary>
public interface ISignedUrlService
{
    /// <summary>Signs {recordingId}|{expiresUtc}|{userId} and returns a
    /// URL-safe signature. Pass null userId when the signature is not bound
    /// to a specific caller.</summary>
    string Sign(Guid recordingId, DateTime expiresUtc, Guid? userId);

    /// <summary>Constant-time validation of a signature. Also rejects
    /// expired timestamps and mismatched ids.</summary>
    bool TryValidate(Guid recordingId, string signature, DateTime expiresUtc, Guid? userId);
}

public class SignedUrlOptions
{
    public const string SectionName = "Urls";
    public string SigningKey { get; set; } = "dev-signing-key-change-me";
    public int ExpiryMinutes { get; set; } = 10;
}

public class SignedUrlService : ISignedUrlService
{
    private readonly SignedUrlOptions _options;

    public SignedUrlService(IOptions<SignedUrlOptions> options)
    {
        _options = options.Value;
    }

    public string Sign(Guid recordingId, DateTime expiresUtc, Guid? userId)
        => Compute(recordingId, expiresUtc, userId);

    public bool TryValidate(Guid recordingId, string signature, DateTime expiresUtc, Guid? userId)
    {
        if (string.IsNullOrWhiteSpace(signature) || expiresUtc < DateTime.UtcNow)
            return false;

        var expected = Compute(recordingId, expiresUtc, userId);
        var actualBytes = Encoding.UTF8.GetBytes(signature);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        return actualBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private string Compute(Guid recordingId, DateTime expiresUtc, Guid? userId)
    {
        var payload = $"v1|{recordingId:N}|{userId?.ToString("N") ?? "public"}|{expiresUtc:O}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.SigningKey));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
