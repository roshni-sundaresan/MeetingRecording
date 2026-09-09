using MeetingRecorder.Application.DTOs;

namespace MeetingRecorder.Application.Interfaces;

public interface ISarvamApiService
{
    Task<IReadOnlyList<TranscriptLineDto>> TranscribeAudioAsync(string filePath, string? languageCode = null, CancellationToken ct = default);
    Task<IReadOnlyList<TranscriptLineDto>> TranscribeAudioAsync(string filePath, string? languageCode, string? apiKeyOverride, CancellationToken ct = default);

    Task<string> SynthesizeTextToSpeechAsync(string text, string? languageCode = null, CancellationToken ct = default);
    Task<string> SynthesizeTextToSpeechAsync(string text, string? languageCode, string? apiKeyOverride, CancellationToken ct = default);

    Task<string?> SummarizeTranscriptAsync(string transcriptText, string? languageCode = null, CancellationToken ct = default);
    Task<string?> SummarizeTranscriptAsync(string transcriptText, string? languageCode, string? apiKeyOverride, CancellationToken ct = default);

    Task<string?> SummarizeTranscriptAsync(IReadOnlyList<TranscriptLineDto> lines, string? languageCode = null, CancellationToken ct = default);
    Task<string?> SummarizeTranscriptAsync(IReadOnlyList<TranscriptLineDto> lines, string? languageCode, string? apiKeyOverride, CancellationToken ct = default);

    Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default);
    Task<ApiKeyValidationResult> ValidateApiKeyWithDetailsAsync(string apiKey, CancellationToken ct = default);
}

public record ApiKeyValidationResult(bool IsValid, string Message, string? ErrorCode = null, int? StatusCode = null);
