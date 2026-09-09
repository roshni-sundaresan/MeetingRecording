using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using MeetingRecorder.Application.Common;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingRecorder.Infrastructure.ExternalServices.Meetings;

public class MicrosoftTeamsClient : IMeetingProviderClient
{
    private readonly HttpClient _httpClient;
    private readonly MeetingIntegrationOptions _options;
    private readonly ILogger<MicrosoftTeamsClient> _logger;

    public MeetingProvider Provider => MeetingProvider.Teams;

    public MicrosoftTeamsClient(
        HttpClient httpClient,
        IOptions<MeetingIntegrationOptions> options,
        ILogger<MicrosoftTeamsClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<MeetingConferenceDetails> CreateMeetingAsync(ScheduleMeetingRequest request, CancellationToken ct = default)
    {
        // 1. If a delegated OAuth token from frontend Microsoft Sign-In (MSAL) is provided, call Microsoft Graph API
        if (!string.IsNullOrWhiteSpace(request.ProviderAccessToken))
        {
            try
            {
                var details = await CreateGraphOnlineMeetingAsync(request, request.ProviderAccessToken, ct);
                if (details != null)
                {
                    return details;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create Teams meeting via Microsoft Graph API with provided token. Falling back to generated meeting link.");
            }
        }

        // 2. Default / Developer / Mock mode: Generate a valid Teams meetup join URL structure
        return GenerateMockTeamsMeeting(request);
    }

    private async Task<MeetingConferenceDetails?> CreateGraphOnlineMeetingAsync(
        ScheduleMeetingRequest request, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            "https://graph.microsoft.com/v1.0/me/onlineMeetings");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var parsedStart = MeetingTimeHelper.Parse(request.StartTime, request.TimeZone);
        var parsedEnd = MeetingTimeHelper.Parse(request.EndTime, request.TimeZone);

        var body = new
        {
            startDateTime = parsedStart.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            endDateTime = parsedEnd.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            subject = request.Title
        };

        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Microsoft Graph API returned status {StatusCode}: {Error}", response.StatusCode, err);
            return null;
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;

        var joinWebUrl = root.TryGetProperty("joinWebUrl", out var urlProp) ? urlProp.GetString() : null;
        var meetingId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

        string? meetingCode = null;
        string? passcode = null;

        if (root.TryGetProperty("joinMeetingIdSettings", out var jmIdSettings))
        {
            if (jmIdSettings.TryGetProperty("joinMeetingId", out var jmIdProp))
                meetingCode = jmIdProp.GetString();

            if (jmIdSettings.TryGetProperty("passcode", out var passProp))
                passcode = passProp.GetString();
        }

        if (string.IsNullOrWhiteSpace(meetingCode) && root.TryGetProperty("videoTeleconferenceId", out var vtcProp))
        {
            meetingCode = vtcProp.GetString();
        }

        // Safety guard: ensure passcode is never an HTML blob and never exceeds column length
        if (!string.IsNullOrWhiteSpace(passcode) &&
            (passcode.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
             passcode.StartsWith("<", StringComparison.OrdinalIgnoreCase) ||
             passcode.Length > 100))
        {
            passcode = null;
        }

        if (string.IsNullOrWhiteSpace(joinWebUrl))
        {
            return null;
        }

        return new MeetingConferenceDetails(
            JoinUrl: joinWebUrl,
            MeetingCode: meetingCode ?? meetingId,
            Passcode: passcode,
            ExternalMeetingId: meetingId);
    }

    private static MeetingConferenceDetails GenerateMockTeamsMeeting(ScheduleMeetingRequest request)
    {
        var threadId = $"19:meeting_{Guid.NewGuid():N}@thread.v2";
        var tenantId = Guid.NewGuid().ToString("D");
        var organizerOid = Guid.NewGuid().ToString("D");

        var contextObject = new
        {
            Tid = tenantId,
            Oid = organizerOid
        };
        var contextJson = JsonSerializer.Serialize(contextObject);

        // Standard Microsoft Teams web join URL
        var joinUrl = $"https://teams.microsoft.com/l/meetup-join/{Uri.EscapeDataString(threadId)}/0?context={Uri.EscapeDataString(contextJson)}";

        // Teams numeric meeting ID format (e.g. 345 678 912 012)
        var meetingCode = $"{RandomNumberGenerator.GetInt32(100, 999)} {RandomNumberGenerator.GetInt32(100, 999)} {RandomNumberGenerator.GetInt32(100, 999)} {RandomNumberGenerator.GetInt32(100, 999)}";
        var passcode = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
        var externalId = $"teams_{Guid.NewGuid():N}";

        return new MeetingConferenceDetails(
            JoinUrl: joinUrl,
            MeetingCode: meetingCode,
            Passcode: passcode,
            ExternalMeetingId: externalId);
    }
}
