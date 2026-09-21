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

    [Fact]
    public void MicrosoftTeamsClient_ConvertToHtml_FormatsMarkdownProperly()
    {
        var markdown = "### **1. Executive Summary**\r\nDiscussed key release goals.\r\n\r\n### **2. Action Items**\r\n- First action\r\n- Second action\r\n\r\n---\r\nEnd of meeting.";
        var html = MicrosoftTeamsClient.ConvertToHtml(markdown);

        html.Should().Contain("<h3><strong>1. Executive Summary</strong></h3>");
        html.Should().Contain("<p>Discussed key release goals.</p>");
        html.Should().Contain("<h3><strong>2. Action Items</strong></h3>");
        html.Should().Contain("<ul><li>First action</li><li>Second action</li></ul>");
        html.Should().Contain("<hr/>");
        html.Should().Contain("<p>End of meeting.</p>");
    }

    [Fact]
    public void MicrosoftTeamsClient_ConvertToHtml_PreservesExistingHtml()
    {
        var existingHtml = "<p>Hello <b>world</b></p>";
        var result = MicrosoftTeamsClient.ConvertToHtml(existingHtml);

        result.Should().Be(existingHtml);
    }

    [Fact]
    public async Task MicrosoftTeamsClient_WithSummaryInDescription_SendsFormattedHtmlToGraph()
    {
        string? capturedBody = null;
        var fakeSuccessResponse = @"{
            ""id"": ""teams-evt-123"",
            ""onlineMeeting"": {
                ""joinUrl"": ""https://teams.microsoft.com/l/meetup-join/19%3ameeting"",
                ""conferenceId"": ""123 456 789""
            }
        }";

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Content != null)
            {
                capturedBody = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(fakeSuccessResponse, Encoding.UTF8, "application/json")
            };
        });

        var client = new MicrosoftTeamsClient(
            new HttpClient(handler),
            Options.Create(new MeetingIntegrationOptions()),
            NullLogger<MicrosoftTeamsClient>.Instance);

        var req = new ScheduleMeetingRequest(
            Title: "Sprint Review",
            Provider: MeetingProvider.Teams,
            StartTime: "2026-09-15T10:00:00",
            EndTime: "2026-09-15T11:00:00",
            Description: "### Summary\n- Deliverables met\n- All tests pass",
            ProviderAccessToken: "valid-graph-token");

        var result = await client.CreateMeetingAsync(req);

        result.Should().NotBeNull();
        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("\"contentType\":\"HTML\"");
        capturedBody.Should().Contain("<h3>Summary</h3>");
        capturedBody.Should().Contain("<ul><li>Deliverables met</li><li>All tests pass</li></ul>");
    }

    [Fact]
    public async Task MicrosoftTeamsClient_RefreshAccessToken_Success_ReturnsNewToken()
    {
        var refreshSuccessResponse = @"{
            ""token_type"": ""Bearer"",
            ""scope"": ""Calendars.ReadWrite OnlineMeetings.ReadWrite offline_access"",
            ""expires_in"": 3600,
            ""ext_expires_in"": 3600,
            ""access_token"": ""new-teams-access-token-999"",
            ""refresh_token"": ""new-teams-refresh-token-888""
        }";

        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Content != null)
            {
                capturedBody = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(refreshSuccessResponse, Encoding.UTF8, "application/json")
            };
        });

        var options = new MeetingIntegrationOptions();
        options.MicrosoftTeams.ClientId = "test-client-id";
        options.MicrosoftTeams.ClientSecret = "test-client-secret";

        var client = new MicrosoftTeamsClient(
            new HttpClient(handler),
            Options.Create(options),
            NullLogger<MicrosoftTeamsClient>.Instance);

        var token = await client.RefreshAccessTokenAsync("my-old-refresh-token");

        token.Should().Be("new-teams-access-token-999");
        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("grant_type=refresh_token");
        capturedBody.Should().Contain("refresh_token=my-old-refresh-token");
        capturedBody.Should().Contain("client_id=test-client-id");
        capturedBody.Should().Contain("client_secret=test-client-secret");
    }

    [Fact]
    public async Task MicrosoftTeamsClient_RefreshAccessToken_Failure_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json")
        });

        var client = new MicrosoftTeamsClient(
            new HttpClient(handler),
            Options.Create(new MeetingIntegrationOptions()),
            NullLogger<MicrosoftTeamsClient>.Instance);

        var token = await client.RefreshAccessTokenAsync("bad-refresh-token");

        token.Should().BeNull();
    }

    [Fact]
    public async Task GoogleMeetClient_RefreshAccessToken_Success_ReturnsNewToken()
    {
        var refreshSuccessResponse = @"{
            ""access_token"": ""new-google-access-token-777"",
            ""expires_in"": 3599,
            ""token_type"": ""Bearer""
        }";

        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Content != null)
            {
                capturedBody = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(refreshSuccessResponse, Encoding.UTF8, "application/json")
            };
        });

        var options = new MeetingIntegrationOptions();
        options.Google.ClientId = "google-client-id";
        options.Google.ClientSecret = "google-client-secret";

        var client = new GoogleMeetClient(
            new HttpClient(handler),
            Options.Create(options),
            NullLogger<GoogleMeetClient>.Instance);

        var token = await client.RefreshAccessTokenAsync("my-google-refresh-token");

        token.Should().Be("new-google-access-token-777");
        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("grant_type=refresh_token");
        capturedBody.Should().Contain("refresh_token=my-google-refresh-token");
        capturedBody.Should().Contain("client_id=google-client-id");
        capturedBody.Should().Contain("client_secret=google-client-secret");
    }

    [Fact]
    public async Task GoogleMeetClient_RefreshAccessToken_Failure_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json")
        });

        var client = new GoogleMeetClient(
            new HttpClient(handler),
            Options.Create(new MeetingIntegrationOptions()),
            NullLogger<GoogleMeetClient>.Instance);

        var token = await client.RefreshAccessTokenAsync("bad-refresh-token");

        token.Should().BeNull();
    }

    [Fact]
    public async Task GoogleMeetClient_ExchangeAuthCode_Success_ReturnsTokens()
    {
        var exchangeSuccessResponse = @"{
            ""access_token"": ""google-access-token-123"",
            ""expires_in"": 3599,
            ""refresh_token"": ""google-refresh-token-456"",
            ""scope"": ""https://www.googleapis.com/auth/calendar.events"",
            ""token_type"": ""Bearer""
        }";

        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Content != null)
            {
                capturedBody = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(exchangeSuccessResponse, Encoding.UTF8, "application/json")
            };
        });

        var options = new MeetingIntegrationOptions();
        options.Google.ClientId = "google-client-id";
        options.Google.ClientSecret = "google-client-secret";

        var client = new GoogleMeetClient(
            new HttpClient(handler),
            Options.Create(options),
            NullLogger<GoogleMeetClient>.Instance);

        var result = await client.ExchangeAuthCodeAsync("google-one-time-code-xyz", "postmessage");

        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("google-access-token-123");
        result.RefreshToken.Should().Be("google-refresh-token-456");
        result.ExpiresIn.Should().Be(3599);

        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("grant_type=authorization_code");
        capturedBody.Should().Contain("code=google-one-time-code-xyz");
        capturedBody.Should().Contain("redirect_uri=postmessage");
        capturedBody.Should().Contain("client_id=google-client-id");
        capturedBody.Should().Contain("client_secret=google-client-secret");
    }

    [Fact]
    public async Task MicrosoftTeamsClient_ExchangeAuthCode_Success_ReturnsTokens()
    {
        var exchangeSuccessResponse = @"{
            ""access_token"": ""teams-access-token-123"",
            ""expires_in"": 3600,
            ""refresh_token"": ""teams-refresh-token-456"",
            ""token_type"": ""Bearer""
        }";

        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Content != null)
            {
                capturedBody = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(exchangeSuccessResponse, Encoding.UTF8, "application/json")
            };
        });

        var options = new MeetingIntegrationOptions();
        options.MicrosoftTeams.ClientId = "ms-client-id";
        options.MicrosoftTeams.ClientSecret = "ms-client-secret";

        var client = new MicrosoftTeamsClient(
            new HttpClient(handler),
            Options.Create(options),
            NullLogger<MicrosoftTeamsClient>.Instance);

        var result = await client.ExchangeAuthCodeAsync("ms-one-time-code-abc", "http://localhost:8080");

        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("teams-access-token-123");
        result.RefreshToken.Should().Be("teams-refresh-token-456");
        result.ExpiresIn.Should().Be(3600);

        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("grant_type=authorization_code");
        capturedBody.Should().Contain("code=ms-one-time-code-abc");
        capturedBody.Should().Contain("redirect_uri=http%3A%2F%2Flocalhost%3A8080");
        capturedBody.Should().Contain("client_id=ms-client-id");
        capturedBody.Should().Contain("client_secret=ms-client-secret");
    }
}
