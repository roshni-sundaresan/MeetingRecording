using MeetingRecorder.Application.DTOs;

namespace MeetingRecorder.Application.Interfaces;

public interface ISarvamApiService
{
    Task<IReadOnlyList<TranscriptLineDto>> TranscribeAudioAsync(string filePath, string? languageCode = null, CancellationToken ct = default);
    Task<string> SynthesizeTextToSpeechAsync(string text, string? languageCode = null, CancellationToken ct = default);
}
