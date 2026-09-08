using System.Text;
using System.Text.Json;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MeetingRecorder.WebApi.Middleware;

/// <summary>
/// Transparently detects and decrypts incoming hybrid-encrypted JSON payloads (RSA + AES-256-CBC).
/// If a request contains aesKey/aes_key, iv, and cipherText/cipher_text, its body is decrypted
/// in memory and rewritten so downstream controllers receive standard plaintext JSON models.
/// Multipart/form-data and non-JSON requests are completely bypassed.
/// </summary>
public class EncryptedRequestMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<EncryptedRequestMiddleware> _logger;

    public EncryptedRequestMiddleware(RequestDelegate next, ILogger<EncryptedRequestMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ICryptoService cryptoService)
    {
        // 1. Bypass HTTP methods that never carry JSON request bodies
        if (HttpMethods.IsGet(context.Request.Method) ||
            HttpMethods.IsDelete(context.Request.Method) ||
            HttpMethods.IsHead(context.Request.Method) ||
            HttpMethods.IsOptions(context.Request.Method) ||
            HttpMethods.IsTrace(context.Request.Method))
        {
            await _next(context);
            return;
        }

        // 2. Bypass multipart/form-data (chunked uploads, audio/file uploads) and non-JSON content
        var contentType = context.Request.ContentType;
        if (string.IsNullOrEmpty(contentType) ||
            contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) ||
            !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // 3. Bypass requests with zero content length
        if (context.Request.ContentLength == 0)
        {
            await _next(context);
            return;
        }

        // 4. Enable stream buffering so the request body can be read and rewound if needed
        context.Request.EnableBuffering();

        string bodyText;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            bodyText = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
        }

        if (string.IsNullOrWhiteSpace(bodyText))
        {
            await _next(context);
            return;
        }

        // 5. Inspect JSON to see if it is a hybrid-encrypted payload
        EncryptedPayloadRequest? encryptedPayload = null;
        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                string? aesKey = null;
                string? iv = null;
                string? cipherText = null;

                if (root.TryGetProperty("aesKey", out var pAes) || root.TryGetProperty("aes_key", out pAes))
                    aesKey = pAes.GetString();

                if (root.TryGetProperty("iv", out var pIv))
                    iv = pIv.GetString();

                if (root.TryGetProperty("cipherText", out var pCipher) || root.TryGetProperty("cipher_text", out pCipher))
                    cipherText = pCipher.GetString();

                if (!string.IsNullOrWhiteSpace(aesKey) &&
                    !string.IsNullOrWhiteSpace(iv) &&
                    !string.IsNullOrWhiteSpace(cipherText))
                {
                    encryptedPayload = new EncryptedPayloadRequest
                    {
                        AesKey = aesKey,
                        Iv = iv,
                        CipherText = cipherText
                    };
                }
            }
        }
        catch (JsonException)
        {
            // Not valid JSON; reset stream position and allow downstream model binding / validation to handle it
            context.Request.Body.Position = 0;
            await _next(context);
            return;
        }

        // 6. If not encrypted, leave the stream rewound at 0 and pass through untouched
        if (encryptedPayload == null)
        {
            context.Request.Body.Position = 0;
            await _next(context);
            return;
        }

        // 7. Decrypt the payload and rewrite the request body
        _logger.LogInformation("Transparently decrypting encrypted payload for {Method} {Path}",
            context.Request.Method, context.Request.Path);

        string decryptedJson = cryptoService.DecryptPayloadRaw(encryptedPayload);

        var decryptedBytes = Encoding.UTF8.GetBytes(decryptedJson);
        context.Request.Body = new MemoryStream(decryptedBytes);
        context.Request.ContentLength = decryptedBytes.Length;
        context.Request.ContentType = "application/json; charset=utf-8";

        await _next(context);
    }
}
