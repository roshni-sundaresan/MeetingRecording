namespace MeetingRecorder.Infrastructure.Security;

public class CryptoOptions
{
    public const string SectionName = "Crypto";

    /// <summary>
    /// Raw RSA Private Key PEM string (from environment variable RSA_PRIVATE_KEY).
    /// </summary>
    public string? RsaPrivateKeyPem { get; set; }

    /// <summary>
    /// Path to RSA Private Key PEM file (from environment variable RSA_PRIVATE_KEY_PATH).
    /// </summary>
    public string? RsaPrivateKeyPath { get; set; }

    /// <summary>
    /// Raw RSA Public Key PEM string (from environment variable RSA_PUBLIC_KEY).
    /// </summary>
    public string? RsaPublicKeyPem { get; set; }

    /// <summary>
    /// Path to RSA Public Key PEM file (from environment variable RSA_PUBLIC_KEY_PATH).
    /// </summary>
    public string? RsaPublicKeyPath { get; set; }
}
