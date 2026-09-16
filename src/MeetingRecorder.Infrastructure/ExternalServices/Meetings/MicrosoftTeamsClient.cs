using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using MeetingRecorder.Application.Common;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
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

    private static readonly JsonSerializerOptions GraphJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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
            // Primary: Create Calendar Event (/me/events) with isOnlineMeeting: true.
            // This schedules the meeting directly on the user's Teams & Outlook Calendar AND dispatches invitations to attendees.
            try
            {
                var eventDetails = await CreateGraphCalendarEventAsync(request, request.ProviderAccessToken, ct);
                if (eventDetails != null)
                {
                    return eventDetails;
                }
            }
            catch (AppException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create Teams calendar event via Microsoft Graph /me/events. Falling back to /me/onlineMeetings.");
            }

            // Fallback: Create standalone online meeting room (/me/onlineMeetings)
            try
            {
                var details = await CreateGraphOnlineMeetingAsync(request, request.ProviderAccessToken, ct);
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
                _logger.LogWarning(ex, "Failed to create Teams meeting via Microsoft Graph API with provided token.");
                throw new AppException("Failed to schedule meeting on Microsoft Teams. Please verify your Microsoft credentials and try again.", 400, "TEAMS_SCHEDULING_FAILED", ex);
            }

            // If a token was provided but no meeting could be scheduled, throw rather than generating a fake meeting
            throw new AppException("Unable to schedule meeting on Microsoft Teams with the provided credentials.", 400, "TEAMS_SCHEDULING_FAILED");
        }

        // 2. Default / Developer / Mock mode: Generate a valid Teams meetup join URL structure (ONLY when no token was provided)
        return GenerateMockTeamsMeeting(request);
    }

    private async Task<MeetingConferenceDetails?> CreateGraphCalendarEventAsync(
        ScheduleMeetingRequest request, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            "https://graph.microsoft.com/v1.0/me/events");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var parsedStart = MeetingTimeHelper.Parse(request.StartTime, request.TimeZone);
        var parsedEnd = MeetingTimeHelper.Parse(request.EndTime, request.TimeZone);

        var attendeesList = request.Attendees?
            .Where(email => !string.IsNullOrWhiteSpace(email))
            .Select(email => new
            {
                emailAddress = new
                {
                    address = email.Trim()
                },
                type = "required"
            })
            .ToArray() ?? Array.Empty<object>();

        var description = !string.IsNullOrWhiteSpace(request.Description)
            ? request.Description
            : request.Title;

        var body = new
        {
            subject = request.Title,
            body = new
            {
                contentType = "HTML",
                content = ConvertToHtml(description)
            },
            start = new
            {
                dateTime = parsedStart.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss"),
                timeZone = "UTC"
            },
            end = new
            {
                dateTime = parsedEnd.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss"),
                timeZone = "UTC"
            },
            attendees = attendeesList,
            isOnlineMeeting = true,
            onlineMeetingProvider = "teamsForBusiness"
        };

        req.Content = new StringContent(JsonSerializer.Serialize(body, GraphJsonOptions), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Microsoft Graph /me/events API returned status {StatusCode}: {Error}", response.StatusCode, err);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var msg = ExtractGraphErrorMessage(err, "Microsoft authentication token has expired or is invalid. Please sign in again with Microsoft.");
                throw new AppException(msg, 403, "MICROSOFT_TOKEN_EXPIRED");
            }

            return null;
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;

        string? joinWebUrl = null;
        string? meetingCode = null;
        string? passcode = null;
        var eventId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

        if (root.TryGetProperty("onlineMeeting", out var omProp) && omProp.ValueKind == JsonValueKind.Object)
        {
            if (omProp.TryGetProperty("joinUrl", out var joinUrlProp))
            {
                joinWebUrl = joinUrlProp.GetString();
            }

            if (omProp.TryGetProperty("conferenceId", out var confIdProp))
            {
                meetingCode = confIdProp.GetString();
            }

            if (omProp.TryGetProperty("joinMeetingIdSettings", out var jmSettings) && jmSettings.ValueKind == JsonValueKind.Object)
            {
                if (jmSettings.TryGetProperty("joinMeetingId", out var jmIdProp))
                {
                    meetingCode = jmIdProp.GetString();
                }

                if (jmSettings.TryGetProperty("passcode", out var passProp))
                {
                    passcode = passProp.GetString();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(joinWebUrl) && root.TryGetProperty("onlineMeetingUrl", out var omUrlProp))
        {
            joinWebUrl = omUrlProp.GetString();
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
            _logger.LogWarning("Microsoft Graph /me/events succeeded but did not return a Teams join URL. Falling back to /me/onlineMeetings.");
            return null;
        }

        return new MeetingConferenceDetails(
            JoinUrl: joinWebUrl.Length > 2000 ? joinWebUrl[..2000] : joinWebUrl,
            MeetingCode: meetingCode != null && meetingCode.Length > 200 ? meetingCode[..200] : (meetingCode ?? eventId),
            Passcode: passcode != null && passcode.Length > 100 ? passcode[..100] : passcode,
            ExternalMeetingId: eventId != null && eventId.Length > 500 ? eventId[..500] : eventId);
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

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var msg = ExtractGraphErrorMessage(err, "Microsoft authentication token has expired or is invalid. Please sign in again with Microsoft.");
                throw new AppException(msg, 403, "MICROSOFT_TOKEN_EXPIRED");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var msg = ExtractGraphErrorMessage(err, "Microsoft account lacks permission to create online meetings (OnlineMeetings.ReadWrite required).");
                throw new AppException(msg, 403, "MICROSOFT_PERMISSION_DENIED");
            }

            var generalMsg = ExtractGraphErrorMessage(err, $"Microsoft Graph meeting creation failed ({response.StatusCode}).");
            throw new AppException(generalMsg, (int)response.StatusCode, "TEAMS_CREATION_FAILED");
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
            JoinUrl: joinWebUrl.Length > 2000 ? joinWebUrl[..2000] : joinWebUrl,
            MeetingCode: meetingCode != null && meetingCode.Length > 200 ? meetingCode[..200] : (meetingCode ?? meetingId),
            Passcode: passcode != null && passcode.Length > 100 ? passcode[..100] : passcode,
            ExternalMeetingId: meetingId != null && meetingId.Length > 500 ? meetingId[..500] : meetingId);
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

    private static string ExtractGraphErrorMessage(string errorJson, string defaultMsg)
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

    public static string ConvertToHtml(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        // If the content already contains common HTML block tags, return as-is
        if (content.Contains("<p>") || content.Contains("<br") || content.Contains("<div>") ||
            content.Contains("<h1>") || content.Contains("<h2>") || content.Contains("<h3>") ||
            content.Contains("<ul>") || content.Contains("<ol>"))
        {
            return content;
        }

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        var inList = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (string.IsNullOrEmpty(line))
            {
                if (inList)
                {
                    sb.Append("</ul>");
                    inList = false;
                }
                sb.Append("<br/>");
                continue;
            }

            // Headings
            if (line.StartsWith("### "))
            {
                if (inList) { sb.Append("</ul>"); inList = false; }
                sb.Append("<h3>").Append(FormatInline(line[4..])).Append("</h3>");
            }
            else if (line.StartsWith("## "))
            {
                if (inList) { sb.Append("</ul>"); inList = false; }
                sb.Append("<h2>").Append(FormatInline(line[3..])).Append("</h2>");
            }
            else if (line.StartsWith("# "))
            {
                if (inList) { sb.Append("</ul>"); inList = false; }
                sb.Append("<h1>").Append(FormatInline(line[2..])).Append("</h1>");
            }
            else if (line.StartsWith("---") || line.StartsWith("***"))
            {
                if (inList) { sb.Append("</ul>"); inList = false; }
                sb.Append("<hr/>");
            }
            else if (line.StartsWith("• ") || line.StartsWith("- ") || line.StartsWith("* "))
            {
                if (!inList)
                {
                    sb.Append("<ul>");
                    inList = true;
                }
                sb.Append("<li>").Append(FormatInline(line[2..])).Append("</li>");
            }
            else if (Regex.IsMatch(line, @"^\d+\.\s+") ||
                     line.Equals("Meeting Summary:", StringComparison.OrdinalIgnoreCase) ||
                     line.Equals("Meeting Summary / MOM:", StringComparison.OrdinalIgnoreCase))
            {
                if (inList) { sb.Append("</ul>"); inList = false; }
                sb.Append("<p><strong>").Append(FormatInline(line)).Append("</strong></p>");
            }
            else
            {
                if (inList)
                {
                    sb.Append("</ul>");
                    inList = false;
                }
                sb.Append("<p>").Append(FormatInline(line)).Append("</p>");
            }
        }

        if (inList)
        {
            sb.Append("</ul>");
        }

        return sb.ToString();
    }

    private static string FormatInline(string text)
    {
        var encoded = WebUtility.HtmlEncode(text);
        // Replace **bold** with <strong>bold</strong>
        encoded = Regex.Replace(encoded, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        // Replace *italic* with <em>italic</em>
        encoded = Regex.Replace(encoded, @"\*(.+?)\*", "<em>$1</em>");
        return encoded;
    }
}
