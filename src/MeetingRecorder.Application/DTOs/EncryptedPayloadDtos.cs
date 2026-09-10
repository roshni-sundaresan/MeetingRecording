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
public class LoginRequestInput : EncryptedPayloadRequest
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

    [JsonPropertyName("microsoft_auth")]
    public string? MicrosoftAuth { get; set; }

    [JsonPropertyName("microsoftAuth")]
    public string? MicrosoftAuthCamel { set => MicrosoftAuth = value; }

    [JsonPropertyName("microsoft_auth_key")]
    public string? MicrosoftAuthKey { set => MicrosoftAuth = value; }

    [JsonPropertyName("microsoftAuthKey")]
    public string? MicrosoftAuthKeyCamel { set => MicrosoftAuth = value; }

    [JsonPropertyName("google_auth")]
    public string? GoogleAuth { get; set; }

    [JsonPropertyName("googleAuth")]
    public string? GoogleAuthCamel { set => GoogleAuth = value; }

    [JsonPropertyName("google_auth_key")]
    public string? GoogleAuthKey { set => GoogleAuth = value; }

    [JsonPropertyName("googleAuthKey")]
    public string? GoogleAuthKeyCamel { set => GoogleAuth = value; }
}

public class RegisterRequestInput : EncryptedPayloadRequest
{
    public string? Email { get; set; }
    public string? Name { get; set; }
    public string? Mobile { get; set; }
    public string? Password { get; set; }

    [JsonPropertyName("profilePhotoUrl")]
    public string? ProfilePhotoUrl { get; set; }

    [JsonPropertyName("profile_photo_url")]
    public string? ProfilePhotoUrlSnake { set => ProfilePhotoUrl = value; }
}

public class PasswordResetRequestInput : EncryptedPayloadRequest
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("email")]
    public string? Email { set => Username = value; }
}

public class VerifyOtpRequestInput : EncryptedPayloadRequest
{
    [JsonPropertyName("resetRequestId")]
    public string? ResetRequestId { get; set; }

    [JsonPropertyName("reset_request_id")]
    public string? ResetRequestIdSnake { set => ResetRequestId = value; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("username")]
    public string? Username { set => Email = value; }

    [JsonPropertyName("otp")]
    public string? Otp { get; set; }
}

public class ResendOtpRequestInput : EncryptedPayloadRequest
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("email")]
    public string? Email { set => Username = value; }
}

public class CompleteResetRequestInput : EncryptedPayloadRequest
{
    [JsonPropertyName("resetToken")]
    public string? ResetToken { get; set; }

    [JsonPropertyName("reset_token")]
    public string? ResetTokenSnake { set => ResetToken = value; }

    [JsonPropertyName("token")]
    public string? Token { set => ResetToken ??= value; }

    [JsonPropertyName("newPassword")]
    public string? NewPassword { get; set; }

    [JsonPropertyName("new_password")]
    public string? NewPasswordSnake { set => NewPassword = value; }

    [JsonPropertyName("password")]
    public string? Password { set => NewPassword = string.IsNullOrWhiteSpace(NewPassword) ? value : NewPassword; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("username")]
    public string? Username { set => Email = value; }

    [JsonPropertyName("otp")]
    public string? Otp { get; set; }
}

public record PublicKeyResponse(string Algorithm, string Format, string PublicKey);
