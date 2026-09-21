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

        // Extract or fetch header and meeting MOM / summary if provided
        var headerText = !string.IsNullOrWhiteSpace(request.Header) ? request.Header.Trim() : (!string.IsNullOrWhiteSpace(request.Description) ? request.Description.Trim() : null);
        var momText = !string.IsNullOrWhiteSpace(request.Mom) ? request.Mom.Trim() : (!string.IsNullOrWhiteSpace(request.Summary) ? request.Summary.Trim() : null);

        if (string.IsNullOrWhiteSpace(momText) && request.RecordingId.HasValue)
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
                momText = recording.Summary.Trim();
                _logger.LogInformation("Loaded MOM/summary from recording {RecordingId} for scheduled meeting", request.RecordingId.Value);
            }
            else
            {
                _logger.LogWarning("Recording {RecordingId} does not have a summary yet", request.RecordingId.Value);
            }
        }

        // Build composite description with clean formatting (stripping hashtags, stars, and markdown noise)
        string? effectiveDescription;
        if (!string.IsNullOrWhiteSpace(momText))
        {
            var cleanMom = CleanMarkdown(momText);
            var momSectionTitle = !string.IsNullOrWhiteSpace(request.Mom) ? "Minutes of Meeting (MOM):" : "Meeting Summary:";

            if (!string.IsNullOrWhiteSpace(headerText))
            {
                var cleanHeader = CleanMarkdown(headerText);
                if (cleanHeader.Contains(cleanMom, StringComparison.OrdinalIgnoreCase))
                {
                    effectiveDescription = cleanHeader;
                }
                else
                {
                    effectiveDescription = $"{cleanHeader}\n\n{momSectionTitle}\n{cleanMom}";
                }
            }
            else
            {
                effectiveDescription = !string.IsNullOrWhiteSpace(request.Mom) && !cleanMom.StartsWith("Minutes of Meeting", StringComparison.OrdinalIgnoreCase)
                    ? $"{momSectionTitle}\n{cleanMom}"
                    : cleanMom;
            }
        }
        else
        {
            effectiveDescription = CleanMarkdown(headerText);
        }

        request = request with { Description = string.IsNullOrWhiteSpace(effectiveDescription) ? null : effectiveDescription };

        // If MicrosoftAuth or GoogleAuth or RefreshTokens were passed in request, store/update them on user
        if (user != null)
        {
            var userModified = false;
            if (!string.IsNullOrWhiteSpace(request.MicrosoftAuth))
            {
                user.MicrosoftOAuthKey = request.MicrosoftAuth.Trim();
                user.OAuthKey = request.MicrosoftAuth.Trim();
                userModified = true;
            }

            if (!string.IsNullOrWhiteSpace(request.MicrosoftRefreshToken))
            {
                user.MicrosoftRefreshToken = request.MicrosoftRefreshToken.Trim();
                userModified = true;
            }

            if (!string.IsNullOrWhiteSpace(request.GoogleAuth))
            {
                user.GoogleOAuthKey = request.GoogleAuth.Trim();
                user.OAuthKey = request.GoogleAuth.Trim();
                userModified = true;
            }

            if (!string.IsNullOrWhiteSpace(request.GoogleRefreshToken))
            {
                user.GoogleRefreshToken = request.GoogleRefreshToken.Trim();
                userModified = true;
            }

            if (userModified)
            {
                _uow.Repository<User>().Update(user);
                await _uow.SaveChangesAsync(ct);
            }
        }

        // Determine refresh token for the provider client
        var refreshToken = request.Provider switch
        {
            MeetingProvider.Teams => request.MicrosoftRefreshToken ?? user?.MicrosoftRefreshToken,
            MeetingProvider.GoogleMeet => request.GoogleRefreshToken ?? user?.GoogleRefreshToken,
            _ => null
        };

        // Determine token for the provider client
        var token = request.Provider switch
        {
            MeetingProvider.Teams => request.MicrosoftAuth ?? request.ProviderAccessToken ?? user?.MicrosoftOAuthKey ?? user?.OAuthKey,
            MeetingProvider.GoogleMeet => request.GoogleAuth ?? request.ProviderAccessToken ?? user?.GoogleOAuthKey ?? user?.OAuthKey,
            _ => request.ProviderAccessToken ?? user?.OAuthKey
        };

        // Check if an authorization code was passed in the request
        var authCode = request.Provider switch
        {
            MeetingProvider.GoogleMeet => !string.IsNullOrWhiteSpace(request.GoogleAuthCode) ? request.GoogleAuthCode.Trim() : null,
            MeetingProvider.Teams => !string.IsNullOrWhiteSpace(request.MicrosoftAuthCode) ? request.MicrosoftAuthCode.Trim() : null,
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(authCode))
        {
            _logger.LogInformation("Authorization code provided for provider {Provider}. Exchanging code for access and refresh tokens.", request.Provider);
            var tokenResult = await client.ExchangeAuthCodeAsync(authCode, request.RedirectUri, ct);
            if (tokenResult != null && !string.IsNullOrWhiteSpace(tokenResult.AccessToken))
            {
                token = tokenResult.AccessToken;
                if (!string.IsNullOrWhiteSpace(tokenResult.RefreshToken))
                {
                    refreshToken = tokenResult.RefreshToken;
                }

                if (user != null)
                {
                    if (request.Provider == MeetingProvider.Teams)
                    {
                        user.MicrosoftOAuthKey = tokenResult.AccessToken;
                        user.OAuthKey = tokenResult.AccessToken;
                        if (!string.IsNullOrWhiteSpace(tokenResult.RefreshToken))
                        {
                            user.MicrosoftRefreshToken = tokenResult.RefreshToken;
                        }
                    }
                    else if (request.Provider == MeetingProvider.GoogleMeet)
                    {
                        user.GoogleOAuthKey = tokenResult.AccessToken;
                        user.OAuthKey = tokenResult.AccessToken;
                        if (!string.IsNullOrWhiteSpace(tokenResult.RefreshToken))
                        {
                            user.GoogleRefreshToken = tokenResult.RefreshToken;
                        }
                    }
                    _uow.Repository<User>().Update(user);
                    await _uow.SaveChangesAsync(ct);
                    _logger.LogInformation("Saved tokens from authorization code exchange to user profile {UserId}.", userId);
                }
            }
            else
            {
                _logger.LogWarning("Failed to exchange authorization code with provider {Provider}.", request.Provider);
                throw new AppException($"Failed to exchange authorization code for {request.Provider}. Please verify your OAuth client credentials and redirect URI.", 400, $"{request.Provider.ToString().ToUpperInvariant()}_AUTH_CODE_EXCHANGE_FAILED");
            }
        }

        // Proactive refresh: if access token is missing but refresh token exists, fetch a fresh access token
        if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(refreshToken))
        {
            _logger.LogInformation("No access token provided or stored for provider {Provider}. Attempting proactive refresh with available refresh token.", request.Provider);
            var refreshedToken = await client.RefreshAccessTokenAsync(refreshToken, ct);
            if (!string.IsNullOrWhiteSpace(refreshedToken))
            {
                token = refreshedToken;
                if (user != null)
                {
                    if (request.Provider == MeetingProvider.Teams)
                    {
                        user.MicrosoftOAuthKey = refreshedToken;
                        user.OAuthKey = refreshedToken;
                    }
                    else if (request.Provider == MeetingProvider.GoogleMeet)
                    {
                        user.GoogleOAuthKey = refreshedToken;
                        user.OAuthKey = refreshedToken;
                    }
                    _uow.Repository<User>().Update(user);
                    await _uow.SaveChangesAsync(ct);
                }
            }
        }

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
            _logger.LogWarning("Provider token expired ({ErrorCode}). Checking if refresh token is available for auto-refresh.", ex.ErrorCode);
            string? newAccessToken = null;
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                _logger.LogInformation("Attempting automatic OAuth token refresh for user {UserId} with provider {Provider}.", userId, request.Provider);
                newAccessToken = await client.RefreshAccessTokenAsync(refreshToken, ct);
            }

            if (!string.IsNullOrWhiteSpace(newAccessToken))
            {
                _logger.LogInformation("Token refresh successful. Updating user profile and retrying meeting creation.");
                if (user != null)
                {
                    if (ex.ErrorCode == "MICROSOFT_TOKEN_EXPIRED")
                    {
                        user.MicrosoftOAuthKey = newAccessToken;
                        user.OAuthKey = newAccessToken;
                    }
                    else
                    {
                        user.GoogleOAuthKey = newAccessToken;
                        user.OAuthKey = newAccessToken;
                    }
                    _uow.Repository<User>().Update(user);
                    await _uow.SaveChangesAsync(ct);
                }

                request = request with { ProviderAccessToken = newAccessToken };
                details = await client.CreateMeetingAsync(request, ct);
            }
            else
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

    public async Task<ExchangeOAuthCodeResponse> ExchangeOAuthCodeAsync(
        Guid userId, ExchangeOAuthCodeRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            throw new AppException("'code' is required.", 400);
        }

        var client = _meetingClients.FirstOrDefault(c => c.Provider == request.Provider)
            ?? throw new AppException($"Meeting provider '{request.Provider}' is not supported.", 400);

        var tokenResult = await client.ExchangeAuthCodeAsync(request.Code.Trim(), request.RedirectUri, ct);
        if (tokenResult == null || string.IsNullOrWhiteSpace(tokenResult.AccessToken))
        {
            throw new AppException($"Failed to exchange authorization code for {request.Provider}. Please verify your OAuth client credentials and redirect URI.", 400, $"{request.Provider.ToString().ToUpperInvariant()}_AUTH_CODE_EXCHANGE_FAILED");
        }

        var user = await _uow.Repository<User>().GetByIdAsync(userId, ct);
        if (user != null)
        {
            if (request.Provider == MeetingProvider.Teams)
            {
                user.MicrosoftOAuthKey = tokenResult.AccessToken;
                user.OAuthKey = tokenResult.AccessToken;
                if (!string.IsNullOrWhiteSpace(tokenResult.RefreshToken))
                {
                    user.MicrosoftRefreshToken = tokenResult.RefreshToken;
                }
            }
            else if (request.Provider == MeetingProvider.GoogleMeet)
            {
                user.GoogleOAuthKey = tokenResult.AccessToken;
                user.OAuthKey = tokenResult.AccessToken;
                if (!string.IsNullOrWhiteSpace(tokenResult.RefreshToken))
                {
                    user.GoogleRefreshToken = tokenResult.RefreshToken;
                }
            }
            _uow.Repository<User>().Update(user);
            await _uow.SaveChangesAsync(ct);
            _logger.LogInformation("Saved tokens from dedicated code exchange to user profile {UserId}.", userId);
        }

        return new ExchangeOAuthCodeResponse(
            AccessToken: tokenResult.AccessToken,
            RefreshToken: tokenResult.RefreshToken,
            ExpiresIn: tokenResult.ExpiresIn,
            Provider: request.Provider);
    }
}

