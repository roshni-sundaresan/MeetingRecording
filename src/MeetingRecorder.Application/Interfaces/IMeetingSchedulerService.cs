using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.DTOs.Common;

namespace MeetingRecorder.Application.Interfaces;

public interface IMeetingSchedulerService
{
    Task<ScheduledMeetingResponse> ScheduleMeetingAsync(Guid userId, ScheduleMeetingRequest request, CancellationToken ct = default);
    Task<PagedResult<ScheduledMeetingResponse>> GetScheduledMeetingsAsync(Guid userId, QueryParameters query, CancellationToken ct = default);
    Task<ScheduledMeetingResponse> GetScheduledMeetingByIdAsync(Guid userId, Guid meetingId, CancellationToken ct = default);
    Task<bool> CancelScheduledMeetingAsync(Guid userId, Guid meetingId, CancellationToken ct = default);
}
