using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeetingRecorder.Application.Common;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingRecorder.Infrastructure.ExternalServices.Meetings;

public class GoogleMeetClient : IMeetingProviderClient
{
    private readonly HttpClient _httpClient;
    private readonly MeetingIntegrationOptions _options;
    private readonly ILogger<GoogleMeetClient> _logger;

    public MeetingProvider Provider => MeetingProvider.GoogleMeet;

    public GoogleMeetClient(
        HttpClient httpClient,
        IOptions<MeetingIntegrationOptions> options,
        ILogger<GoogleMeetClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<MeetingConferenceDetails> CreateMeetingAsync(ScheduleMeetingRequest request, CancellationToken ct = default)
    {
        // 1. If a delegated OAuth token from frontend Google Sign-In is available, call the real Google Calendar API
        if (!string.IsNullOrWhiteSpace(request.ProviderAccessToken))
        {
            try
            {
                var details = await CreateGoogleCalendarMeetAsync(request, request.ProviderAccessToken, ct);
                if (details != null)
                {
                    return details;
                }
            }
            catch (AppException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create Google Meet via Google Calendar API with provided token.");
                throw new AppException("Failed to schedule meeting on Google Meet. Please verify your Google credentials and try again.", 400, "GOOGLE_SCHEDULING_FAILED", ex);
            }

            // If a token was provided but no meeting could be scheduled, throw rather than generating a fake meeting
            throw new AppException("Unable to schedule meeting on Google Meet with the provided credentials.", 400, "GOOGLE_SCHEDULING_FAILED");
        }

        // 2. Default / Developer / Mock mode: Generate a valid Google Meet link structure (ONLY when no token was provided)
        return GenerateMockGoogleMeet(request);
    }

    private async Task<MeetingConferenceDetails?> CreateGoogleCalendarMeetAsync(
        ScheduleMeetingRequest request, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.googleapis.com/calendar/v3/calendars/primary/events?conferenceDataVersion=1");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var attendeesList = request.Attendees?.Select(email => new { email }).ToArray() ?? Array.Empty<object>();

        var parsedStart = MeetingTimeHelper.Parse(request.StartTime, request.TimeZone);
        var parsedEnd = MeetingTimeHelper.Parse(request.EndTime, request.TimeZone);

        var body = new
        {
            summary = request.Title,
            description = request.Description,
            start = new
            {
                dateTime = parsedStart.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                timeZone = parsedStart.TimeZoneId
            },
            end = new
            {
                dateTime = parsedEnd.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                timeZone = parsedEnd.TimeZoneId
            },
            attendees = attendeesList,
            conferenceData = new
            {
                createRequest = new
                {
                    requestId = Guid.NewGuid().ToString("N"),
                    conferenceSolutionKey = new { type = "hangoutsMeet" }
                }
            }
        };

        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Google Calendar API returned status {StatusCode}: {Error}", response.StatusCode, err);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var msg = ExtractGoogleErrorMessage(err, "Google authentication token has expired or is invalid. Please sign in again with Google.");
                throw new AppException(msg, 403, "GOOGLE_TOKEN_EXPIRED");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var msg = ExtractGoogleErrorMessage(err, "Google account lacks permission to create calendar events.");
                throw new AppException(msg, 403, "GOOGLE_PERMISSION_DENIED");
            }

            var generalMsg = ExtractGoogleErrorMessage(err, $"Google Calendar meeting creation failed ({response.StatusCode}).");
            throw new AppException(generalMsg, (int)response.StatusCode, "GOOGLE_SCHEDULING_FAILED");
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;

        var hangoutLink = root.TryGetProperty("hangoutLink", out var hLink) ? hLink.GetString() : null;
        var eventId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

        string? meetingCode = null;
        string? passcode = null;

        if (root.TryGetProperty("conferenceData", out var confData))
        {
            if (confData.TryGetProperty("conferenceId", out var confId))
                meetingCode = confId.GetString();

            if (confData.TryGetProperty("entryPoints", out var entryPoints) && entryPoints.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entryPoints.EnumerateArray())
                {
                    if (entry.TryGetProperty("pin", out var pinProp))
                    {
                        passcode = pinProp.GetString();
                        break;
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(hangoutLink))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(meetingCode) && hangoutLink.Contains(".com/"))
        {
            meetingCode = hangoutLink.Substring(hangoutLink.LastIndexOf('/') + 1);
        }

        return new MeetingConferenceDetails(
            JoinUrl: hangoutLink,
            MeetingCode: meetingCode,
            Passcode: passcode,
            ExternalMeetingId: eventId);
    }

    private static MeetingConferenceDetails GenerateMockGoogleMeet(ScheduleMeetingRequest request)
    {
        // Google Meet codes follow the 3-4-3 lowercase letter pattern: xxx-yyyy-zzz
        var code = GenerateMeetCode();
        var joinUrl = $"https://meet.google.com/{code}";
        var externalId = $"gmeet_{Guid.NewGuid():N}";
        var pin = RandomNumberGenerator.GetInt32(100000, 999999).ToString();

        return new MeetingConferenceDetails(
            JoinUrl: joinUrl,
            MeetingCode: code,
            Passcode: pin,
            ExternalMeetingId: externalId);
    }

    private static string GenerateMeetCode()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz";
        string RandomChars(int count)
        {
            var bytes = new byte[count];
            RandomNumberGenerator.Fill(bytes);
            var sb = new StringBuilder(count);
            foreach (var b in bytes)
            {
                sb.Append(chars[b % chars.Length]);
            }
            return sb.ToString();
        }

        return $"{RandomChars(3)}-{RandomChars(4)}-{RandomChars(3)}";
    }

    private static string ExtractGoogleErrorMessage(string errorJson, string defaultMsg)
    {
        try
        {
            using var doc = JsonDocument.Parse(errorJson);
            if (doc.RootElement.TryGetProperty("error", out var errObj) &&
                errObj.TryGetProperty("message", out var msgProp) &&
                !string.IsNullOrWhiteSpace(msgProp.GetString()))
            {
                return msgProp.GetString()!;
            }
        }
        catch
        {
            // ignored
        }
        return defaultMsg;
    }
}
