using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeetingRecorder.UnitTests;

public class CryptoServiceTests
{
    private readonly string _privKeyPem;
    private readonly string _pubKeyPem;

    public CryptoServiceTests()
    {
        using var rsa = RSA.Create(2048);
        _privKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        _pubKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
    }

    private CryptoService CreateService(string? privPem = null, string? pubPem = null)
    {
        var opts = Options.Create(new CryptoOptions
        {
            RsaPrivateKeyPem = privPem ?? _privKeyPem,
            RsaPublicKeyPem = pubPem ?? _pubKeyPem
        });
        return new CryptoService(opts, NullLogger<CryptoService>.Instance);
    }

    [Fact]
    public void DecryptRsa_WithOaepSha256_Succeeds()
    {
        using var service = CreateService();
        using var rsaPub = RSA.Create();
        rsaPub.ImportFromPem(_pubKeyPem);

        var originalBytes = Encoding.UTF8.GetBytes("TestAESKey32BytesLongSecret12345");
        var encrypted = rsaPub.Encrypt(originalBytes, RSAEncryptionPadding.OaepSHA256);

        var decrypted = service.DecryptRsa(encrypted);
        Assert.Equal(originalBytes, decrypted);
    }

    [Fact]
    public void DecryptRsa_WithPkcs1Fallback_Succeeds()
    {
        using var service = CreateService();
        using var rsaPub = RSA.Create();
        rsaPub.ImportFromPem(_pubKeyPem);

        var originalBytes = Encoding.UTF8.GetBytes("TestLegacyPkcs1KeySecret12345678");
        var encrypted = rsaPub.Encrypt(originalBytes, RSAEncryptionPadding.Pkcs1);

        var decrypted = service.DecryptRsa(encrypted);
        Assert.Equal(originalBytes, decrypted);
    }

    [Fact]
    public void EncryptDecryptAes_RoundTrip_MatchesOriginal()
    {
        using var service = CreateService();
        var aesKey = RandomNumberGenerator.GetBytes(32);
        var message = "Hello from Flutter frontend with sensitive credentials!";

        var (cipherText, iv) = service.EncryptAes(message, aesKey);

        Assert.NotNull(cipherText);
        Assert.NotNull(iv);

        var decrypted = service.DecryptAes(cipherText, aesKey, Convert.FromBase64String(iv));
        Assert.Equal(message, decrypted);
    }

    [Fact]
    public void DecryptPayload_WithValidEncryptedRequest_DeserializesModel()
    {
        using var service = CreateService();
        using var rsaPub = RSA.Create();
        rsaPub.ImportFromPem(_pubKeyPem);

        var aesKey = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(16);

        var loginDto = new LoginRequest("user@example.com", "Password@123");
        var json = JsonSerializer.Serialize(loginDto);

        // Encrypt with AES
        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        byte[] cipherBytes;
        using (var encryptor = aes.CreateEncryptor())
        using (var ms = new MemoryStream())
        {
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            using (var writer = new StreamWriter(cs, Encoding.UTF8))
            {
                writer.Write(json);
            }
            cipherBytes = ms.ToArray();
        }

        // Encrypt AES key with RSA
        var encAesKey = rsaPub.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);

        var request = new EncryptedPayloadRequest
        {
            AesKey = Convert.ToBase64String(encAesKey),
            Iv = Convert.ToBase64String(iv),
            CipherText = Convert.ToBase64String(cipherBytes)
        };

        var result = service.DecryptPayload<LoginRequest>(request);

        Assert.NotNull(result);
        Assert.Equal("user@example.com", result.Email);
        Assert.Equal("Password@123", result.Password);
    }

    [Fact]
    public void DecryptPayload_WithInvalidBase64_ThrowsAppException()
    {
        using var service = CreateService();
        var request = new EncryptedPayloadRequest
        {
            AesKey = "not-valid-base64!",
            Iv = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
            CipherText = "not-valid-base64!"
        };

        var ex = Assert.Throws<AppException>(() => service.DecryptPayload<LoginRequest>(request));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void DecryptPayloadRaw_ReturnsOriginalJsonString()
    {
        using var service = CreateService();
        using var rsaPub = RSA.Create();
        rsaPub.ImportFromPem(_pubKeyPem);

        var aesKey = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(16);
        var expectedJson = "{\"title\":\"Quarterly Review\",\"provider\":\"google_meet\"}";

        var (cipherText, ivBase64) = service.EncryptAes(expectedJson, aesKey);
        var encAesKey = rsaPub.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);

        var request = new EncryptedPayloadRequest
        {
            AesKey = Convert.ToBase64String(encAesKey),
            Iv = ivBase64,
            CipherText = cipherText
        };

        var rawDecrypted = service.DecryptPayloadRaw(request);

        Assert.Equal(expectedJson, rawDecrypted);
    }
}
