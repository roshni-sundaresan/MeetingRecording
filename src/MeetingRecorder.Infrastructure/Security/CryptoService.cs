using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingRecorder.Infrastructure.Security;

public class CryptoService : ICryptoService, IDisposable
{
    private readonly ILogger<CryptoService> _logger;
    private readonly RSA? _rsaPrivateKey;
    private readonly string? _publicKeyPem;
    private bool _disposed;

    public CryptoService(IOptions<CryptoOptions> options, ILogger<CryptoService> logger)
    {
        _logger = logger;

        try
        {
            var privPem = ResolvePem(
                options.Value.RsaPrivateKeyPem,
                Environment.GetEnvironmentVariable("RSA_PRIVATE_KEY"),
                options.Value.RsaPrivateKeyPath,
                Environment.GetEnvironmentVariable("RSA_PRIVATE_KEY_PATH") ?? "private_key.pem");

            if (!string.IsNullOrWhiteSpace(privPem))
            {
                var rsa = RSA.Create();
                rsa.ImportFromPem(privPem);
                _rsaPrivateKey = rsa;
                _logger.LogInformation("RSA Private Key loaded successfully into CryptoService.");
            }
            else
            {
                _logger.LogWarning("No RSA Private Key found. Encrypted endpoints will fail until RSA_PRIVATE_KEY is configured.");
            }

            _publicKeyPem = ResolvePem(
                options.Value.RsaPublicKeyPem,
                Environment.GetEnvironmentVariable("RSA_PUBLIC_KEY"),
                options.Value.RsaPublicKeyPath,
                Environment.GetEnvironmentVariable("RSA_PUBLIC_KEY_PATH") ?? "public_key.pem");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize RSA keys in CryptoService.");
        }
    }

    public string? GetPublicKeyPem() => _publicKeyPem;

    public byte[] DecryptRsa(byte[] cipherBytes)
    {
        if (_rsaPrivateKey == null)
            throw new AppException("Server RSA private key is not configured.", 500, "CRYPTO_KEY_MISSING");

        try
        {
            // Primary modern standard: OAEP SHA-256
            return _rsaPrivateKey.Decrypt(cipherBytes, RSAEncryptionPadding.OaepSHA256);
        }
        catch (CryptographicException)
        {
            // Fallback: PKCS#1 v1.5 for legacy frontend libraries
            try
            {
                return _rsaPrivateKey.Decrypt(cipherBytes, RSAEncryptionPadding.Pkcs1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("RSA decryption failed with both OAEP-SHA256 and PKCS#1: {Error}", ex.Message);
                throw new AppException("Failed to decrypt RSA AES key. Check public key and padding.", 400, "RSA_DECRYPT_FAILED");
            }
        }
    }

    public string DecryptAes(string cipherTextBase64, byte[] aesKey, byte[] iv)
    {
        if (string.IsNullOrWhiteSpace(cipherTextBase64))
            throw new AppException("Ciphertext cannot be empty.", 400, "VALIDATION_ERROR");

        if (aesKey == null || (aesKey.Length != 16 && aesKey.Length != 24 && aesKey.Length != 32))
            throw new AppException("Invalid AES key length. Expected 128, 192, or 256 bits.", 400, "INVALID_AES_KEY");

        if (iv == null || iv.Length != 16)
            throw new AppException("Invalid IV length. Expected 16 bytes for AES.", 400, "INVALID_IV");

        try
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherTextBase64);

            using var aes = Aes.Create();
            aes.Key = aesKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            using var ms = new MemoryStream(cipherBytes);
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var reader = new StreamReader(cs, Encoding.UTF8);

            return reader.ReadToEnd();
        }
        catch (FormatException)
        {
            throw new AppException("Ciphertext is not valid Base64.", 400, "INVALID_BASE64");
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning("AES decryption failed: {Error}", ex.Message);
            throw new AppException("Failed to decrypt AES ciphertext. Invalid key, IV, or padding.", 400, "AES_DECRYPT_FAILED");
        }
    }

    public (string CipherTextBase64, string IvBase64) EncryptAes(string plainText, byte[] aesKey)
    {
        if (plainText == null) plainText = string.Empty;

        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        using var ms = new MemoryStream();
        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        using (var writer = new StreamWriter(cs, Encoding.UTF8))
        {
            writer.Write(plainText);
        }

        byte[] cipherBytes = ms.ToArray();
        return (Convert.ToBase64String(cipherBytes), Convert.ToBase64String(aes.IV));
    }

    public T DecryptPayload<T>(EncryptedPayloadRequest request)
    {
        string decryptedJson = DecryptPayloadRaw(request);

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            };
            var result = JsonSerializer.Deserialize<T>(decryptedJson, options);
            if (result == null)
                throw new AppException("Decrypted JSON payload could not be deserialized.", 400, "INVALID_PAYLOAD");

            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Failed to deserialize decrypted JSON into {Type}: {Error}", typeof(T).Name, ex.Message);
            throw new AppException($"Decrypted payload is not valid JSON for {typeof(T).Name}.", 400, "INVALID_JSON");
        }
    }

    public string DecryptPayloadRaw(EncryptedPayloadRequest request)
    {
        if (request == null)
            throw new AppException("Encrypted request payload is required.", 400, "INVALID_PAYLOAD");

        if (!request.IsEncrypted)
            throw new AppException("Missing required encrypted payload fields (aesKey, iv, cipherText).", 400, "INVALID_ENCRYPTED_PAYLOAD");

        byte[] encryptedAesKeyBytes;
        byte[] ivBytes;

        try
        {
            encryptedAesKeyBytes = Convert.FromBase64String(request.AesKey!);
        }
        catch
        {
            throw new AppException("'aesKey' is not a valid Base64 string.", 400, "INVALID_BASE64");
        }

        try
        {
            ivBytes = Convert.FromBase64String(request.Iv!);
        }
        catch
        {
            throw new AppException("'iv' is not a valid Base64 string.", 400, "INVALID_BASE64");
        }

        byte[] decryptedAesKey = DecryptRsa(encryptedAesKeyBytes);
        return DecryptAes(request.CipherText!, decryptedAesKey, ivBytes);
    }

    private static string? ResolvePem(string? directPem, string? envPem, string? filePath, string? defaultFileName)
    {
        if (!string.IsNullOrWhiteSpace(directPem)) return directPem;
        if (!string.IsNullOrWhiteSpace(envPem)) return envPem;

        var candidatePath = !string.IsNullOrWhiteSpace(filePath) ? filePath : defaultFileName;
        if (!string.IsNullOrWhiteSpace(candidatePath))
        {
            var searchPaths = new[]
            {
                candidatePath,
                Path.Combine(AppContext.BaseDirectory, candidatePath),
                Path.Combine(Directory.GetCurrentDirectory(), candidatePath),
                Path.Combine(Directory.GetCurrentDirectory(), "..", candidatePath),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "..", candidatePath),
                Path.Combine(AppContext.BaseDirectory, "..", candidatePath),
                Path.Combine(AppContext.BaseDirectory, "..", "..", candidatePath),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", candidatePath)
            };

            foreach (var p in searchPaths)
            {
                try
                {
                    if (File.Exists(p))
                        return File.ReadAllText(p);
                }
                catch
                {
                    // Ignore path probing errors
                }
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _rsaPrivateKey?.Dispose();
            _disposed = true;
        }
    }
}
