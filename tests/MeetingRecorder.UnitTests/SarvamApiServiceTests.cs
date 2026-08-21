using System.Net;
using FluentAssertions;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Domain.Entities;
using MeetingRecorder.Infrastructure.ExternalServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace MeetingRecorder.UnitTests;

public class SarvamApiServiceTests
{
    private readonly SarvamOptions _options = new()
    {
        ApiKey = "test-api-key",
        BaseUrl = "https://api.sarvam.ai",
        SttModel = "saaras:v3",
        TtsModel = "bulbul:v3",
        TtsSpeaker = "meera",
        LanguageCode = "en-IN"
    };

    [Fact]
    public async Task SynthesizeTextToSpeech_WithValidText_ReturnsAudioBase64()
    {
        var jsonResponse = @"{ ""audios"": [""UklGRiQAAABXQVZFZm10IBAAAAABAAEAQB8AAEAFAAACABAAZGF0YQAAAAA=""] }";
        var handler = new MockHttpMessageHandler(jsonResponse, HttpStatusCode.OK);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.sarvam.ai") };
        var sut = new SarvamApiService(httpClient, Options.Create(_options), NullLogger<SarvamApiService>.Instance);

        var result = await sut.SynthesizeTextToSpeechAsync("Hello welcome to meeting");

        result.Should().Be("UklGRiQAAABXQVZFZm10IBAAAAABAAEAQB8AAEAFAAACABAAZGF0YQAAAAA=");
    }

    [Fact]
    public async Task SynthesizeTextToSpeech_WithEmptyText_ThrowsAppException()
    {
        var handler = new MockHttpMessageHandler("", HttpStatusCode.OK);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.sarvam.ai") };
        var sut = new SarvamApiService(httpClient, Options.Create(_options), NullLogger<SarvamApiService>.Instance);

        var act = () => sut.SynthesizeTextToSpeechAsync("   ");

        await act.Should().ThrowAsync<AppException>().WithMessage("*required*");
    }

