using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Infrastructure.Security;
using MeetingRecorder.WebApi.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeetingRecorder.UnitTests;

public class EncryptedRequestMiddlewareTests
{
    private readonly string _privKeyPem;
    private readonly string _pubKeyPem;
    private readonly CryptoService _cryptoService;

    public EncryptedRequestMiddlewareTests()
    {
        using var rsa = RSA.Create(2048);
        _privKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        _pubKeyPem = rsa.ExportSubjectPublicKeyInfoPem();

        var opts = Options.Create(new CryptoOptions
        {
            RsaPrivateKeyPem = _privKeyPem,
            RsaPublicKeyPem = _pubKeyPem
        });
        _cryptoService = new CryptoService(opts, NullLogger<CryptoService>.Instance);
    }

    private EncryptedRequestMiddleware CreateMiddleware(RequestDelegate next)
    {
        return new EncryptedRequestMiddleware(next, NullLogger<EncryptedRequestMiddleware>.Instance);
    }

    [Fact]
    public async Task InvokeAsync_WithEncryptedPayload_RewritesBodyToDecryptedJson()
    {
        var expectedJson = "{\"title\":\"Sprint Meeting\",\"provider\":\"google_meet\"}";
        var aesKey = RandomNumberGenerator.GetBytes(32);
        var (cipherText, ivBase64) = _cryptoService.EncryptAes(expectedJson, aesKey);

        using var rsaPub = RSA.Create();
        rsaPub.ImportFromPem(_pubKeyPem);
        var encAesKey = rsaPub.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);

        var encryptedEnvelope = new
        {
            aes_key = Convert.ToBase64String(encAesKey),
            iv = ivBase64,
            cipher_text = cipherText
        };
        var envelopeJson = JsonSerializer.Serialize(encryptedEnvelope);

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(envelopeJson));
        context.Request.ContentLength = context.Request.Body.Length;

        string? executedBody = null;
        var middleware = CreateMiddleware(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            executedBody = await reader.ReadToEndAsync();
        });

        await middleware.InvokeAsync(context, _cryptoService);

        Assert.Equal(expectedJson, executedBody);
        Assert.Equal("application/json; charset=utf-8", context.Request.ContentType);
    }

    [Fact]
    public async Task InvokeAsync_WithPlaintextJson_PassesThroughUntouched()
    {
        var plainJson = "{\"email\":\"user@example.com\",\"password\":\"Pass123!\"}";

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(plainJson));
        context.Request.ContentLength = context.Request.Body.Length;

        string? executedBody = null;
        var middleware = CreateMiddleware(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            executedBody = await reader.ReadToEndAsync();
        });

        await middleware.InvokeAsync(context, _cryptoService);

        Assert.Equal(plainJson, executedBody);
    }

    [Fact]
    public async Task InvokeAsync_WithMultipart_BypassesDecryption()
    {
        var multipartData = "------WebKitFormBoundaryXYZ\r\nContent-Disposition: form-data; name=\"file\"\r\n\r\nfake-binary";

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "multipart/form-data; boundary=----WebKitFormBoundaryXYZ";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(multipartData));
        context.Request.ContentLength = context.Request.Body.Length;

        bool nextCalled = false;
        var middleware = CreateMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, _cryptoService);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_WithGetMethod_BypassesDecryption()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.ContentType = "application/json";

        bool nextCalled = false;
        var middleware = CreateMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, _cryptoService);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_WithEncryptedApiKeyPayload_DecryptsAndBindsSuccessfully()
    {
        var apiKeyJson = "{\"email\":\"user@example.com\",\"api_key\":\"sarvam_key_12345678\",\"validate\":false}";
        var aesKey = RandomNumberGenerator.GetBytes(32);
        var (cipherText, ivBase64) = _cryptoService.EncryptAes(apiKeyJson, aesKey);

        using var rsaPub = RSA.Create();
        rsaPub.ImportFromPem(_pubKeyPem);
        var encAesKey = rsaPub.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);

        var encryptedEnvelope = new
        {
            aes_key = Convert.ToBase64String(encAesKey),
            iv = ivBase64,
            cipher_text = cipherText
        };
        var envelopeJson = JsonSerializer.Serialize(encryptedEnvelope);

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/Users/api-key";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(envelopeJson));
        context.Request.ContentLength = context.Request.Body.Length;

        SetApiKeyRequest? boundModel = null;
        var middleware = CreateMiddleware(async ctx =>
        {
            boundModel = await JsonSerializer.DeserializeAsync<SetApiKeyRequest>(ctx.Request.Body);
        });

        await middleware.InvokeAsync(context, _cryptoService);

        Assert.NotNull(boundModel);
        Assert.Equal("user@example.com", boundModel.Email);
        Assert.Equal("sarvam_key_12345678", boundModel.ApiKey);
        Assert.False(boundModel.Validate);
    }

    [Fact]
    public async Task PostLiveEncryptedApiKey_WhenServerIsRunning_Succeeds()
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://localhost:5080") };
            var pubResp = await http.GetAsync("/api/Auth/public-key");
            if (!pubResp.IsSuccessStatusCode) return;

            var pubJson = await pubResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(pubJson);
            var pubPem = doc.RootElement.GetProperty("data").GetProperty("public_key").GetString()!;

            using var rsa = RSA.Create();
            rsa.ImportFromPem(pubPem);

            var aesKey = RandomNumberGenerator.GetBytes(32);
            var iv = RandomNumberGenerator.GetBytes(16);

            var plainPayload = "{\"email\":\"encrypted_live_test@idealake.com\",\"api_key\":\"sarvam_live_enc_9999\",\"validate\":false}";

            using var aes = Aes.Create();
            aes.Key = aesKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            byte[] cipherBytes;
            using (var enc = aes.CreateEncryptor())
            using (var ms = new MemoryStream())
            {
                using (var cs = new CryptoStream(ms, enc, CryptoStreamMode.Write))
                using (var writer = new StreamWriter(cs, Encoding.UTF8))
                {
                    writer.Write(plainPayload);
                }
                cipherBytes = ms.ToArray();
            }

            byte[] encAesKey = rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);

            var encBody = new
            {
                aes_key = Convert.ToBase64String(encAesKey),
                iv = Convert.ToBase64String(iv),
                cipher_text = Convert.ToBase64String(cipherBytes)
            };

            var encContent = new StringContent(JsonSerializer.Serialize(encBody), Encoding.UTF8, "application/json");
            var postResp = await http.PostAsync("/api/Users/api-key", encContent);
            Assert.True(postResp.IsSuccessStatusCode);

            var getResp = await http.GetAsync("/api/Users/api-key?email=encrypted_live_test@idealake.com");
            Assert.True(getResp.IsSuccessStatusCode);
            var getJson = await getResp.Content.ReadAsStringAsync();
            using var getDoc = JsonDocument.Parse(getJson);
            var data = getDoc.RootElement.GetProperty("data");
            Assert.Equal("sarvam_live_enc_9999", data.GetProperty("masked_key").GetString());
            Assert.Equal("encrypted_live_test@idealake.com", data.GetProperty("email").GetString());
        }
        catch (HttpRequestException)
        {
            // Server offline, ignore gracefully
        }
    }
}
