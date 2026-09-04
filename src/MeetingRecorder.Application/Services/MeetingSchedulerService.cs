using System.Text.Json;
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

        var details = await client.CreateMeetingAsync(request, ct);

        var meeting = new ScheduledMeeting
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim(),
            Provider = request.Provider,
            StartTime = request.StartTime.ToUniversalTime(),
            EndTime = request.EndTime.ToUniversalTime(),
            TimeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? "UTC" : request.TimeZone.Trim(),
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

        return MapToResponse(meeting);
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
            Items = items.Select(MapToResponse).ToList(),
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

        return MapToResponse(meeting);
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

    private static ScheduledMeetingResponse MapToResponse(ScheduledMeeting meeting)
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
            CreatedAt: meeting.CreatedAt);
    }
}
