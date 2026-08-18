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

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseContent;
        private readonly HttpStatusCode _statusCode;

        public MockHttpMessageHandler(string responseContent, HttpStatusCode statusCode)
        {
            _responseContent = responseContent;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseContent)
            };
            return Task.FromResult(response);
        }
    }
}
