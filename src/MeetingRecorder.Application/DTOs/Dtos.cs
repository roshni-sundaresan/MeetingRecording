using System.Text.Json.Serialization;
using MeetingRecorder.Domain;
using MeetingRecorder.Domain.Enums;

namespace MeetingRecorder.Application.DTOs;

// ---------- Auth ----------
public record LoginRequest
{
    public string Email { get; init; } = string.Empty;
    public string? Password { get; init; }

    [JsonPropertyName("providerName")]
    public string? ProviderName { get; init; }

    [JsonPropertyName("provider_name")]
    public string? ProviderNameSnake { init => ProviderName = value; }

    [JsonPropertyName("oauthKey")]
    public string? OAuthKey { get; init; }

    [JsonPropertyName("oauth_key")]
    public string? OAuthKeySnake { init => OAuthKey = value; }

    [JsonPropertyName("microsoft_auth")]
    public string? MicrosoftAuth { get; init; }

    [JsonPropertyName("microsoftAuth")]
    public string? MicrosoftAuthCamel { init => MicrosoftAuth = value; }

    [JsonPropertyName("microsoft_auth_key")]
    public string? MicrosoftAuthKey { init => MicrosoftAuth = value; }

    [JsonPropertyName("microsoftAuthKey")]
    public string? MicrosoftAuthKeyCamel { init => MicrosoftAuth = value; }

    [JsonPropertyName("google_auth")]
    public string? GoogleAuth { get; init; }

    [JsonPropertyName("googleAuth")]
    public string? GoogleAuthCamel { init => GoogleAuth = value; }

    [JsonPropertyName("google_auth_key")]
    public string? GoogleAuthKey { init => GoogleAuth = value; }

    [JsonPropertyName("googleAuthKey")]
    public string? GoogleAuthKeyCamel { init => GoogleAuth = value; }

    public LoginRequest() { }

    public LoginRequest(
        string Email,
        string? Password = null,
        string? ProviderName = null,
        string? OAuthKey = null,
        string? MicrosoftAuth = null,
        string? GoogleAuth = null)
    {
        this.Email = Email;
        this.Password = Password;
        this.ProviderName = ProviderName;
        this.OAuthKey = OAuthKey;
        this.MicrosoftAuth = MicrosoftAuth;
        this.GoogleAuth = GoogleAuth;
    }
}

public record RegisterRequest
{
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Mobile { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;

    [JsonPropertyName("profilePhotoUrl")]
    public string? ProfilePhotoUrl { get; init; }

    [JsonPropertyName("profile_photo_url")]
    public string? ProfilePhotoUrlSnake { init => ProfilePhotoUrl = value; }

    public RegisterRequest() { }
    public RegisterRequest(string Email, string Name, string Mobile, string Password, string? ProfilePhotoUrl = null)
    {
        this.Email = Email;
        this.Name = Name;
        this.Mobile = Mobile;
        this.Password = Password;
        this.ProfilePhotoUrl = ProfilePhotoUrl;
    }
}

// ---------- Password reset (server-authoritative OTP flow) ----------
public record PasswordResetRequestRequest
{
    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    public string? Email { init => Username = value ?? string.Empty; }

    public PasswordResetRequestRequest() { }
    public PasswordResetRequestRequest(string Username)
    {
        this.Username = Username;
    }
}

public record VerifyOtpRequest
{
    [JsonPropertyName("resetRequestId")]
    public string? ResetRequestId { get; init; }

    [JsonPropertyName("reset_request_id")]
    public string? ResetRequestIdSnake { init => ResetRequestId = value; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("username")]
    public string? Username { init => Email = value; }

    [JsonPropertyName("otp")]
    public string Otp { get; init; } = string.Empty;

    public VerifyOtpRequest() { }
    public VerifyOtpRequest(string? ResetRequestId = null, string Otp = "", string? Email = null)
    {
        this.ResetRequestId = ResetRequestId;
        this.Otp = Otp;
        this.Email = Email;
    }
}

public record ResendOtpRequest
{
    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    public string? Email { init => Username = value ?? string.Empty; }

    public ResendOtpRequest() { }
    public ResendOtpRequest(string Username)
    {
        this.Username = Username;
    }
}

public record CompleteResetRequest
{
    [JsonPropertyName("resetToken")]
    public string? ResetToken { get; init; }

    [JsonPropertyName("reset_token")]
    public string? ResetTokenSnake { init => ResetToken = value; }

    [JsonPropertyName("token")]
    public string? Token { init => ResetToken ??= value; }

    [JsonPropertyName("newPassword")]
    public string NewPassword { get; init; } = string.Empty;

    [JsonPropertyName("new_password")]
    public string? NewPasswordSnake { init => NewPassword = value ?? string.Empty; }

    [JsonPropertyName("password")]
    public string? Password { init => NewPassword = string.IsNullOrWhiteSpace(NewPassword) ? (value ?? string.Empty) : NewPassword; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("username")]
    public string? Username { init => Email = value; }

    [JsonPropertyName("otp")]
    public string? Otp { get; init; }

