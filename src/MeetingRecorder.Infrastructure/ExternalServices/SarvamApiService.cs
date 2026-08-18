using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingRecorder.Infrastructure.ExternalServices;

public class SarvamApiService : ISarvamApiService
{
    private readonly HttpClient _httpClient;
    private readonly SarvamOptions _options;
    private readonly ILogger<SarvamApiService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public SarvamApiService(HttpClient httpClient, IOptions<SarvamOptions> options, ILogger<SarvamApiService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/'));
        }
    }

    public async Task<IReadOnlyList<TranscriptLineDto>> TranscribeAudioAsync(
        string filePath, string? languageCode = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new NotFoundException("Audio file for transcription", filePath ?? string.Empty);
        }

        var apiKey = GetEffectiveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Sarvam API key is not configured. Returning empty transcript.");
            return Array.Empty<TranscriptLineDto>();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/speech-to-text");
        request.Headers.Add("api-subscription-key", apiKey);

        using var content = new MultipartFormDataContent();
        await using var fileStream = File.OpenRead(filePath);
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(filePath));

        content.Add(streamContent, "file", Path.GetFileName(filePath));
        content.Add(new StringContent(string.IsNullOrWhiteSpace(_options.SttModel) ? "saaras:v3" : _options.SttModel), "model");
        content.Add(new StringContent(string.IsNullOrWhiteSpace(languageCode) ? _options.LanguageCode : languageCode), "language_code");

        request.Content = content;

        _logger.LogInformation("Sending STT request to Sarvam AI for file {FilePath}", filePath);
        var response = await _httpClient.SendAsync(request, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Sarvam STT failed with status {StatusCode}: {Response}", response.StatusCode, responseJson);
            throw new AppException($"Sarvam STT failed with status code {response.StatusCode}.", (int)response.StatusCode, "SARVAM_STT_ERROR");
        }

        return ParseSttResponse(responseJson);
    }

    public async Task<string> SynthesizeTextToSpeechAsync(
        string text, string? languageCode = null, CancellationToken ct = default)
    {
        var cleanedText = text?.Trim();
        if (string.IsNullOrWhiteSpace(cleanedText))
        {
            throw new AppException("Text is required for TTS synthesis.", 400, "VALIDATION_ERROR");
        }

        var apiKey = GetEffectiveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AppException("Sarvam API key is not configured on the server.", 500, "SARVAM_KEY_MISSING");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/text-to-speech");
        request.Headers.Add("api-subscription-key", apiKey);

        var payload = new
        {
            inputs = new[] { cleanedText },
            target_language_code = languageCode ?? _options.LanguageCode,
            speaker = _options.TtsSpeaker,
            model = _options.TtsModel
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        _logger.LogInformation("Sending TTS synthesis request to Sarvam AI for text length {Length}", cleanedText.Length);
        var response = await _httpClient.SendAsync(request, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Sarvam TTS failed with status {StatusCode}: {Response}", response.StatusCode, responseJson);
            throw new AppException($"Sarvam TTS failed with status code {response.StatusCode}.", (int)response.StatusCode, "SARVAM_TTS_ERROR");
        }

        return ExtractAudioBase64(responseJson);
    }

    private string GetEffectiveApiKey()
    {
        // Allow environment variable override
        var envKey = Environment.GetEnvironmentVariable("SARVAM_API_KEY");
        return !string.IsNullOrWhiteSpace(envKey) ? envKey : _options.ApiKey;
    }

    private static IReadOnlyList<TranscriptLineDto> ParseSttResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<TranscriptLineDto>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var results = new List<TranscriptLineDto>();

            // Case 1: Array of entries directly
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    results.Add(ParseSingleEntry(item));
                }
                return results;
            }

            // Case 2: Object with diarized_transcript -> entries
            if (root.TryGetProperty("diarized_transcript", out var diarized) && diarized.ValueKind == JsonValueKind.Object)
            {
                if (diarized.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in entries.EnumerateArray())
                    {
                        results.Add(ParseSingleEntry(item));
                    }
                    return results;
                }
            }

            // Case 3: Object with data -> entries/array
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    results.Add(ParseSingleEntry(item));
                }
                return results;
            }

            // Case 4: Single transcript field
            if (root.TryGetProperty("transcript", out var transcriptProp) && transcriptProp.ValueKind == JsonValueKind.String)
            {
                var text = transcriptProp.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    results.Add(new TranscriptLineDto("Speaker 1", text, 0, 0, null));
                }
            }

            return results;
        }
        catch (JsonException ex)
        {
            throw new AppException("Failed to parse Sarvam STT response JSON.", 500, "JSON_PARSE_ERROR", innerException: ex);
        }
    }

    private static TranscriptLineDto ParseSingleEntry(JsonElement item)
    {
        string speaker = "Speaker 1";
        if (item.TryGetProperty("speaker", out var sProp) && sProp.ValueKind == JsonValueKind.String)
            speaker = sProp.GetString()!;
        else if (item.TryGetProperty("speaker_id", out var sidProp) && sidProp.ValueKind == JsonValueKind.String)
            speaker = sidProp.GetString()!;

        string text = "";
        if (item.TryGetProperty("text", out var tProp) && tProp.ValueKind == JsonValueKind.String)
            text = tProp.GetString()!;
        else if (item.TryGetProperty("transcript", out var trProp) && trProp.ValueKind == JsonValueKind.String)
            text = trProp.GetString()!;

        int? startSec = null;
        if (item.TryGetProperty("start_seconds", out var ssProp) && ssProp.ValueKind == JsonValueKind.Number)
            startSec = (int)Math.Round(ssProp.GetDouble());
        else if (item.TryGetProperty("timestamp", out var tsProp) && tsProp.ValueKind == JsonValueKind.Number)
            startSec = (int)Math.Round(tsProp.GetDouble());
        else if (item.TryGetProperty("timestamp_seconds", out var tssProp) && tssProp.ValueKind == JsonValueKind.Number)
            startSec = (int)Math.Round(tssProp.GetDouble());

        int? endSec = null;
        if (item.TryGetProperty("end_seconds", out var esProp) && esProp.ValueKind == JsonValueKind.Number)
            endSec = (int)Math.Round(esProp.GetDouble());

        return new TranscriptLineDto(speaker, text, startSec, startSec, endSec);
    }

    private static string ExtractAudioBase64(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("audios", out var audios) && audios.ValueKind == JsonValueKind.Array && audios.GetArrayLength() > 0)
        {
            var first = audios[0].GetString();
            if (!string.IsNullOrEmpty(first)) return first;
        }

        if (root.TryGetProperty("audio_base64", out var b64Prop) && b64Prop.ValueKind == JsonValueKind.String)
        {
            var b64 = b64Prop.GetString();
            if (!string.IsNullOrEmpty(b64)) return b64;
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("audio_base64", out var db64) && db64.ValueKind == JsonValueKind.String)
            {
                var val = db64.GetString();
                if (!string.IsNullOrEmpty(val)) return val;
            }
        }

        throw new AppException("No audio base64 data found in Sarvam TTS response.", 500, "SARVAM_TTS_RESPONSE_INVALID");
    }

    private static string GetContentType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            _ => "application/octet-stream"
        };
    }
}
