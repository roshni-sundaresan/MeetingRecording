using MeetingRecorder.Domain.Common;

namespace MeetingRecorder.Domain.Entities;

public class Recording : BaseEntity
{
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public RecordingType Type { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public TimeSpan Duration { get; set; }
    public string? Summary { get; set; }
    public string? Transcript { get; set; }
    public string? Actions { get; set; }
    public string? Notes { get; set; }
    public bool IsRecording { get; set; }
    public bool Bookmarked { get; set; }
    public string? FilePath { get; set; }
    public string? SourceLanguageCode { get; set; }
    public TranscriptionStatus TranscriptionStatus { get; set; } = TranscriptionStatus.None;

    public User? User { get; set; }
}