    [Fact]
    public async Task TranscribeAudio_WithMissingFile_ThrowsNotFoundException()
    {
        var handler = new MockHttpMessageHandler("", HttpStatusCode.OK);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.sarvam.ai") };
        var sut = new SarvamApiService(httpClient, Options.Create(_options), NullLogger<SarvamApiService>.Instance);

        var act = () => sut.TranscribeAudioAsync("non_existent_file.wav");

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task TranscribeAudio_WithWebmFile_SendsCorrectMimeTypeAndParsesDiarizedArray()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_recording_{Guid.NewGuid():N}.webm");
        await File.WriteAllBytesAsync(tempFile, new byte[] { 0x1A, 0x45, 0xDF, 0xA3 });

        try
        {
            var jsonResponse = @"{
                ""diarized_transcript"": [
                    {
                        ""transcript"": ""Hello from frontend webm recording"",
                        ""start_time_seconds"": 1.5,
                        ""end_time_seconds"": 4.2,
                        ""speaker_id"": 0
                    }
                ]
            }";

            string? capturedContentType = null;
            var handler = new MockHttpMessageHandler(jsonResponse, HttpStatusCode.OK, req =>
            {
                if (req.Content is MultipartFormDataContent multipart)
                {
                    var fileContent = multipart.FirstOrDefault(c => c.Headers.ContentDisposition?.FileName != null);
                    capturedContentType = fileContent?.Headers.ContentType?.MediaType;
                }
            });
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.sarvam.ai") };
            var sut = new SarvamApiService(httpClient, Options.Create(_options), NullLogger<SarvamApiService>.Instance);

            var results = await sut.TranscribeAudioAsync(tempFile);

            results.Should().HaveCount(1);
            results[0].Speaker.Should().Be("Speaker 1");
            results[0].Text.Should().Be("Hello from frontend webm recording");
            results[0].StartSeconds.Should().Be(2);

            capturedContentType.Should().Be("audio/webm");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task TranscribeAudio_WithStandardSarvamRestJson_ReturnsTranscriptLine()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_recording_{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(tempFile, new byte[] { 0x00, 0x00, 0x00, 0x20 });

        try
        {
            var jsonResponse = @"{
                ""request_id"": ""req_12345"",
                ""transcript"": ""Standard REST transcription test"",
                ""language_code"": ""en-IN""
            }";

            var handler = new MockHttpMessageHandler(jsonResponse, HttpStatusCode.OK);
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.sarvam.ai") };
            var sut = new SarvamApiService(httpClient, Options.Create(_options), NullLogger<SarvamApiService>.Instance);

            var results = await sut.TranscribeAudioAsync(tempFile);

            results.Should().HaveCount(1);
            results[0].Text.Should().Be("Standard REST transcription test");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SummarizeTranscript_WithValidTranscript_ReturnsMOMSummary()
    {
        var jsonResponse = @"{
            ""id"": ""chatcmpl-12345"",
            ""choices"": [
                {
                    ""message"": {
                        ""role"": ""assistant"",
                        ""content"": ""### Minutes of Meeting (MOM)\n\n**1. Executive Summary:** The team discussed Q3 release roadmap.\n**2. Key Points:** Architecture review completed.\n**3. Action Items:** John to deploy staging build by Friday.""
                    }
                }
            ]
        }";

        var handler = new MockHttpMessageHandler(jsonResponse, HttpStatusCode.OK);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.sarvam.ai") };
        var sut = new SarvamApiService(httpClient, Options.Create(_options), NullLogger<SarvamApiService>.Instance);

        var lines = new List<MeetingRecorder.Application.DTOs.TranscriptLineDto>
        {
            new("Speaker 1", "Let's review the Q3 release roadmap and architecture.", 0, 0, 10),
            new("Speaker 2", "John will handle the staging deployment by Friday.", 11, 11, 20)
        };

        var summary = await sut.SummarizeTranscriptAsync(lines);

        summary.Should().NotBeNull();
        summary.Should().Contain("Minutes of Meeting (MOM)");
        summary.Should().Contain("Action Items");
    }

    [Fact]
    public async Task SummarizeTranscript_WhenApiFails_ReturnsFallbackSummary()
    {
        var handler = new MockHttpMessageHandler("Internal Server Error", HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.sarvam.ai") };
        var sut = new SarvamApiService(httpClient, Options.Create(_options), NullLogger<SarvamApiService>.Instance);

        var lines = new List<MeetingRecorder.Application.DTOs.TranscriptLineDto>
        {
            new("Speaker 1", "We decided on adopting the new cloud architecture.", 0, 0, 5)
        };

        var summary = await sut.SummarizeTranscriptAsync(lines);

        summary.Should().NotBeNull();
        summary.Should().Contain("We decided on adopting the new cloud architecture");
    }

    [Fact]
    public async Task SummarizeTranscript_WithEmptyTranscript_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler("", HttpStatusCode.OK);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.sarvam.ai") };
        var sut = new SarvamApiService(httpClient, Options.Create(_options), NullLogger<SarvamApiService>.Instance);

        var summary = await sut.SummarizeTranscriptAsync(string.Empty);

        summary.Should().BeNull();
    }

    [Fact]
    public async Task SynthesizeTextToSpeech_WithCustomApiKeyOverride_UsesOverriddenKey()
    {
        string? capturedKey = null;
        var jsonResponse = @"{ ""audios"": [""UklGRiQAAABXQVZFZm10IBAAAAABAAEAQB8AAEAFAAACABAAZGF0YQAAAAA=""] }";
        var handler = new MockHttpMessageHandler(jsonResponse, HttpStatusCode.OK, req =>
        {
            if (req.Headers.TryGetValues("api-subscription-key", out var values))
            {
                capturedKey = values.FirstOrDefault();
            }
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.sarvam.ai") };
        var sut = new SarvamApiService(httpClient, Options.Create(_options), NullLogger<SarvamApiService>.Instance);

        var result = await sut.SynthesizeTextToSpeechAsync("Hello welcome", null, "custom-user-api-key");

        result.Should().Be("UklGRiQAAABXQVZFZm10IBAAAAABAAEAQB8AAEAFAAACABAAZGF0YQAAAAA=");
        capturedKey.Should().Be("custom-user-api-key");
    }

    [Fact]
    public async Task ValidateApiKey_WhenValid_ReturnsTrue()
    {
        var jsonResponse = @"{ ""id"": ""chatcmpl-test"" }";
        var handler = new MockHttpMessageHandler(jsonResponse, HttpStatusCode.OK);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.sarvam.ai") };
        var sut = new SarvamApiService(httpClient, Options.Create(_options), NullLogger<SarvamApiService>.Instance);

        var isValid = await sut.ValidateApiKeyAsync("sk_valid_test_key_12345");

        isValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateApiKey_WhenUnauthorized_ReturnsFalse()
    {
        var handler = new MockHttpMessageHandler("Unauthorized", HttpStatusCode.Unauthorized);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.sarvam.ai") };
        var sut = new SarvamApiService(httpClient, Options.Create(_options), NullLogger<SarvamApiService>.Instance);

        var isValid = await sut.ValidateApiKeyAsync("sk_invalid_test_key_12345");

        isValid.Should().BeFalse();
    }

    [Fact]
    public async Task TranscribeAudio_WhenUserHasCustomApiKey_UsesUsersSpecificKeyOnly()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllBytesAsync(tempFile, new byte[100]);

        try
        {
            var userId = Guid.NewGuid();
            var userWithCustomKey = new User
            {
                Id = userId,
                Email = "user_a@test.com",
                Name = "User A",
                Mobile = "1234567890",
                PasswordHash = "hash",
                CustomApiKey = "sk_user_a_private_key_123456"
            };

            var currentUserServiceMock = new Mock<ICurrentUserService>();
            currentUserServiceMock.Setup(c => c.UserId).Returns(userId);

            var userRepoMock = new Mock<IRepository<User>>();
            userRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(userWithCustomKey);

            var uowMock = new Mock<IUnitOfWork>();
            uowMock.Setup(u => u.Repository<User>()).Returns(userRepoMock.Object);

            string? capturedKey = null;
            var jsonResponse = @"{ ""transcript"": ""Hello from user A"" }";
            var handler = new MockHttpMessageHandler(jsonResponse, HttpStatusCode.OK, req =>
            {
                if (req.Headers.TryGetValues("api-subscription-key", out var values))
                {
                    capturedKey = values.FirstOrDefault();
                }
            });

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.sarvam.ai") };
            var sut = new SarvamApiService(httpClient, Options.Create(_options), NullLogger<SarvamApiService>.Instance, currentUserServiceMock.Object, uowMock.Object);

            // Act
            var result = await sut.TranscribeAudioAsync(tempFile);

            // Assert: It used User A's unique key, NOT the system key
            capturedKey.Should().Be("sk_user_a_private_key_123456");
            result.Should().HaveCount(1);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task TranscribeAudio_WhenUserHasNoCustomApiKey_FallsBackToSystemKey()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllBytesAsync(tempFile, new byte[100]);

        try
        {
            var userId = Guid.NewGuid();
            var userWithNoKey = new User
            {
                Id = userId,
                Email = "user_b@test.com",
                Name = "User B",
                Mobile = "1234567890",
                PasswordHash = "hash",
                CustomApiKey = null // No custom key configured
            };

            var currentUserServiceMock = new Mock<ICurrentUserService>();
            currentUserServiceMock.Setup(c => c.UserId).Returns(userId);

            var userRepoMock = new Mock<IRepository<User>>();
            userRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(userWithNoKey);

            var uowMock = new Mock<IUnitOfWork>();
            uowMock.Setup(u => u.Repository<User>()).Returns(userRepoMock.Object);

            string? capturedKey = null;
            var jsonResponse = @"{ ""transcript"": ""Hello from user B"" }";
            var handler = new MockHttpMessageHandler(jsonResponse, HttpStatusCode.OK, req =>
            {
                if (req.Headers.TryGetValues("api-subscription-key", out var values))
                {
                    capturedKey = values.FirstOrDefault();
                }
            });

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.sarvam.ai") };
            var sut = new SarvamApiService(httpClient, Options.Create(_options), NullLogger<SarvamApiService>.Instance, currentUserServiceMock.Object, uowMock.Object);

            // Act
            var result = await sut.TranscribeAudioAsync(tempFile);

            // Assert: User B without custom key uses the system default key
            capturedKey.Should().Be(_options.ApiKey);
            result.Should().HaveCount(1);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseContent;
        private readonly HttpStatusCode _statusCode;
        private readonly Action<HttpRequestMessage>? _onRequest;

        public MockHttpMessageHandler(string responseContent, HttpStatusCode statusCode, Action<HttpRequestMessage>? onRequest = null)
        {
            _responseContent = responseContent;
            _statusCode = statusCode;
            _onRequest = onRequest;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _onRequest?.Invoke(request);

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseContent)
            };
            return Task.FromResult(response);
        }
    }
}
