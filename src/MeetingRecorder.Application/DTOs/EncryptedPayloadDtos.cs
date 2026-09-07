using System.Text.Json.Serialization;

namespace MeetingRecorder.Application.DTOs;

public class EncryptedPayloadRequest
{
    /// <summary>Base64-encoded AES key, encrypted using the server's RSA public key.</summary>
    [JsonPropertyName("aesKey")]
    public string? AesKey { get; set; }

    [JsonPropertyName("aes_key")]
    public string? AesKeySnake { set => AesKey = value; }

    /// <summary>Base64-encoded 16-byte initialization vector (IV) for AES-256-CBC.</summary>
    [JsonPropertyName("iv")]
    public string? Iv { get; set; }

    /// <summary>Base64-encoded ciphertext, encrypted using AES-256-CBC with PKCS7 padding.</summary>
    [JsonPropertyName("cipherText")]
    public string? CipherText { get; set; }

    [JsonPropertyName("cipher_text")]
    public string? CipherTextSnake { set => CipherText = value; }

    [JsonIgnore]
    public bool IsEncrypted =>
        !string.IsNullOrWhiteSpace(AesKey) &&
        !string.IsNullOrWhiteSpace(Iv) &&
        !string.IsNullOrWhiteSpace(CipherText);
}

/// <summary>
/// Dual-purpose login model supporting both plain JSON (email + password)
/// and hybrid-encrypted payloads (aesKey + iv + cipherText).
/// </summary>
public class LoginRequestInput
{
    // Plaintext fields
    public string? Email { get; set; }
    public string? Password { get; set; }

    [JsonPropertyName("providerName")]
    public string? ProviderName { get; set; }

    [JsonPropertyName("provider_name")]
    public string? ProviderNameSnake { set => ProviderName = value; }

    [JsonPropertyName("oauthKey")]
    public string? OAuthKey { get; set; }

    [JsonPropertyName("oauth_key")]
    public string? OAuthKeySnake { set => OAuthKey = value; }

    // Encrypted payload fields
    [JsonPropertyName("aesKey")]
    public string? AesKey { get; set; }

    [JsonPropertyName("aes_key")]
    public string? AesKeySnake { set => AesKey = value; }

    [JsonPropertyName("iv")]
    public string? Iv { get; set; }

    [JsonPropertyName("cipherText")]
    public string? CipherText { get; set; }

    [JsonPropertyName("cipher_text")]
    public string? CipherTextSnake { set => CipherText = value; }

    [JsonIgnore]
    public bool IsEncrypted =>
        !string.IsNullOrWhiteSpace(AesKey) &&
        !string.IsNullOrWhiteSpace(Iv) &&
        !string.IsNullOrWhiteSpace(CipherText);
}

public record PublicKeyResponse(string Algorithm, string Format, string PublicKey);
