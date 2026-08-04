using MeetingRecorder.Domain;

namespace MeetingRecorder.Application.DTOs;

// ---------- Auth ----------
public record LoginRequest(string Email, string Password);

public record RegisterRequest(string Email, string Name, string Mobile, string Password, string? ProfilePhotoUrl);

public record AuthResponse(string Token, DateTime ExpiresAt, UserResponse User);

// ---------- Users ----------
public record UserResponse(Guid Id, string Email, string Name, string Mobile, string? ProfilePhotoUrl,
    DateTime CreatedDate, DateTime? UpdatedDate, string Role);

public record CreateUserRequest(string Email, string Name, string Mobile, string Password, string? ProfilePhotoUrl);

public record UpdateUserRequest(string Name, string Mobile, string? ProfilePhotoUrl);

// ---------- Structured content blocks (match the Flutter app's models) ----------
public record TranscriptLineDto(string Speaker, string Text, int? TimestampSeconds);

public record ActionItemDto(string Text, bool Done);

public record RecordingNoteDto(string? Id, int StartSeconds, int EndSeconds, string Text, string? ClipPath);

// ---------- Recordings ----------
public record CreateRecordingRequest(Guid UserId, string Title, RecordingType Type, DateTime? CreatedAt,
    TimeSpan? Duration, string? Summary, IReadOnlyList<TranscriptLineDto>? Transcript,
    IReadOnlyList<ActionItemDto>? Actions, IReadOnlyList<RecordingNoteDto>? Notes,
    bool IsRecording, bool Bookmarked, string? FilePath, string? SourceLanguageCode,
    TranscriptionStatus? TranscriptionStatus, int? DurationSeconds = null);

public record UpdateRecordingRequest(string? Title, RecordingType? Type, TimeSpan? Duration, string? Summary,
    IReadOnlyList<TranscriptLineDto>? Transcript, IReadOnlyList<ActionItemDto>? Actions,
    IReadOnlyList<RecordingNoteDto>? Notes, bool? IsRecording, bool? Bookmarked,
    string? FilePath, string? SourceLanguageCode, TranscriptionStatus? TranscriptionStatus);

public record BookmarkRequest(bool Bookmarked);

// ---------- Batch fetch & playback ----------
public record BatchRecordingsRequest(IReadOnlyList<Guid> Ids);

public record RecordingPlaylistItem(Guid Id, string Title, RecordingType Type, TimeSpan Duration, int DurationSeconds,
    string? StreamUrl, string? ContentType, string? SourceLanguageCode, TranscriptionStatus TranscriptionStatus);

public record RecordingResponse(Guid Id, Guid UserId, string Title, RecordingType Type, DateTime CreatedAt,
    TimeSpan Duration, int DurationSeconds, string? Summary,
    IReadOnlyList<TranscriptLineDto>? Transcript, IReadOnlyList<ActionItemDto>? Actions,
    IReadOnlyList<RecordingNoteDto>? Notes,
    bool IsRecording, bool Bookmarked, string? FilePath, string? SourceLanguageCode,
    TranscriptionStatus TranscriptionStatus, DateTime CreatedDate, DateTime? UpdatedDate);

// ---------- Batch upload ----------
public record StartUploadRequest(Guid UserId, string FileName, RecordingType Type, int TotalChunks,
    string? SourceLanguageCode, long? FileSizeBytes, TimeSpan? Duration, int? DurationSeconds = null);

public record StartUploadResponse(Guid BatchId, int TotalChunks, string UploadDirectory);

/// <summary>
/// Chunk metadata arrives as multipart form values (inherently strings).
/// Structured blocks (transcript/actions/notes) are sent as JSON strings
/// and deserialized by the service when the recording is finalized.
/// </summary>
public record UploadChunkRequest(Guid BatchId, int ChunkNumber, int TotalChunks, Guid UserId,
    DateTime? UploadedAt, string? Summary, string? Transcript, string? Actions, string? Notes);

public record UploadStatusResponse(Guid BatchId, Guid UserId, string FileName, int TotalChunks,
    IReadOnlyList<int> ReceivedChunks, bool IsComplete, string Status, long TotalBytesReceived);

public record RetryChunkRequest(Guid BatchId, int ChunkNumber);
