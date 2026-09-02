using MeetingRecorder.Domain;

namespace MeetingRecorder.Application.DTOs;

// ---------- Auth ----------
public record LoginRequest(string Email, string Password);

public record RegisterRequest(string Email, string Name, string Mobile, string Password, string? ProfilePhotoUrl);

// ---------- Password reset (server-authoritative OTP flow) ----------
public record PasswordResetRequestRequest(string Username);

public record VerifyOtpRequest(string ResetRequestId, string Otp);

public record ResendOtpRequest(string Username);

public record CompleteResetRequest(string ResetToken, string NewPassword);

/// <summary>Response for request/resend. The OTP is never included except via
/// <c>DevOtp</c>, which is only populated when PasswordReset:DevOtpExposure is
/// enabled (development environments only).</summary>
public record PasswordResetRequestResponse(string Message, Guid? ResetRequestId, DateTime? ExpiresAt, string? DevOtp = null);

/// <summary>Issued only after successful OTP verification. Short-lived,
/// single-use, bound to the specific user + reset request.</summary>
public record VerifyOtpResponse(string ResetToken, DateTime ExpiresAt);

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

public record UpdateUserRequest(
    string? Email = null,
    string? Name = null,
    string? Mobile = null,
    string? Password = null,
    string? ProfilePhotoUrl = null);

public record UpdateProfileRequest(
    string? Email = null,
    string? Name = null,
    string? Mobile = null,
    string? Password = null,
    string? ProfilePhotoUrl = null);

// ---------- User API Key Settings (Option 1: Custom Key vs Option 2: Buy/System Key) ----------
public record SetApiKeyRequest(string ApiKey, bool Validate = false);

public record UserApiKeyStatusResponse(
    bool HasCustomKey,
    string? MaskedKey,
    string KeySource,
    bool IsSystemKeyConfigured,
    DateTime? UpdatedDate = null);

public record ValidateApiKeyRequest(string ApiKey);

public record ValidateApiKeyResponse(bool IsValid, string? Message);

// ---------- Structured content blocks (match the Flutter app's models) ----------
public record TranscriptLineDto(
    string Speaker,
    string Text,
    int? TimestampSeconds = null,
    int? StartSeconds = null,
    int? EndSeconds = null);

public record ActionItemDto(string Text, bool Done);

public record RecordingNoteDto(string? Id, int StartSeconds, int EndSeconds, string Text, string? ClipPath);

// ---------- Sarvam AI TTS & STT ----------
public record SynthesizeTtsRequest(string Text, string? LanguageCode = null);

public record SynthesizeTtsResponse(string AudioBase64);

public record TranscribeAudioRequest(Guid? RecordingId = null, string? FilePath = null, string? LanguageCode = null);

public record TranscriptionResultResponse(
    Guid? RecordingId,
    string? Title,
    string? FilePath,
    string? Summary,
    IReadOnlyList<TranscriptLineDto> Transcription,
    string? Transcript,
    string? SourceLanguageCode,
    TranscriptionStatus Status = TranscriptionStatus.Completed);

public record UploadAudioRecordingRequest(
    string? Title = null,
    string? LanguageCode = null,
    RecordingType? Type = null,
    Guid? RecordingId = null);

public record SummarizeTextRequest(string Text, string? LanguageCode = null, Guid? RecordingId = null);

public record SummarizeTextResponse(string Summary, Guid? RecordingId = null);

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
