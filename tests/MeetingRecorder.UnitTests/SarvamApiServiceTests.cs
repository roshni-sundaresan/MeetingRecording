using System.Net;
using FluentAssertions;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Infrastructure.ExternalServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
