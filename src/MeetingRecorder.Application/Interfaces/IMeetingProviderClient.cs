using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Domain.Enums;

namespace MeetingRecorder.Application.Interfaces;

public record MeetingConferenceDetails(
    string JoinUrl,
    string? MeetingCode,
    string? Passcode,
    string? ExternalMeetingId);

public interface IMeetingProviderClient
{
    MeetingProvider Provider { get; }
    Task<MeetingConferenceDetails> CreateMeetingAsync(ScheduleMeetingRequest request, CancellationToken ct = default);
}
