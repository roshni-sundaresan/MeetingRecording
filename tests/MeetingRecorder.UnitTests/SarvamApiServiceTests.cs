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
                        ""content"": ""### **1. Executive Summary / Overview**\nThe team discussed Q3 release roadmap.\n\n### **2. Key Discussion Points**\n* **Point:** Architecture review completed.\n\n### **3. Decisions Made & Action Items**\n* **Action Item:** John to deploy staging build by Friday.""
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
        summary.Should().Contain("1. Executive Summary / Overview");
        summary.Should().Contain("3. Decisions Made & Action Items");
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
        summary.Should().Contain("1. Executive Summary / Overview");
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
    public void SanitizeSummary_RemovesConversationalPreamblesAndPlaceholders()
    {
        var dirtySummary = @"Of course. Below is a professional and structured Minutes of Meeting (MOM) based on the provided transcript. Given the brevity of the input, the summary is focused on accurately capturing the single point of discussion.
 
---
 
### **Meeting Minutes / Summary**
 
**Date:** [Date of Meeting]
**Attendees:** [Name, Pooja - Speaker 1]
**Meeting Type:** Team Introduction / Project Update
**Subject:** Introduction to a Meeting Recording, Translation, and Summarization Application
 
---
 
#### **1. Executive Summary / Overview**
 
This meeting snippet began with an introduction from Pooja (Speaker 1). She presented a project she is leading.
 
#### **2. Key Discussion Points**
 
*   **Speaker Introduction:** Pooja introduced herself.
*   **Project Goal:** The primary objective of the application is to solve common challenges.
 
#### **3. Decisions Made & Action Items**
 
*   **Next Steps (Inferred):** A potential next step is to schedule a follow-up session.
 
---
***Note:*** This summary is based on a partial transcript or meeting snippet.";

        var clean = SarvamApiService.SanitizeSummary(dirtySummary);

        clean.Should().NotContain("Of course. Below is");
        clean.Should().NotContain("[Date of Meeting]");
        clean.Should().NotContain("***Note:***");
        clean.Should().StartWith("### **1. Executive Summary / Overview**");
        clean.Should().Contain("### **2. Key Discussion Points**");
        clean.Should().Contain("### **3. Decisions Made & Action Items**");
    }

    [Fact]
    public void SanitizeSummary_RemovesDisclaimerBlockAndLyricalNotes()
    {
        var disclaimerSummary = @"**Disclaimer:** The provided transcript appears to be the lyrics to the song ""Only Know You've Been High"" by Passenger, rather than a standard business meeting transcript. However, interpreting the lyrical themes as discussion points, here is a summary in the requested format.

---

### **Minutes of Meeting / MOM**

**Date:** [Not specified]
**Attendees:** Speaker 1

---

### **1. Executive Summary / Overview**

The meeting centered on a profound discussion regarding loss, regret, and the painful realization of value.

### **2. Key Discussion Points**

*   **Recognition of Value in Scarcity:** The team discussed how value is often only recognized during crisis.
*   **Appreciation Through Loss:** A key point was the tendency to miss something only after it.";

        var clean = SarvamApiService.SanitizeSummary(disclaimerSummary);

        clean.Should().NotContain("Disclaimer:");
        clean.Should().NotContain("Only Know You've Been High");
        clean.Should().NotContain("[Not specified]");
        clean.Should().StartWith("### **1. Executive Summary / Overview**");
        clean.Should().Contain("### **2. Key Discussion Points**");
    }

    [Fact]
    public void SanitizeSummary_RemovesModelReasoningMonologueAndDraftRepetition()
    {
        var modelWithMonologue = @"### **1. Executive Summary / Overview**
### **2. Key Discussion Points**
### **3. Decisions Made & Action Items**

I must populate these sections. Since the transcript is nonsensical/fragmented, I should still create a summary that reflects the content without inventing meeting context. However, minutes usually require business context. The prompt asks for MOM from the transcript. 

Given the constraints:
- Start immediately with the header.
- No disclaimers.
- No placeholders.
- Clean markdown.

I will write a general summary acknowledging multiple speakers discussed various topics.

Wait, Rule 3: ""Discussions about transcript quality/length/nature"" are not allowed.

Let's draft:

### **1. Executive Summary / Overview**
The conversation involved multiple participants discussing unrelated topics.

### **2. Key Discussion Points**
*   **Demonstrations:** Speaker 1 noted that a demo should be shown.

### **3. Decisions Made & Action Items**
*   No formal decisions were made during the discussion.

Is this acceptable? It follows the structure.

Let's refine the content to be as accurate as possible without being verbose.

### **1. Executive Summary / Overview**
Participants engaged in an informal exchange covering demonstrations, media selection, local geography, and retail environment.

### **2. Key Discussion Points**
*   **Demonstration Timing:** Speaker 1 indicated that a demo should be presented upon arrival.
*   **Media Consumption:** Speaker 0 suggested watching an alternate video.

### **3. Decisions Made & Action Items**
*   No definitive decisions or actionable next steps were recorded.

This looks clean and complies.
Double-check rules:
1. Output";

        var clean = SarvamApiService.SanitizeSummary(modelWithMonologue);

        clean.Should().NotContain("I must populate these sections");
        clean.Should().NotContain("Wait, Rule 3");
        clean.Should().NotContain("Let's draft");
        clean.Should().NotContain("Is this acceptable");
        clean.Should().NotContain("This looks clean and complies");
        clean.Should().NotContain("Double-check rules");
        clean.Should().StartWith("### **1. Executive Summary / Overview**\nParticipants engaged in an informal exchange");
        clean.Should().Contain("### **2. Key Discussion Points**");
        clean.Should().Contain("### **3. Decisions Made & Action Items**\n*   No definitive decisions or actionable next steps were recorded.");
    }

    [Fact]
    public void SanitizeSummary_RemovesThinkingTags()
    {
        var rawWithThink = @"<think>
Thinking process:
1. The transcript is brief.
2. Formulate MOM sections.
</think>
### **1. Executive Summary / Overview**
Participants reviewed the deployment schedule.

### **2. Key Discussion Points**
*   **Deployment:** Slated for Friday.

### **3. Decisions Made & Action Items**
*   Proceed as scheduled.";

        var clean = SarvamApiService.SanitizeSummary(rawWithThink);

        clean.Should().NotContain("<think>");
        clean.Should().NotContain("Thinking process:");
        clean.Should().StartWith("### **1. Executive Summary / Overview**");
        clean.Should().Contain("### **2. Key Discussion Points**");
        clean.Should().Contain("### **3. Decisions Made & Action Items**");
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
