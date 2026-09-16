using System.Text.Json;
using System.Text.RegularExpressions;
using MeetingRecorder.Application.Common;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.DTOs.Common;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Domain.Entities;
using MeetingRecorder.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeetingRecorder.Application.Services;

public class MeetingSchedulerService : IMeetingSchedulerService
{
    private readonly IUnitOfWork _uow;
    private readonly IEnumerable<IMeetingProviderClient> _meetingClients;
    private readonly ILogger<MeetingSchedulerService> _logger;

    public MeetingSchedulerService(
        IUnitOfWork uow,
        IEnumerable<IMeetingProviderClient> meetingClients,
        ILogger<MeetingSchedulerService> logger)
    {
        _uow = uow;
        _meetingClients = meetingClients;
        _logger = logger;
    }

    public async Task<ScheduledMeetingResponse> ScheduleMeetingAsync(
        Guid userId, ScheduleMeetingRequest request, CancellationToken ct = default)
    {
        var client = _meetingClients.FirstOrDefault(c => c.Provider == request.Provider)
            ?? throw new AppException($"No meeting provider client available for '{request.Provider}'.", 400, "UNSUPPORTED_PROVIDER");

        _logger.LogInformation("Scheduling meeting '{Title}' with provider '{Provider}' for user {UserId}",
            request.Title, request.Provider, userId);

        var parsedStart = MeetingTimeHelper.Parse(request.StartTime, request.TimeZone);
        var parsedEnd = MeetingTimeHelper.Parse(request.EndTime, request.TimeZone);

        var user = await _uow.Repository<User>().GetByIdAsync(userId, ct);

        // Extract or fetch meeting summary/MOM if provided
        var summaryToInclude = request.Summary?.Trim();
        if (string.IsNullOrWhiteSpace(summaryToInclude) && request.RecordingId.HasValue)
        {
            var recordingRepo = _uow.Repository<Recording>();
            var recording = await recordingRepo.FirstOrDefaultAsync(r => r.Id == request.RecordingId.Value && !r.IsDeleted, ct)
                ?? throw new NotFoundException(nameof(Recording), request.RecordingId.Value);

            if (user?.Role != "Admin" && recording.UserId != userId)
            {
                throw new AppException("You do not have access to this recording.", 403, "RECORDING_ACCESS_DENIED");
            }

            if (!string.IsNullOrWhiteSpace(recording.Summary))
            {
                summaryToInclude = recording.Summary.Trim();
                _logger.LogInformation("Loaded summary from recording {RecordingId} for scheduled meeting", request.RecordingId.Value);
            }
            else
            {
                _logger.LogWarning("Recording {RecordingId} does not have a summary yet", request.RecordingId.Value);
            }
        }

        // Build composite description with clean formatting (stripping hashtags, stars, and markdown noise)
        string? effectiveDescription;
        if (!string.IsNullOrWhiteSpace(summaryToInclude))
        {
            var cleanSummary = CleanMarkdown(summaryToInclude);
            if (!string.IsNullOrWhiteSpace(request.Description))
            {
                var cleanDesc = CleanMarkdown(request.Description);
                if (cleanDesc.Contains(cleanSummary, StringComparison.OrdinalIgnoreCase))
                {
                    effectiveDescription = cleanDesc;
                }
                else
                {
                    effectiveDescription = $"{cleanDesc}\n\nMeeting Summary:\n{cleanSummary}";
                }
            }
            else
            {
                effectiveDescription = cleanSummary;
            }
        }
        else
        {
            effectiveDescription = CleanMarkdown(request.Description);
        }

        request = request with { Description = string.IsNullOrWhiteSpace(effectiveDescription) ? null : effectiveDescription };

        // If MicrosoftAuth or GoogleAuth was passed in request, store/update them on user
        if (user != null)
        {
            var userModified = false;
            if (!string.IsNullOrWhiteSpace(request.MicrosoftAuth))
            {
                user.MicrosoftOAuthKey = request.MicrosoftAuth.Trim();
                user.OAuthKey = request.MicrosoftAuth.Trim();
                userModified = true;
            }

            if (!string.IsNullOrWhiteSpace(request.GoogleAuth))
            {
                user.GoogleOAuthKey = request.GoogleAuth.Trim();
                user.OAuthKey = request.GoogleAuth.Trim();
                userModified = true;
            }

            if (userModified)
            {
                _uow.Repository<User>().Update(user);
                await _uow.SaveChangesAsync(ct);
            }
        }

        // Determine token for the provider client
        var token = request.Provider switch
        {
            MeetingProvider.Teams => request.MicrosoftAuth ?? request.ProviderAccessToken ?? user?.MicrosoftOAuthKey ?? user?.OAuthKey,
            MeetingProvider.GoogleMeet => request.GoogleAuth ?? request.ProviderAccessToken ?? user?.GoogleOAuthKey ?? user?.OAuthKey,
            _ => request.ProviderAccessToken ?? user?.OAuthKey
        };

        if (!string.IsNullOrWhiteSpace(token))
        {
            request = request with { ProviderAccessToken = token };
            _logger.LogInformation("Using OAuth token for user {UserId} with provider {Provider}", userId, request.Provider);
        }

        MeetingConferenceDetails details;
        try
        {
            details = await client.CreateMeetingAsync(request, ct);
        }
        catch (AppException ex) when (ex.ErrorCode is "MICROSOFT_TOKEN_EXPIRED" or "GOOGLE_TOKEN_EXPIRED")
        {
            if (user != null)
            {
                var userModified = false;
                if (ex.ErrorCode == "MICROSOFT_TOKEN_EXPIRED" && !string.IsNullOrWhiteSpace(user.MicrosoftOAuthKey))
                {
                    user.MicrosoftOAuthKey = null;
                    if (user.OAuthKey == token) user.OAuthKey = null;
                    userModified = true;
                }
                else if (ex.ErrorCode == "GOOGLE_TOKEN_EXPIRED" && !string.IsNullOrWhiteSpace(user.GoogleOAuthKey))
                {
                    user.GoogleOAuthKey = null;
                    if (user.OAuthKey == token) user.OAuthKey = null;
                    userModified = true;
                }

                if (userModified)
                {
                    _uow.Repository<User>().Update(user);
                    await _uow.SaveChangesAsync(ct);
                    _logger.LogInformation("Cleared expired OAuth key from user profile {UserId} for provider {Provider}", userId, request.Provider);
                }
            }
            throw;
        }

        var dbDescription = request.Description?.Trim();
        if (dbDescription != null && dbDescription.Length > 4000)
        {
            dbDescription = dbDescription[..4000];
        }

        var meeting = new ScheduledMeeting
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = request.Title.Trim(),
            Description = dbDescription,
            Provider = request.Provider,
            StartTime = parsedStart.UtcDateTime,
            EndTime = parsedEnd.UtcDateTime,
            TimeZone = parsedStart.TimeZoneId,
            JoinUrl = details.JoinUrl,
            MeetingCode = details.MeetingCode,
            Passcode = details.Passcode,
            ExternalMeetingId = details.ExternalMeetingId,
            AttendeesJson = request.Attendees != null && request.Attendees.Count > 0
                ? JsonSerializer.Serialize(request.Attendees)
                : null,
            Status = MeetingStatus.Scheduled,
            CreatedAt = DateTime.UtcNow
        };

