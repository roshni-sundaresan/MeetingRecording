namespace MeetingRecorder.Domain;

/// <summary>
/// Mirrors the Flutter app's TranscriptionStatus enum names
/// (none / processing / completed / failed) so string-based JSON
/// round-trips without client-side mapping.
/// </summary>
public enum TranscriptionStatus
{
    None = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3
}
