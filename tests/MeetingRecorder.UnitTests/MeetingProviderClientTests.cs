using System.Net;
using System.Text;
using FluentAssertions;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Domain.Enums;
using MeetingRecorder.Infrastructure.ExternalServices.Meetings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeetingRecorder.UnitTests;

public class MeetingProviderClientTests
{
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task MicrosoftTeamsClient_NoTokenProvided_GeneratesMockMeeting()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new MicrosoftTeamsClient(
            new HttpClient(handler),
            Options.Create(new MeetingIntegrationOptions()),
            NullLogger<MicrosoftTeamsClient>.Instance);

        var req = new ScheduleMeetingRequest(
            Title: "Test Meeting",
            Provider: MeetingProvider.Teams,
            StartTime: "2026-09-15T10:00:00",
            EndTime: "2026-09-15T11:00:00",
            ProviderAccessToken: null);

        var result = await client.CreateMeetingAsync(req);

        result.Should().NotBeNull();
        result.JoinUrl.Should().StartWith("https://teams.microsoft.com/l/meetup-join/");
        result.MeetingCode.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task MicrosoftTeamsClient_ExpiredToken_ThrowsAppExceptionWith403()
    {
        var expiredGraphResponse = @"{
            ""error"": {
                ""code"": ""InvalidAuthenticationToken"",
                ""message"": ""Lifetime validation failed, the token is expired."",
                ""innerError"": {
                    ""date"": ""2026-09-15T03:23:35"",
                    ""request-id"": ""dd1d6366-5c6e-49d7-9531-ef623720277d""
                }
            }
        }";

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(expiredGraphResponse, Encoding.UTF8, "application/json")
        });

        var client = new MicrosoftTeamsClient(
            new HttpClient(handler),
            Options.Create(new MeetingIntegrationOptions()),
            NullLogger<MicrosoftTeamsClient>.Instance);

        var req = new ScheduleMeetingRequest(
            Title: "Sprint Planning",
            Provider: MeetingProvider.Teams,
            StartTime: "2026-09-15T10:00:00",
            EndTime: "2026-09-15T11:00:00",
            ProviderAccessToken: "expired-token-xyz");

        var act = async () => await client.CreateMeetingAsync(req);

        var ex = await act.Should().ThrowAsync<AppException>();
        ex.Which.StatusCode.Should().Be(403);
        ex.Which.ErrorCode.Should().Be("MICROSOFT_TOKEN_EXPIRED");
        ex.Which.Message.Should().Contain("Lifetime validation failed, the token is expired.");
    }

    [Fact]
    public async Task GoogleMeetClient_NoTokenProvided_GeneratesMockMeeting()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new GoogleMeetClient(
            new HttpClient(handler),
            Options.Create(new MeetingIntegrationOptions()),
            NullLogger<GoogleMeetClient>.Instance);

        var req = new ScheduleMeetingRequest(
            Title: "Test Google Meet",
            Provider: MeetingProvider.GoogleMeet,
            StartTime: "2026-09-15T10:00:00",
            EndTime: "2026-09-15T11:00:00",
            ProviderAccessToken: null);

        var result = await client.CreateMeetingAsync(req);

        result.Should().NotBeNull();
        result.JoinUrl.Should().StartWith("https://meet.google.com/");
        result.MeetingCode.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GoogleMeetClient_ExpiredToken_ThrowsAppExceptionWith403()
    {
        var expiredGoogleResponse = @"{
            ""error"": {
                ""code"": 401,
                ""message"": ""Request had invalid authentication credentials. Expected OAuth 2 access token."",
                ""status"": ""UNAUTHENTICATED""
            }
        }";

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(expiredGoogleResponse, Encoding.UTF8, "application/json")
        });

        var client = new GoogleMeetClient(
            new HttpClient(handler),
            Options.Create(new MeetingIntegrationOptions()),
            NullLogger<GoogleMeetClient>.Instance);

        var req = new ScheduleMeetingRequest(
            Title: "Sprint Planning",
            Provider: MeetingProvider.GoogleMeet,
            StartTime: "2026-09-15T10:00:00",
            EndTime: "2026-09-15T11:00:00",
            ProviderAccessToken: "expired-google-token");

        var act = async () => await client.CreateMeetingAsync(req);

        var ex = await act.Should().ThrowAsync<AppException>();
        ex.Which.StatusCode.Should().Be(403);
        ex.Which.ErrorCode.Should().Be("GOOGLE_TOKEN_EXPIRED");
        ex.Which.Message.Should().Contain("Request had invalid authentication credentials");
    }
}