using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingRecorder.Infrastructure.ExternalServices;

public class SarvamApiService : ISarvamApiService
{
    private readonly HttpClient _httpClient;
    private readonly SarvamOptions _options;
    private readonly ILogger<SarvamApiService> _logger;
    private readonly ICurrentUserService? _currentUserService;
    private readonly IUnitOfWork? _uow;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public SarvamApiService(
        HttpClient httpClient,
        IOptions<SarvamOptions> options,
        ILogger<SarvamApiService> logger,
        ICurrentUserService? currentUserService = null,
        IUnitOfWork? uow = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _currentUserService = currentUserService;
        _uow = uow;

        if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/'));
        }
    }

    public Task<IReadOnlyList<TranscriptLineDto>> TranscribeAudioAsync(
        string filePath, string? languageCode = null, CancellationToken ct = default)
        => TranscribeAudioAsync(filePath, languageCode, null, ct);

    public async Task<IReadOnlyList<TranscriptLineDto>> TranscribeAudioAsync(
        string filePath, string? languageCode, string? apiKeyOverride, CancellationToken ct = default)
    {
        var resolvedPath = string.IsNullOrWhiteSpace(filePath)
            ? string.Empty
            : (Path.IsPathRooted(filePath) ? filePath : Path.GetFullPath(filePath));

        if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
        {
            throw new NotFoundException("Audio file for transcription", filePath ?? string.Empty);
        }

        var apiKey = await GetEffectiveApiKeyAsync(apiKeyOverride, ct);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Sarvam API key is not configured. Returning empty transcript.");
            return Array.Empty<TranscriptLineDto>();
        }

        var baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl) ? "https://api.sarvam.ai" : _options.BaseUrl.TrimEnd('/');

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/speech-to-text");
        request.Headers.Add("api-subscription-key", apiKey);

        using var content = new MultipartFormDataContent();
        await using var fileStream = File.OpenRead(resolvedPath);
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(resolvedPath));

        content.Add(streamContent, "file", Path.GetFileName(resolvedPath));
        var normalizedLang = NormalizeLanguageCode(languageCode);
        content.Add(new StringContent(string.IsNullOrWhiteSpace(_options.SttModel) ? "saaras:v3" : _options.SttModel), "model");
        content.Add(new StringContent(normalizedLang), "language_code");

        request.Content = content;

        try
        {
            _logger.LogInformation("Sending REST STT request to Sarvam AI for file {FilePath} (Language: {Language})", resolvedPath, normalizedLang);
            var response = await _httpClient.SendAsync(request, ct);
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                return ParseSttResponse(responseJson);
            }

            _logger.LogWarning("Sarvam REST STT returned status {StatusCode}: {Response}. Falling back to Sarvam Batch STT API.", response.StatusCode, responseJson);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Sarvam REST STT encountered an error. Falling back to Sarvam Batch STT API.");
        }

        // Fallback to Batch STT API (handles long-form audio up to 2 hours with speaker diarization)
        return await TranscribeAudioBatchAsync(resolvedPath, normalizedLang, apiKey, ct);
    }

    private async Task<IReadOnlyList<TranscriptLineDto>> TranscribeAudioBatchAsync(
        string resolvedPath, string languageCode, string apiKey, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl) ? "https://api.sarvam.ai" : _options.BaseUrl.TrimEnd('/');
        _logger.LogInformation("Starting Sarvam Batch STT job for file {FilePath} (Language: {Language})", resolvedPath, languageCode);

        // 1. Initialize Batch Job
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/speech-to-text/job/v1");
        createRequest.Headers.Add("api-subscription-key", apiKey);
        var createPayload = new
        {
            job_parameters = new
            {
                language_code = languageCode,
                model = string.IsNullOrWhiteSpace(_options.SttModel) ? "saaras:v3" : _options.SttModel,
                with_diarization = true
            }
        };
        createRequest.Content = new StringContent(JsonSerializer.Serialize(createPayload), Encoding.UTF8, "application/json");

        var createResponse = await _httpClient.SendAsync(createRequest, ct);
        var createJson = await createResponse.Content.ReadAsStringAsync(ct);
        if (!createResponse.IsSuccessStatusCode)
        {
            _logger.LogError("Sarvam Batch STT job initialization failed with status {StatusCode}: {Response}", createResponse.StatusCode, createJson);
            throw new AppException($"Failed to initialize Sarvam batch STT job: {createJson}", (int)createResponse.StatusCode, "SARVAM_BATCH_INIT_ERROR");
        }

        using var createDoc = JsonDocument.Parse(createJson);
        if (!createDoc.RootElement.TryGetProperty("job_id", out var jobIdProp))
        {
            throw new AppException("No job_id returned in Sarvam Batch STT initialization response.", 500, "SARVAM_BATCH_INVALID_JOB_ID");
        }
        var jobId = jobIdProp.GetString()!;
        var fileName = Path.GetFileName(resolvedPath);

        // 2. Request presigned upload URL
        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/speech-to-text/job/v1/upload-files");
        uploadRequest.Headers.Add("api-subscription-key", apiKey);
        var uploadPayload = new
        {
            job_id = jobId,
            files = new[] { fileName }
        };
        uploadRequest.Content = new StringContent(JsonSerializer.Serialize(uploadPayload), Encoding.UTF8, "application/json");

        var uploadResponse = await _httpClient.SendAsync(uploadRequest, ct);
        var uploadJson = await uploadResponse.Content.ReadAsStringAsync(ct);
        if (!uploadResponse.IsSuccessStatusCode)
        {
            _logger.LogError("Sarvam Batch STT upload-files failed with status {StatusCode}: {Response}", uploadResponse.StatusCode, uploadJson);
            throw new AppException($"Failed to obtain Sarvam batch upload URL: {uploadJson}", (int)uploadResponse.StatusCode, "SARVAM_BATCH_UPLOAD_URL_ERROR");
        }

        using var uploadDoc = JsonDocument.Parse(uploadJson);
        string? fileUploadUrl = null;
        if (uploadDoc.RootElement.TryGetProperty("upload_urls", out var uploadUrls) && uploadUrls.TryGetProperty(fileName, out var fileEntry))
        {
            if (fileEntry.TryGetProperty("file_url", out var fuProp))
            {
                fileUploadUrl = fuProp.GetString();
            }
        }

        if (string.IsNullOrWhiteSpace(fileUploadUrl))
        {
            throw new AppException("No file_url returned for Sarvam batch upload.", 500, "SARVAM_BATCH_NO_UPLOAD_URL");
        }

        // 3. Upload audio file stream to Azure Blob
        using (var putRequest = new HttpRequestMessage(HttpMethod.Put, fileUploadUrl))
        {
            putRequest.Headers.Add("x-ms-blob-type", "BlockBlob");
            await using var fileStream = File.OpenRead(resolvedPath);
            putRequest.Content = new StreamContent(fileStream);
            putRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(resolvedPath));

            using var putClient = new HttpClient();
            var putResponse = await putClient.SendAsync(putRequest, ct);
            if (!putResponse.IsSuccessStatusCode)
            {
                var putErr = await putResponse.Content.ReadAsStringAsync(ct);
                _logger.LogError("Failed to upload audio to Sarvam Azure Blob ({StatusCode}): {Response}", putResponse.StatusCode, putErr);
                throw new AppException($"Failed to upload audio to Sarvam storage: {putResponse.StatusCode}", (int)putResponse.StatusCode, "SARVAM_BATCH_BLOB_UPLOAD_ERROR");
            }
        }

        // 4. Start the Batch Job
        using var startRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/speech-to-text/job/v1/{jobId}/start");
        startRequest.Headers.Add("api-subscription-key", apiKey);
        startRequest.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var startResponse = await _httpClient.SendAsync(startRequest, ct);
        if (!startResponse.IsSuccessStatusCode)
        {
            var startErr = await startResponse.Content.ReadAsStringAsync(ct);
            _logger.LogError("Sarvam Batch STT start failed ({StatusCode}): {Response}", startResponse.StatusCode, startErr);
            throw new AppException($"Failed to start Sarvam batch job: {startErr}", (int)startResponse.StatusCode, "SARVAM_BATCH_START_ERROR");
        }

        // 5. Poll Job Status (timeout after 5 minutes)
        var maxPollingTime = TimeSpan.FromMinutes(5);
        var startTime = DateTime.UtcNow;
        string? outputFileName = null;

        while (DateTime.UtcNow - startTime < maxPollingTime && !ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct);

            using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/speech-to-text/job/v1/{jobId}/status");
            statusRequest.Headers.Add("api-subscription-key", apiKey);

            var statusResponse = await _httpClient.SendAsync(statusRequest, ct);
            var statusJson = await statusResponse.Content.ReadAsStringAsync(ct);

            if (statusResponse.IsSuccessStatusCode)
            {
                using var statusDoc = JsonDocument.Parse(statusJson);
                var jobState = statusDoc.RootElement.TryGetProperty("job_state", out var js) ? js.GetString() : null;

                if (string.Equals(jobState, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    if (statusDoc.RootElement.TryGetProperty("job_details", out var jobDetails) && jobDetails.GetArrayLength() > 0)
                    {
                        var firstDetail = jobDetails[0];
                        if (firstDetail.TryGetProperty("outputs", out var outputs) && outputs.GetArrayLength() > 0)
                        {
                            var firstOutput = outputs[0];
                            if (firstOutput.TryGetProperty("file_name", out var fnProp))
                            {
                                outputFileName = fnProp.GetString();
                            }
                        }
                    }
                    break;
                }
                else if (string.Equals(jobState, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    var errMsg = statusDoc.RootElement.TryGetProperty("error_message", out var em) ? em.GetString() : "Unknown error";
                    throw new AppException($"Sarvam batch STT job failed: {errMsg}", 500, "SARVAM_BATCH_JOB_FAILED");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(outputFileName))
        {
            throw new AppException("Sarvam batch job timed out or did not produce an output file.", 504, "SARVAM_BATCH_TIMEOUT");
        }

        // 6. Get Download URL for output
        using var dlRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/speech-to-text/job/v1/download-files");
        dlRequest.Headers.Add("api-subscription-key", apiKey);
        var dlPayload = new
        {
            job_id = jobId,
            files = new[] { outputFileName }
        };
        dlRequest.Content = new StringContent(JsonSerializer.Serialize(dlPayload), Encoding.UTF8, "application/json");

        var dlResponse = await _httpClient.SendAsync(dlRequest, ct);
        var dlJson = await dlResponse.Content.ReadAsStringAsync(ct);
        if (!dlResponse.IsSuccessStatusCode)
        {
            _logger.LogError("Sarvam Batch STT download-files failed ({StatusCode}): {Response}", dlResponse.StatusCode, dlJson);
            throw new AppException($"Failed to get download URL for Sarvam batch results: {dlJson}", (int)dlResponse.StatusCode, "SARVAM_BATCH_DOWNLOAD_URL_ERROR");
        }

        using var dlDoc = JsonDocument.Parse(dlJson);
        string? resultDownloadUrl = null;
        if (dlDoc.RootElement.TryGetProperty("download_urls", out var dlUrls) && dlUrls.TryGetProperty(outputFileName, out var dlEntry))
        {
            if (dlEntry.TryGetProperty("file_url", out var fu))
            {
                resultDownloadUrl = fu.GetString();
            }
        }

        if (string.IsNullOrWhiteSpace(resultDownloadUrl))
        {
            throw new AppException("No download URL returned for Sarvam batch transcript.", 500, "SARVAM_BATCH_NO_RESULT_URL");
        }

        // 7. Download result JSON
        using var resultClient = new HttpClient();
        var transcriptResultJson = await resultClient.GetStringAsync(resultDownloadUrl, ct);

        _logger.LogInformation("Successfully received Sarvam Batch STT transcript for job {JobId}", jobId);
        return ParseSttResponse(transcriptResultJson);
    }

    public Task<string?> SummarizeTranscriptAsync(
        IReadOnlyList<TranscriptLineDto> lines, string? languageCode = null, CancellationToken ct = default)
        => SummarizeTranscriptAsync(lines, languageCode, null, ct);

    public async Task<string?> SummarizeTranscriptAsync(
        IReadOnlyList<TranscriptLineDto> lines, string? languageCode, string? apiKeyOverride, CancellationToken ct = default)
    {
        if (lines == null || lines.Count == 0)
            return null;

        var fullText = string.Join("\n", lines.Select(l => $"{l.Speaker}: {l.Text}")).Trim();
        return await SummarizeTranscriptAsync(fullText, languageCode, apiKeyOverride, ct);
    }

    public Task<string?> SummarizeTranscriptAsync(
        string transcriptText, string? languageCode = null, CancellationToken ct = default)
        => SummarizeTranscriptAsync(transcriptText, languageCode, null, ct);

    public async Task<string?> SummarizeTranscriptAsync(
        string transcriptText, string? languageCode, string? apiKeyOverride, CancellationToken ct = default)
    {
        var cleanedText = transcriptText?.Trim();
        if (string.IsNullOrWhiteSpace(cleanedText))
        {
            return null;
        }

        var apiKey = await GetEffectiveApiKeyAsync(apiKeyOverride, ct);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Sarvam API key is not configured. Generating fallback summary from transcript.");
            return GenerateFallbackSummary(cleanedText);
        }

        try
        {
            var baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl) ? "https://api.sarvam.ai" : _options.BaseUrl.TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions");
            request.Headers.Add("api-subscription-key", apiKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var model = string.IsNullOrWhiteSpace(_options.SummaryModel) ? "sarvam-105b" : _options.SummaryModel;

            var systemPrompt = "You are an expert AI meeting assistant. Your task is to generate a comprehensive, clear, and structured Minutes of Meeting (MOM) / Summary from the provided audio transcription.\n\n" +
                "STRICT OUTPUT RULES:\n" +
                "1. Output ONLY the clean markdown summary. Do NOT include ANY conversational greetings, intros, or preambles (e.g., 'Of course', 'Below is...', 'Here is a structured summary...', 'Certainly').\n" +
                "2. Do NOT include ANY disclaimers, commentary about transcript quality/length/nature, or trailing notes (e.g., '**Disclaimer:**', '***Note:***', or explanations about lyrics/partial audio).\n" +
                "3. Do NOT include bracketed metadata placeholders or empty headers (e.g., '[Date of Meeting]', '[Time of Meeting]', '[Not specified]', '[Name, Pooja - Speaker 1]').\n" +
                "4. Start IMMEDIATELY with the first section header: '### **1. Executive Summary / Overview**'.\n\n" +
                "MOM STRUCTURE TO FOLLOW:\n" +
                "### **1. Executive Summary / Overview**\n" +
                "[Concise paragraph summarizing the core purpose, discussion, and context of the meeting]\n\n" +
                "### **2. Key Discussion Points**\n" +
                "*   **[Point Title / Topic]:** [Clear explanation or details discussed]\n" +
                "*   **[Point Title / Topic]:** [Clear explanation or details discussed]\n\n" +
                "### **3. Decisions Made & Action Items**\n" +
                "*   **[Action Item / Decision / Next Step]:** [Concise details or next steps discussed]";

            var userPrompt = $"Generate a clean, structured Minutes of Meeting (MOM) for the following transcript without any preambles, disclaimers, or metadata placeholders:\n\n{cleanedText}";

            var payload = new
            {
                model = model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.3,
                max_tokens = 2048
            };

            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(50));

            _logger.LogInformation("Sending MOM/Summary chat completion request to Sarvam AI with model {Model} for transcript length {Length}", model, cleanedText.Length);
            var response = await _httpClient.SendAsync(request, cts.Token);
            var responseJson = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Sarvam chat completion summary returned status {StatusCode}: {Response}. Using structured fallback MOM.", response.StatusCode, responseJson);
                return GenerateFallbackSummary(cleanedText);
            }

            var extractedSummary = ExtractChatCompletionContent(responseJson);
            if (!string.IsNullOrWhiteSpace(extractedSummary))
            {
                var sanitized = SanitizeSummary(extractedSummary);
                if (!string.IsNullOrWhiteSpace(sanitized))
                {
                    return sanitized;
                }
            }

            return GenerateFallbackSummary(cleanedText);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception while generating summary via Sarvam AI. Using structured fallback MOM.");
            return GenerateFallbackSummary(cleanedText);
        }
    }

    public Task<string> SynthesizeTextToSpeechAsync(
        string text, string? languageCode = null, CancellationToken ct = default)
        => SynthesizeTextToSpeechAsync(text, languageCode, null, ct);

    public async Task<string> SynthesizeTextToSpeechAsync(
        string text, string? languageCode, string? apiKeyOverride, CancellationToken ct = default)
    {
        var cleanedText = text?.Trim();
        if (string.IsNullOrWhiteSpace(cleanedText))
        {
            throw new AppException("Text is required for TTS synthesis.", 400, "VALIDATION_ERROR");
        }

        var apiKey = await GetEffectiveApiKeyAsync(apiKeyOverride, ct);
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

    public async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        var result = await ValidateApiKeyWithDetailsAsync(apiKey, ct);
        return result.IsValid;
    }

    public async Task<ApiKeyValidationResult> ValidateApiKeyWithDetailsAsync(string apiKey, CancellationToken ct = default)
    {
        var cleanKey = apiKey?.Trim();
        if (string.IsNullOrWhiteSpace(cleanKey) || cleanKey.Length < 10)
        {
            return new ApiKeyValidationResult(
                IsValid: false,
                Message: "API key must be at least 10 characters long.",
                ErrorCode: "KEY_TOO_SHORT",
                StatusCode: 400);
        }

        try
        {
            var baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl) ? "https://api.sarvam.ai" : _options.BaseUrl.TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/text-to-speech");
            request.Headers.Add("api-subscription-key", cleanKey);

            var payload = new
            {
                inputs = new[] { "hi" },
                target_language_code = "en-IN",
                speaker = "pooja",
                model = "bulbul:v3"
            };

            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var response = await _httpClient.SendAsync(request, cts.Token);
            var responseJson = await response.Content.ReadAsStringAsync(cts.Token);

            if (response.IsSuccessStatusCode)
            {
                return new ApiKeyValidationResult(
                    IsValid: true,
                    Message: "API key is valid.",
                    StatusCode: (int)response.StatusCode);
            }

            // Parse Sarvam's error response
            string? sarvamMessage = null;
            string? sarvamCode = null;
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.Object)
                {
                    if (errProp.TryGetProperty("message", out var mProp)) sarvamMessage = mProp.GetString();
                    if (errProp.TryGetProperty("code", out var cProp)) sarvamCode = cProp.GetString();
                }
            }
            catch { }

            // 1. Account quota / payment / credit check
            if (response.StatusCode == System.Net.HttpStatusCode.PaymentRequired ||
                string.Equals(sarvamCode, "insufficient_quota_error", StringComparison.OrdinalIgnoreCase) ||
                (sarvamMessage != null && sarvamMessage.Contains("credits", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("Sarvam API key has insufficient credits: {Message}", sarvamMessage);
                return new ApiKeyValidationResult(
                    IsValid: false,
                    Message: "API key is valid, but the Sarvam account has no remaining credits (insufficient quota). Please recharge credits on your Sarvam dashboard.",
                    ErrorCode: "INSUFFICIENT_QUOTA",
                    StatusCode: 402);
            }

            // 2. Invalid or unauthorized key
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                string.Equals(sarvamCode, "invalid_api_key_error", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Sarvam API key unauthorized: {Message}", sarvamMessage);
                return new ApiKeyValidationResult(
                    IsValid: false,
                    Message: "API key is invalid or unauthorized.",
                    ErrorCode: "INVALID_API_KEY",
                    StatusCode: (int)response.StatusCode);
            }

            _logger.LogWarning("Sarvam API key validation failed with status {StatusCode}: {Error}", response.StatusCode, responseJson);
            return new ApiKeyValidationResult(
                IsValid: false,
                Message: !string.IsNullOrWhiteSpace(sarvamMessage) ? sarvamMessage : "API key validation failed.",
                ErrorCode: sarvamCode ?? "VALIDATION_FAILED",
                StatusCode: (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while validating Sarvam API key against external service.");
            return new ApiKeyValidationResult(
                IsValid: false,
                Message: "Unable to connect to Sarvam AI validation service.",
                ErrorCode: "SERVICE_UNAVAILABLE",
                StatusCode: 503);
        }
    }

    private async Task<string> GetEffectiveApiKeyAsync(string? apiKeyOverride = null, CancellationToken ct = default)
    {
        // 1. Explicit override passed by caller
        if (!string.IsNullOrWhiteSpace(apiKeyOverride) && apiKeyOverride.Trim().Length >= 10)
        {
            return apiKeyOverride.Trim();
        }

        // 2. Check if current authenticated user has a custom API key
        if (_currentUserService?.UserId is Guid userId && _uow is not null)
        {
            try
            {
                var user = await _uow.Repository<User>().FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct);
                if (!string.IsNullOrWhiteSpace(user?.CustomApiKey) && user.CustomApiKey.Trim().Length >= 10)
                {
                    return user.CustomApiKey.Trim();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to retrieve custom API key for user {UserId}. Falling back to system default.", userId);
            }
        }

        // 3. Environment variable override
        var envKey = Environment.GetEnvironmentVariable("SARVAM_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey) && envKey.Trim().Length >= 10)
        {
            return envKey.Trim();
        }

        // 4. Default configuration key
        return _options.ApiKey?.Trim() ?? string.Empty;
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

            // Case 2: Object with diarized_transcript (Array or Object with entries)
            if (root.TryGetProperty("diarized_transcript", out var diarized))
            {
                if (diarized.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in diarized.EnumerateArray())
                    {
                        results.Add(ParseSingleEntry(item));
                    }
                    return results;
                }
                if (diarized.ValueKind == JsonValueKind.Object && diarized.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in entries.EnumerateArray())
                    {
                        results.Add(ParseSingleEntry(item));
                    }
                    return results;
                }
            }

            // Case 3: Object with data -> entries/array
            if (root.TryGetProperty("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        results.Add(ParseSingleEntry(item));
                    }
                    return results;
                }
                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("entries", out var dEntries) && dEntries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dEntries.EnumerateArray())
                    {
                        results.Add(ParseSingleEntry(item));
                    }
                    return results;
                }
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
        {
            speaker = sProp.GetString()!;
        }
        else if (item.TryGetProperty("speaker_id", out var sidProp))
        {
            if (sidProp.ValueKind == JsonValueKind.String)
            {
                var sid = sidProp.GetString();
                speaker = !string.IsNullOrWhiteSpace(sid) ? (sid.StartsWith("Speaker", StringComparison.OrdinalIgnoreCase) ? sid : $"Speaker {sid}") : "Speaker 1";
            }
            else if (sidProp.ValueKind == JsonValueKind.Number)
            {
                speaker = $"Speaker {sidProp.GetInt32() + 1}";
            }
        }

        string text = "";
        if (item.TryGetProperty("text", out var tProp) && tProp.ValueKind == JsonValueKind.String)
            text = tProp.GetString()!;
        else if (item.TryGetProperty("transcript", out var trProp) && trProp.ValueKind == JsonValueKind.String)
            text = trProp.GetString()!;
        else if (item.TryGetProperty("content", out var cProp) && cProp.ValueKind == JsonValueKind.String)
            text = cProp.GetString()!;

        int? startSec = null;
        if (item.TryGetProperty("start_time_seconds", out var stsProp) && stsProp.ValueKind == JsonValueKind.Number)
            startSec = (int)Math.Round(stsProp.GetDouble());
        else if (item.TryGetProperty("start_seconds", out var ssProp) && ssProp.ValueKind == JsonValueKind.Number)
            startSec = (int)Math.Round(ssProp.GetDouble());
        else if (item.TryGetProperty("timestamp", out var tsProp) && tsProp.ValueKind == JsonValueKind.Number)
            startSec = (int)Math.Round(tsProp.GetDouble());
        else if (item.TryGetProperty("timestamp_seconds", out var tssProp) && tssProp.ValueKind == JsonValueKind.Number)
            startSec = (int)Math.Round(tssProp.GetDouble());

        int? endSec = null;
        if (item.TryGetProperty("end_time_seconds", out var etsProp) && etsProp.ValueKind == JsonValueKind.Number)
            endSec = (int)Math.Round(etsProp.GetDouble());
        else if (item.TryGetProperty("end_seconds", out var esProp) && esProp.ValueKind == JsonValueKind.Number)
            endSec = (int)Math.Round(esProp.GetDouble());

        return new TranscriptLineDto(speaker, text, startSec, startSec, endSec);
    }

    private static string? ExtractChatCompletionContent(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                {
                    if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        var text = content.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                    }
                    if (message.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
                    {
                        var text = reasoning.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                    }
                }
                if (firstChoice.TryGetProperty("text", out var choiceText) && choiceText.ValueKind == JsonValueKind.String)
                {
                    var text = choiceText.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }

            if (root.TryGetProperty("summary", out var summaryProp) && summaryProp.ValueKind == JsonValueKind.String)
            {
                var text = summaryProp.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public static string SanitizeSummary(string? rawSummary)
    {
        if (string.IsNullOrWhiteSpace(rawSummary))
            return string.Empty;

        var text = rawSummary.Trim();

        // 1. Remove disclaimer blocks (e.g., "**Disclaimer:** The provided transcript appears to be...")
        text = Regex.Replace(text, @"(?is)\*{0,2}Disclaimer:\*{0,2}[\s\S]*?(?=(#{1,4}|\*{2}\d+\.|\n\n\n|\Z))", "", RegexOptions.IgnoreCase);

        // 2. Remove trailing note blocks (e.g., "***Note:*** This summary is based on a partial transcript...")
        text = Regex.Replace(text, @"(?is)(\*{1,3}|__)?\s*Note:\s*(\*{1,3}|__)?[\s\S]*$", "", RegexOptions.IgnoreCase);

        // 3. Find starting position of first section header (e.g., "1. Executive Summary" or "Executive Summary")
        var firstSectionMatch = Regex.Match(text, @"(?m)^(?:#{1,4}\s*)?(?:\*{1,2})?(?:1[\.\)]\s*)?Executive Summary(?:\s*/\s*Overview)?(?:\*{1,2})?", RegexOptions.IgnoreCase);
        if (firstSectionMatch.Success && firstSectionMatch.Index > 0)
        {
            text = text.Substring(firstSectionMatch.Index);
        }
        else
        {
            var alternateHeaderMatch = Regex.Match(text, @"(?m)^(?:#{1,4}\s*)?(?:\*{1,2})?1[\.\)]\s+", RegexOptions.IgnoreCase);
            if (alternateHeaderMatch.Success && alternateHeaderMatch.Index > 0)
            {
                text = text.Substring(alternateHeaderMatch.Index);
            }
        }

        // 4. Process line-by-line to remove unwanted headers/metadata and normalize section headers
        var rawLines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var cleanedLines = new List<string>();
        bool contentStarted = false;

        foreach (var line in rawLines)
        {
            var trimmedLine = line.Trim();

            // Skip leading blank lines
            if (!contentStarted && string.IsNullOrWhiteSpace(trimmedLine))
                continue;

            // Skip conversational filler if content hasn't started
            if (!contentStarted)
            {
                if (Regex.IsMatch(trimmedLine, @"^(?:Of course|Certainly|Sure|Here is|Below is|Here's|As requested|Based on the provided)\b", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(trimmedLine, @"^(?:---+|\*\*\*+|___+)$") ||
                    Regex.IsMatch(trimmedLine, @"^(?:\*{1,2}|__)?(?:Minutes of Meeting|MOM|Meeting Minutes|Meeting Summary)(?:\s*/\s*Summary)?(?:\*{1,2}|__)?$", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(trimmedLine, @"^(?:\*{1,2}|__)?(?:Date|Time|Attendees|Meeting Type|Subject|Topic)\s*:\s*(?:\*{1,2}|__)?(?:\s*\[.*\]|\s*Not specified|\s*None|\s*N/A|\s*)$", RegexOptions.IgnoreCase))
                {
                    continue;
                }
            }

            // Remove placeholder metadata lines anywhere
            if (Regex.IsMatch(trimmedLine, @"^(?:\*{1,2}|__)?(?:Date|Time|Attendees|Meeting Type|Subject|Topic)\s*:\s*(?:\*{1,2}|__)?\s*(\[.*\]|Not specified|None|N/A)\s*$", RegexOptions.IgnoreCase))
            {
                continue;
            }

            // Remove standalone top title banners like "### **Minutes of Meeting / MOM**" or "**Meeting Minutes / Summary**"
            if (Regex.IsMatch(trimmedLine, @"^(?:#{1,4}\s*)?(?:\*{1,2}|__)?(?:Minutes of Meeting|MOM|Meeting Minutes)(?:\s*/\s*Summary)?(?:\*{1,2}|__)?$", RegexOptions.IgnoreCase))
            {
                continue;
            }

            // Remove horizontal rules before content
            if (!contentStarted && Regex.IsMatch(trimmedLine, @"^(?:---+|\*\*\*+|___+)$"))
            {
                continue;
            }

            // Normalize Section 1 Header
            if (Regex.IsMatch(trimmedLine, @"^(?:#{1,4}\s*)?(?:\*{1,2})?(?:1[\.\)]\s*)?Executive Summary(?:\s*/\s*Overview)?(?:\*{1,2})?:?$", RegexOptions.IgnoreCase))
            {
                cleanedLines.Add("### **1. Executive Summary / Overview**");
                contentStarted = true;
                continue;
            }

            // Normalize Section 2 Header
            if (Regex.IsMatch(trimmedLine, @"^(?:#{1,4}\s*)?(?:\*{1,2})?(?:2[\.\)]\s*)?Key Discussion Points?(?:\*{1,2})?:?$", RegexOptions.IgnoreCase))
            {
                cleanedLines.Add("### **2. Key Discussion Points**");
                contentStarted = true;
                continue;
            }

            // Normalize Section 3 Header
            if (Regex.IsMatch(trimmedLine, @"^(?:#{1,4}\s*)?(?:\*{1,2})?(?:3[\.\)]\s*)?(?:Decisions Made\s*&?\s*Action Items|Decisions and Action Items|Action Items\s*&?\s*Next Steps|Action Items|Decisions Made)(?:\*{1,2})?:?$", RegexOptions.IgnoreCase))
            {
                cleanedLines.Add("### **3. Decisions Made & Action Items**");
                contentStarted = true;
                continue;
            }

            contentStarted = true;
            cleanedLines.Add(line);
        }

        var result = string.Join("\n", cleanedLines).Trim();

        // Remove trailing empty horizontal lines / dividers
        result = Regex.Replace(result, @"\n\s*(?:---+|\*\*\*+|___+)\s*$", "", RegexOptions.IgnoreCase).Trim();

        return result;
    }

    private static string GenerateFallbackSummary(string transcriptText)
    {
        if (string.IsNullOrWhiteSpace(transcriptText))
            return string.Empty;

        var rawLines = transcriptText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (rawLines.Count == 0)
            return transcriptText;

        var cleanSentences = rawLines.Select(l => l.Contains(':') ? l.Split(':', 2)[1].Trim() : l)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("### **1. Executive Summary / Overview**");
        var overview = string.Join(" ", cleanSentences.Take(3));
        sb.AppendLine(overview.Length > 300 ? overview.Substring(0, 297) + "..." : overview);
        sb.AppendLine();
        sb.AppendLine("### **2. Key Discussion Points**");
        foreach (var item in cleanSentences.Take(5))
        {
            sb.AppendLine($"*   **Point:** {item}");
        }
        sb.AppendLine();
        sb.AppendLine("### **3. Decisions Made & Action Items**");
        sb.AppendLine("*   **Next Steps:** Follow up on key items and milestones from the discussion.");

        return sb.ToString().Trim();
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
            ".mp4" => "audio/mp4",
            ".webm" => "audio/webm",
            ".weba" => "audio/webm",
            ".aac" => "audio/aac",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            ".3gp" or ".3gpp" => "audio/3gpp",
            ".amr" => "audio/amr",
            ".opus" => "audio/opus",
            ".wma" => "audio/x-ms-wma",
            _ => "application/octet-stream"
        };
    }

    private string NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
            return string.IsNullOrWhiteSpace(_options.LanguageCode) ? "en-IN" : _options.LanguageCode;

        var code = languageCode.Trim().ToLowerInvariant();
        if (code.StartsWith("en")) return "en-IN";
        if (code.StartsWith("hi")) return "hi-IN";
        if (code.StartsWith("bn")) return "bn-IN";
        if (code.StartsWith("kn")) return "kn-IN";
        if (code.StartsWith("ml")) return "ml-IN";
        if (code.StartsWith("mr")) return "mr-IN";
        if (code.StartsWith("od") || code.StartsWith("or")) return "od-IN";
        if (code.StartsWith("pa")) return "pa-IN";
        if (code.StartsWith("ta")) return "ta-IN";
        if (code.StartsWith("te")) return "te-IN";
        if (code.StartsWith("gu")) return "gu-IN";
        if (code == "unknown") return "unknown";

        return string.IsNullOrWhiteSpace(_options.LanguageCode) ? "en-IN" : _options.LanguageCode;
    }
}
