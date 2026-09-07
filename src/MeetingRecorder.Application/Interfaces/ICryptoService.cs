using MeetingRecorder.Application.DTOs;

namespace MeetingRecorder.Application.Interfaces;

public interface ICryptoService
{
    /// <summary>
    /// Decrypts RSA-encrypted bytes using the server's private key.
    /// Supports OAEP SHA-256 with fallback to PKCS#1 v1.5.
    /// </summary>
    byte[] DecryptRsa(byte[] cipherBytes);

    /// <summary>
    /// Decrypts AES-256-CBC ciphertext using the provided AES key and IV.
    /// </summary>
    string DecryptAes(string cipherTextBase64, byte[] aesKey, byte[] iv);

    /// <summary>
    /// Encrypts plaintext string using AES-256-CBC and the provided AES key.
    /// Returns the Base64-encoded ciphertext and the generated Base64-encoded IV.
    /// </summary>
    (string CipherTextBase64, string IvBase64) EncryptAes(string plainText, byte[] aesKey);

    /// <summary>
    /// High-level method to decrypt an EncryptedPayloadRequest into a typed object.
    /// </summary>
    T DecryptPayload<T>(EncryptedPayloadRequest request);

    /// <summary>
    /// Returns the public key in PEM format.
    /// </summary>
    string? GetPublicKeyPem();
}
