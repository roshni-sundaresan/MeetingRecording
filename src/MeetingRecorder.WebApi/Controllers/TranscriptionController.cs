using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Application.Mapping;
using MeetingRecorder.Domain;
using MeetingRecorder.Domain.Entities;
using MeetingRecorder.WebApi.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingRecorder.WebApi.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class TranscriptionController : ApiControllerBase
{
    private readonly ISarvamApiService _sarvamApiService;
    private readonly IUnitOfWork _uow;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<TranscriptionController> _logger;

    public TranscriptionController(
        ISarvamApiService sarvamApiService,
        IUnitOfWork uow,
        IWebHostEnvironment env,
        ILogger<TranscriptionController> logger)
    {
        _sarvamApiService = sarvamApiService;
        _uow = uow;
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// Direct audio file upload: saves the recording, calls Sarvam STT to get the transcript,
    /// saves transcript to DB, calls Sarvam AI to generate the MOM/Summary, saves MOM/Summary to DB,
    /// and returns the complete result containing both transcription and summary.
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(500L * 1024 * 1024)] // 500 MB
    [ProducesResponseType(typeof(ApiResponse<TranscriptionResultResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<TranscriptionResultResponse>>> UploadAndTranscribe(
        IFormFile file,
        [FromForm] string? title = null,
        [FromForm] string? languageCode = null,
        [FromForm] RecordingType? type = null,
        [FromForm] Guid? recordingId = null,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            throw new AppException("A non-empty audio file is required.", 400, "VALIDATION_ERROR");
        }

        var userId = CurrentUser.UserId ?? Guid.Empty;
        var batchDirId = Guid.NewGuid().ToString("N");
        var recordingDir = Path.Combine(_env.ContentRootPath, "uploads", "recordings", batchDirId);
        Directory.CreateDirectory(recordingDir);

        var originalFileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(originalFileName))
            originalFileName = $"recording_{batchDirId}.mp4";

        var targetFilePath = Path.Combine(recordingDir, originalFileName);
        await using (var stream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await file.CopyToAsync(stream, ct);
        }

        // Relative path for database portability
        var relativePath = Path.Combine("uploads", "recordings", batchDirId, originalFileName);

        Recording? rec = null;
        if (recordingId.HasValue && recordingId.Value != Guid.Empty)
        {
            rec = await _uow.Repository<Recording>().FirstOrDefaultAsync(r => r.Id == recordingId.Value && !r.IsDeleted, ct);
            if (rec is not null)
            {
                AccessPolicies.EnsureCanActOnUser(CurrentUser, rec.UserId);
                rec.FilePath = relativePath;
                rec.TranscriptionStatus = TranscriptionStatus.Processing;
                rec.UpdatedDate = DateTime.UtcNow;
                _uow.Repository<Recording>().Update(rec);
                await _uow.SaveChangesAsync(ct);
            }
        }

        IReadOnlyList<TranscriptLineDto> lines = Array.Empty<TranscriptLineDto>();
        string? summary = null;

        try
        {
            // 1. Call Sarvam STT to get transcript
            lines = await _sarvamApiService.TranscribeAudioAsync(targetFilePath, languageCode ?? rec?.SourceLanguageCode, ct);

            // 2. Call Sarvam AI to generate MOM / Summary from transcript
            if (lines.Count > 0)
            {
                try
                {
                    summary = await _sarvamApiService.SummarizeTranscriptAsync(lines, languageCode ?? rec?.SourceLanguageCode, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate AI summary for uploaded file. Using transcript fallback.");
                }
            }

            // 3. Save or update recording in DB
            if (rec is null)
            {
                rec = new Recording
                {
                    UserId = userId,
                    Title = !string.IsNullOrWhiteSpace(title) ? title.Trim() : Path.GetFileNameWithoutExtension(originalFileName),
                    Type = type ?? RecordingType.Meeting,
                    CreatedAt = DateTime.UtcNow,
                    Duration = TimeSpan.Zero,
                    Summary = summary,
                    Transcript = StructuredContent.ToJson(lines),
                    FilePath = relativePath,
                    SourceLanguageCode = languageCode,
                    TranscriptionStatus = lines.Count > 0 ? TranscriptionStatus.Completed : TranscriptionStatus.None,
                    IsRecording = false,
                    Bookmarked = false
                };
                _uow.Repository<Recording>().Add(rec);
            }
            else
            {
                rec.Transcript = StructuredContent.ToJson(lines);
                rec.Summary = summary;
                rec.TranscriptionStatus = lines.Count > 0 ? TranscriptionStatus.Completed : TranscriptionStatus.None;
                rec.UpdatedDate = DateTime.UtcNow;
                _uow.Repository<Recording>().Update(rec);
            }

            await _uow.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing transcription upload for file {FileName}", originalFileName);
            if (rec is not null)
            {
                rec.TranscriptionStatus = lines.Count > 0 ? TranscriptionStatus.Completed : TranscriptionStatus.Failed;
                rec.UpdatedDate = DateTime.UtcNow;
                _uow.Repository<Recording>().Update(rec);
                await _uow.SaveChangesAsync(ct);
            }
            throw;
        }

        var fullTranscript = string.Join("\n", lines.Select(l => $"{l.Speaker}: {l.Text}")).Trim();
        var response = new TranscriptionResultResponse(
            rec?.Id,
            rec?.Title ?? (!string.IsNullOrWhiteSpace(title) ? title : Path.GetFileNameWithoutExtension(originalFileName)),
            relativePath,
            summary,
            lines,
            fullTranscript,
            rec?.SourceLanguageCode ?? languageCode,
            rec?.TranscriptionStatus ?? TranscriptionStatus.Completed);

        return Envelope(response, "Audio uploaded, transcribed, and summarized successfully.");
    }

    /// <summary>
    /// Explicitly trigger audio transcription via Sarvam AI for a recording or file.
    /// Stores the transcript in DB, calls Sarvam AI to generate the MOM/Summary, stores the MOM/Summary in DB,
    /// and returns both transcription and summary in the response.
    /// </summary>
    [HttpPost("transcribe")]
    [ProducesResponseType(typeof(ApiResponse<TranscriptionResultResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<TranscriptionResultResponse>>> Transcribe(
        [FromBody] TranscribeAudioRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);

        Recording? rec = null;
        string? targetFilePath = request.FilePath;

        if (request.RecordingId.HasValue && request.RecordingId.Value != Guid.Empty)
        {
            rec = await _uow.Repository<Recording>().FirstOrDefaultAsync(r => r.Id == request.RecordingId.Value && !r.IsDeleted, ct)
                ?? throw new NotFoundException(nameof(Recording), request.RecordingId.Value);

            AccessPolicies.EnsureCanActOnUser(CurrentUser, rec.UserId);
            targetFilePath ??= rec.FilePath;
        }

        if (string.IsNullOrWhiteSpace(targetFilePath))
        {
            throw new AppException("Either a valid recording_id or file_path is required.", 400, "VALIDATION_ERROR");
        }

        var fullPath = ResolveFilePath(targetFilePath);
        if (!System.IO.File.Exists(fullPath))
        {
            throw new NotFoundException("Audio file for transcription", targetFilePath);
        }

        // Search for matching recording if not already loaded
        if (rec is null)
        {
            rec = await _uow.Repository<Recording>().FirstOrDefaultAsync(r => r.FilePath == targetFilePath && !r.IsDeleted, ct);
            if (rec is not null)
            {
                AccessPolicies.EnsureCanActOnUser(CurrentUser, rec.UserId);
            }
        }

        if (rec is not null)
        {
            rec.TranscriptionStatus = TranscriptionStatus.Processing;
            rec.UpdatedDate = DateTime.UtcNow;
            _uow.Repository<Recording>().Update(rec);
            await _uow.SaveChangesAsync(ct);
        }

        IReadOnlyList<TranscriptLineDto> lines;
        string? summary = null;
        try
        {
            // 1. Call Sarvam STT
            lines = await _sarvamApiService.TranscribeAudioAsync(fullPath, request.LanguageCode ?? rec?.SourceLanguageCode, ct);

            // 2. Call Sarvam Summary / MOM
            if (lines.Count > 0)
            {
                summary = await _sarvamApiService.SummarizeTranscriptAsync(lines, request.LanguageCode ?? rec?.SourceLanguageCode, ct);
            }

            // 3. Store transcript and MOM/summary in DB
            if (rec is not null)
            {
                rec.Transcript = StructuredContent.ToJson(lines);
                rec.Summary = summary;
                rec.TranscriptionStatus = lines.Count > 0 ? TranscriptionStatus.Completed : TranscriptionStatus.None;
                rec.UpdatedDate = DateTime.UtcNow;
                _uow.Repository<Recording>().Update(rec);
                await _uow.SaveChangesAsync(ct);
            }
        }
        catch
        {
            if (rec is not null)
            {
                rec.TranscriptionStatus = TranscriptionStatus.Failed;
                rec.UpdatedDate = DateTime.UtcNow;
                _uow.Repository<Recording>().Update(rec);
                await _uow.SaveChangesAsync(ct);
            }
            throw;
        }

        var fullTranscript = string.Join("\n", lines.Select(l => $"{l.Speaker}: {l.Text}")).Trim();
        var response = new TranscriptionResultResponse(
            rec?.Id,
            rec?.Title,
            targetFilePath,
            summary ?? rec?.Summary,
            lines,
            fullTranscript,
            request.LanguageCode ?? rec?.SourceLanguageCode,
            rec?.TranscriptionStatus ?? TranscriptionStatus.Completed);

        return Envelope(response, "Transcription and MOM/summary completed successfully.");
    }

    /// <summary>
    /// Fetch transcription and MOM/summary results for a recording or file path.
    /// If not yet transcribed or summarized, triggers Sarvam AI automatically and stores in DB.
    /// </summary>
    [HttpGet("result")]
    [ProducesResponseType(typeof(ApiResponse<TranscriptionResultResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<TranscriptionResultResponse>>> GetResult(
        [FromQuery(Name = "recording_id")] Guid? recordingId,
        [FromQuery(Name = "file_path")] string? filePath,
        CancellationToken ct)
    {
        Recording? rec = null;
        if (recordingId.HasValue && recordingId.Value != Guid.Empty)
        {
            rec = await _uow.Repository<Recording>().FirstOrDefaultAsync(r => r.Id == recordingId.Value && !r.IsDeleted, ct);
        }

        if (rec is null && !string.IsNullOrWhiteSpace(filePath))
        {
            rec = await _uow.Repository<Recording>().FirstOrDefaultAsync(r => r.FilePath == filePath && !r.IsDeleted, ct);
        }

        if (rec is not null)
        {
            AccessPolicies.EnsureCanActOnUser(CurrentUser, rec.UserId);

            // If existing transcript is saved
            if (!string.IsNullOrWhiteSpace(rec.Transcript))
            {
                var existingLines = StructuredContent.FromJson<TranscriptLineDto>(rec.Transcript);
                if (existingLines.Count > 0)
                {
                    // Generate summary if missing
                    if (string.IsNullOrWhiteSpace(rec.Summary))
                    {
                        rec.Summary = await _sarvamApiService.SummarizeTranscriptAsync(existingLines, rec.SourceLanguageCode, ct);
                        rec.UpdatedDate = DateTime.UtcNow;
                        _uow.Repository<Recording>().Update(rec);
                        await _uow.SaveChangesAsync(ct);
                    }

                    var existingFullTranscript = string.Join("\n", existingLines.Select(l => $"{l.Speaker}: {l.Text}")).Trim();
                    var existingResponse = new TranscriptionResultResponse(
                        rec.Id,
                        rec.Title,
                        rec.FilePath,
                        rec.Summary,
                        existingLines,
                        existingFullTranscript,
                        rec.SourceLanguageCode,
                        rec.TranscriptionStatus);

                    return Envelope(existingResponse);
                }
            }

            filePath ??= rec.FilePath;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new AppException("Recording not found or file_path is missing.", 404, "NOT_FOUND");
        }

        var fullPath = ResolveFilePath(filePath);
        if (!System.IO.File.Exists(fullPath))
        {
            throw new NotFoundException("Audio file", filePath);
        }

        // Transcribe and summarize via Sarvam AI, and persist if recording is present
        var lines = await _sarvamApiService.TranscribeAudioAsync(fullPath, rec?.SourceLanguageCode, ct);
        string? summary = null;

        if (lines.Count > 0)
        {
            summary = await _sarvamApiService.SummarizeTranscriptAsync(lines, rec?.SourceLanguageCode, ct);
        }

        if (rec is not null && lines.Count > 0)
        {
            rec.Transcript = StructuredContent.ToJson(lines);
            rec.Summary = summary;
            rec.TranscriptionStatus = TranscriptionStatus.Completed;
            rec.UpdatedDate = DateTime.UtcNow;
            _uow.Repository<Recording>().Update(rec);
            await _uow.SaveChangesAsync(ct);
        }

        var fullText = string.Join("\n", lines.Select(l => $"{l.Speaker}: {l.Text}")).Trim();
        var resultResponse = new TranscriptionResultResponse(
            rec?.Id,
            rec?.Title,
            filePath,
            summary ?? rec?.Summary,
            lines,
            fullText,
            rec?.SourceLanguageCode,
            rec?.TranscriptionStatus ?? TranscriptionStatus.Completed);

        return Envelope(resultResponse);
    }

    /// <summary>
    /// Summarize raw transcript text directly using Sarvam AI and return clean MOM / Summary.
    /// Optionally updates a recording's summary if recordingId is provided.
    /// </summary>
    [HttpPost("summarize")]
    [ProducesResponseType(typeof(ApiResponse<SummarizeTextResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<SummarizeTextResponse>>> SummarizeText(
        [FromBody] SummarizeTextRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
        {
            throw new AppException("Transcript text is required for summarization.", 400, "VALIDATION_ERROR");
        }

        var summary = await _sarvamApiService.SummarizeTranscriptAsync(request.Text, request.LanguageCode, ct);

        if (request.RecordingId.HasValue && request.RecordingId.Value != Guid.Empty)
        {
            var rec = await _uow.Repository<Recording>().FirstOrDefaultAsync(r => r.Id == request.RecordingId.Value && !r.IsDeleted, ct);
            if (rec is not null)
            {
                AccessPolicies.EnsureCanActOnUser(CurrentUser, rec.UserId);
                rec.Summary = summary;
                rec.UpdatedDate = DateTime.UtcNow;
                _uow.Repository<Recording>().Update(rec);
                await _uow.SaveChangesAsync(ct);
            }
        }

        return Envelope(new SummarizeTextResponse(summary ?? string.Empty, request.RecordingId), "MOM / Summary generated successfully.");
    }

    private string ResolveFilePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        return Path.GetFullPath(path, _env.ContentRootPath);
    }
}

