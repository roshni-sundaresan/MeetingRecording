using MeetingRecorder.Domain;

namespace MeetingRecorder.Application.DTOs;

// ---------- Auth ----------
public record LoginRequest(string Email, string Password);

public record RegisterRequest(string Email, string Name, string Mobile, string Password, string? ProfilePhotoUrl);

public record ForgotPasswordRequest(string Email);

public record ResetPasswordRequest(string Email, string ResetToken, string NewPassword);

/// Dev-mode response: the reset token is returned inline (no SMTP provider
/// configured). Production should email the token instead and return only the
/// message.
public record ForgotPasswordResponse(string Message, string? ResetToken, int ExpiresMinutes);

/// <summary>
/// Auth payload returned by login/register/refresh. Includes the short-lived
/// JWT (token_type: Bearer) plus a longer-lived refresh token for silent
/// session renewal via POST /api/auth/refresh.
/// </summary>
public record AuthResponse(string Token, DateTime ExpiresAt, UserResponse User,
    string TokenType = "Bearer", string? RefreshToken = null, DateTime? RefreshExpiresAt = null);

public record RefreshTokenRequest(string RefreshToken);

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

/// <summary>Batch identifier + expected chunk count. The internal storage path
/// is intentionally not exposed to clients.</summary>
public record StartUploadResponse(Guid BatchId, int TotalChunks);

/// <summary>
/// Chunk metadata arrives as multipart form values (inherently strings).
/// Structured blocks (transcript/actions/notes) are sent as JSON strings
/// and deserialized by the service when the recording is finalized.
/// </summary>
public record UploadChunkRequest(Guid BatchId, int ChunkNumber, int TotalChunks, Guid UserId,
    DateTime? UploadedAt, string? Summary, string? Transcript, string? Actions, string? Notes,
    string? ChecksumSha256 = null);

public record UploadStatusResponse(Guid BatchId, Guid UserId, string FileName, int TotalChunks,
    IReadOnlyList<int> ReceivedChunks, bool IsComplete, string Status, long TotalBytesReceived);

public record RetryChunkRequest(Guid BatchId, int ChunkNumber);