        var repo = _uow.Repository<ScheduledMeeting>();
        repo.Add(meeting);
        await _uow.SaveChangesAsync(ct);

        _logger.LogInformation("Successfully scheduled meeting {MeetingId} with join URL {JoinUrl}",
            meeting.Id, meeting.JoinUrl);

        var microsoftAuth = user?.MicrosoftOAuthKey ?? request.MicrosoftAuth;
        var googleAuth = user?.GoogleOAuthKey ?? request.GoogleAuth;

        return MapToResponse(meeting, microsoftAuth, googleAuth);
    }

    public Task<PagedResult<ScheduledMeetingResponse>> GetScheduledMeetingsAsync(
        Guid userId, QueryParameters query, CancellationToken ct = default)
    {
        QueryGuard.Validate(query, QueryGuard.AllowedMeetingSort);

        var repo = _uow.Repository<ScheduledMeeting>();
        var baseQuery = repo.Query().Where(m => m.UserId == userId && !m.IsDeleted);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            baseQuery = baseQuery.Where(m => m.Title.Contains(term) || (m.Description != null && m.Description.Contains(term)));
        }

        if (query.CreatedAfter.HasValue)
        {
            baseQuery = baseQuery.Where(m => m.CreatedAt >= query.CreatedAfter.Value);
        }

        if (query.CreatedBefore.HasValue)
        {
            baseQuery = baseQuery.Where(m => m.CreatedAt <= query.CreatedBefore.Value);
        }

        var total = baseQuery.Count();

        var sortKey = query.SortBy?.ToLowerInvariant().Replace("_", "") ?? "createdat";
        var isDesc = query.SortOrder.Equals("desc", StringComparison.OrdinalIgnoreCase);

        var sorted = sortKey switch
        {
            "title" => isDesc ? baseQuery.OrderByDescending(m => m.Title) : baseQuery.OrderBy(m => m.Title),
            "starttime" => isDesc ? baseQuery.OrderByDescending(m => m.StartTime) : baseQuery.OrderBy(m => m.StartTime),
            "endtime" => isDesc ? baseQuery.OrderByDescending(m => m.EndTime) : baseQuery.OrderBy(m => m.EndTime),
            "provider" => isDesc ? baseQuery.OrderByDescending(m => m.Provider) : baseQuery.OrderBy(m => m.Provider),
            _ => isDesc ? baseQuery.OrderByDescending(m => m.CreatedAt) : baseQuery.OrderBy(m => m.CreatedAt)
        };

        var items = sorted
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToList();

        var result = new PagedResult<ScheduledMeetingResponse>
        {
            Items = items.Select(m => MapToResponse(m)).ToList(),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = total
        };

        return Task.FromResult(result);
    }

    public async Task<ScheduledMeetingResponse> GetScheduledMeetingByIdAsync(
        Guid userId, Guid meetingId, CancellationToken ct = default)
    {
        var repo = _uow.Repository<ScheduledMeeting>();
        var meeting = await repo.GetByIdAsync(meetingId, ct);

        if (meeting == null || meeting.IsDeleted || meeting.UserId != userId)
        {
            throw new NotFoundException("ScheduledMeeting", meetingId);
        }

        var user = await _uow.Repository<User>().GetByIdAsync(userId, ct);
        return MapToResponse(meeting, user?.MicrosoftOAuthKey, user?.GoogleOAuthKey);
    }

    public async Task<bool> CancelScheduledMeetingAsync(
        Guid userId, Guid meetingId, CancellationToken ct = default)
    {
        var repo = _uow.Repository<ScheduledMeeting>();
        var meeting = await repo.GetByIdAsync(meetingId, ct);

        if (meeting == null || meeting.IsDeleted || meeting.UserId != userId)
        {
            throw new NotFoundException("ScheduledMeeting", meetingId);
        }

        meeting.Status = MeetingStatus.Cancelled;
        meeting.UpdatedDate = DateTime.UtcNow;
        repo.Update(meeting);
        await _uow.SaveChangesAsync(ct);

        return true;
    }

    private static ScheduledMeetingResponse MapToResponse(
        ScheduledMeeting meeting,
        string? microsoftAuth = null,
        string? googleAuth = null)
    {
        IReadOnlyList<string> attendees = Array.Empty<string>();
        if (!string.IsNullOrWhiteSpace(meeting.AttendeesJson))
        {
            try
            {
                attendees = JsonSerializer.Deserialize<List<string>>(meeting.AttendeesJson) ?? new List<string>();
            }
            catch
            {
                attendees = Array.Empty<string>();
            }
        }

        var tz = MeetingTimeHelper.ResolveTimeZone(meeting.TimeZone);
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(meeting.StartTime, tz).ToString("yyyy-MM-ddTHH:mm:ss");
        var localEnd = TimeZoneInfo.ConvertTimeFromUtc(meeting.EndTime, tz).ToString("yyyy-MM-ddTHH:mm:ss");

        return new ScheduledMeetingResponse(
            Id: meeting.Id,
            UserId: meeting.UserId,
            Title: meeting.Title,
            Description: meeting.Description,
            Provider: meeting.Provider,
            StartTime: meeting.StartTime,
            EndTime: meeting.EndTime,
            TimeZone: meeting.TimeZone,
            JoinUrl: meeting.JoinUrl,
            MeetingCode: meeting.MeetingCode,
            Passcode: meeting.Passcode,
            ExternalMeetingId: meeting.ExternalMeetingId,
            Attendees: attendees,
            Status: meeting.Status,
            CreatedAt: meeting.CreatedAt,
            LocalStartTime: localStart,
            LocalEndTime: localEnd,
            MicrosoftAuth: microsoftAuth,
            GoogleAuth: googleAuth);
    }

    public static string CleanMarkdown(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cleaned = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // Strip horizontal rules (---, ***, ___)
            if (Regex.IsMatch(line, @"^(?:---+|\*\*\*+|___+)$"))
                continue;

            // Strip leading markdown heading markers (#, ##, ###, ####, etc.)
            var t = Regex.Replace(line, @"^#{1,6}\s*", "");

            // Convert bullet points (* or - or + followed by space) to clean bullet point (• )
            t = Regex.Replace(t, @"^[*\-+]\s+", "• ");

            // Strip bold and italic markdown markers (**bold**, *italic*, __bold__, _italic_)
            t = Regex.Replace(t, @"\*\*(.+?)\*\*", "$1");
            t = Regex.Replace(t, @"\*(.+?)\*", "$1");
            t = Regex.Replace(t, @"__(.+?)__", "$1");

            cleaned.Add(t);
        }

        var result = string.Join("\n", cleaned);
        // Collapse 3+ consecutive newlines to 2
        result = Regex.Replace(result, @"\n{3,}", "\n\n");
        return result.Trim();
    }
}
