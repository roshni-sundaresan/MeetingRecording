using MeetingRecorder.Domain.Common;
using MeetingRecorder.Domain.Enums;

namespace MeetingRecorder.Domain.Entities;

public class ScheduledMeeting : BaseEntity
{
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public MeetingProvider Provider { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public string JoinUrl { get; set; } = string.Empty;
    public string? MeetingCode { get; set; }
    public string? Passcode { get; set; }
    public string? ExternalMeetingId { get; set; }
    public string? AttendeesJson { get; set; }
    public MeetingStatus Status { get; set; } = MeetingStatus.Scheduled;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
}