    public CompleteResetRequest() { }
    public CompleteResetRequest(string? ResetToken = null, string NewPassword = "", string? Email = null, string? Otp = null)
    {
        this.ResetToken = ResetToken;
        this.NewPassword = NewPassword;
        this.Email = Email;
        this.Otp = Otp;
    }
}

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
    string TokenType = "Bearer", string? RefreshToken = null, DateTime? RefreshExpiresAt = null,
    [property: JsonPropertyName("microsoft_auth")] string? MicrosoftAuth = null,
    [property: JsonPropertyName("google_auth")] string? GoogleAuth = null);

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
public record SetApiKeyRequest
{
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; init; } = string.Empty;

    [JsonPropertyName("api_key")]
    public string ApiKeySnake { init => ApiKey = value; }

    [JsonPropertyName("validate")]
    public bool Validate { get; init; } = false;

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("email_id")]
    public string? EmailIdSnake { init => Email = value; }

    [JsonPropertyName("emailId")]
    public string? EmailIdCamel { init => Email = value; }

    public SetApiKeyRequest() { }

    public SetApiKeyRequest(string ApiKey, bool Validate = false, string? Email = null)
    {
        this.ApiKey = ApiKey;
        this.Validate = Validate;
        this.Email = Email;
    }
}

public record UserApiKeyStatusResponse(
    bool HasCustomKey,
    string? MaskedKey,
    string KeySource,
    bool IsSystemKeyConfigured,
    DateTime? UpdatedDate = null,
    string? ApiKey = null,
    string? Email = null);

public record ValidateApiKeyRequest
{
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; init; } = string.Empty;

    [JsonPropertyName("api_key")]
    public string ApiKeySnake { init => ApiKey = value; }

    public ValidateApiKeyRequest() { }

    public ValidateApiKeyRequest(string ApiKey)
    {
        this.ApiKey = ApiKey;
    }
}

public record ValidateApiKeyResponse(bool IsValid, string? Message, string? ErrorCode = null);

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

// ---------- Meeting Scheduling (Teams & Google Meet) ----------
/// <summary>
/// Request payload to schedule a meeting on Google Meet or Microsoft Teams.
/// </summary>
public record ScheduleMeetingRequest
{
    public string Title { get; init; } = string.Empty;
    public MeetingProvider Provider { get; init; }
    public string StartTime { get; init; } = string.Empty;
    public string EndTime { get; init; } = string.Empty;
    public string? Description { get; init; }
    public IReadOnlyList<string>? Attendees { get; init; }
    public string? TimeZone { get; init; } = "Asia/Kolkata";
    public string? ProviderAccessToken { get; init; }

    [JsonPropertyName("microsoft_auth")]
    public string? MicrosoftAuth { get; init; }

    [JsonPropertyName("microsoftAuth")]
    public string? MicrosoftAuthCamel { init => MicrosoftAuth = value; }

    [JsonPropertyName("microsoft_auth_key")]
    public string? MicrosoftAuthKey { init => MicrosoftAuth = value; }

    [JsonPropertyName("microsoftAuthKey")]
    public string? MicrosoftAuthKeyCamel { init => MicrosoftAuth = value; }

    [JsonPropertyName("google_auth")]
    public string? GoogleAuth { get; init; }

    [JsonPropertyName("googleAuth")]
    public string? GoogleAuthCamel { init => GoogleAuth = value; }

    [JsonPropertyName("google_auth_key")]
    public string? GoogleAuthKey { init => GoogleAuth = value; }

    [JsonPropertyName("googleAuthKey")]
    public string? GoogleAuthKeyCamel { init => GoogleAuth = value; }

    public ScheduleMeetingRequest() { }

    public ScheduleMeetingRequest(
        string Title,
        MeetingProvider Provider,
        string StartTime,
        string EndTime,
        string? Description = null,
        IReadOnlyList<string>? Attendees = null,
        string? TimeZone = "Asia/Kolkata",
        string? ProviderAccessToken = null,
        string? MicrosoftAuth = null,
        string? GoogleAuth = null)
    {
        this.Title = Title;
        this.Provider = Provider;
        this.StartTime = StartTime;
        this.EndTime = EndTime;
        this.Description = Description;
        this.Attendees = Attendees;
        this.TimeZone = TimeZone ?? "Asia/Kolkata";
        this.ProviderAccessToken = ProviderAccessToken;
        this.MicrosoftAuth = MicrosoftAuth;
        this.GoogleAuth = GoogleAuth;
    }
}

public record ScheduledMeetingResponse(
    Guid Id,
    Guid UserId,
    string Title,
    string? Description,
    MeetingProvider Provider,
    DateTime StartTime,
    DateTime EndTime,
    string TimeZone,
    string JoinUrl,
    string? MeetingCode,
    string? Passcode,
    string? ExternalMeetingId,
    IReadOnlyList<string> Attendees,
    MeetingStatus Status,
    DateTime CreatedAt,
    string? LocalStartTime = null,
    string? LocalEndTime = null,
    [property: JsonPropertyName("microsoft_auth")] string? MicrosoftAuth = null,
    [property: JsonPropertyName("google_auth")] string? GoogleAuth = null);
