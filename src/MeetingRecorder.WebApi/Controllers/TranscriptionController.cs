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

    public TranscriptionController(ISarvamApiService sarvamApiService, IUnitOfWork uow, IWebHostEnvironment env)
    {
        _sarvamApiService = sarvamApiService;
        _uow = uow;
        _env = env;
    }

    /// <summary>
    /// Explicitly trigger audio transcription via Sarvam AI for a recording or file.
    /// </summary>
    [HttpPost("transcribe")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<TranscriptLineDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TranscriptLineDto>>>> Transcribe(
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
        try
        {
            lines = await _sarvamApiService.TranscribeAudioAsync(fullPath, request.LanguageCode ?? rec?.SourceLanguageCode, ct);
            if (rec is not null)
            {
                rec.Transcript = StructuredContent.ToJson(lines);
                rec.TranscriptionStatus = TranscriptionStatus.Completed;
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

        return Envelope(lines, "Transcription completed successfully.");
    }

    /// <summary>
    /// Fetch transcription results for a recording or file path.
    /// If not yet transcribed, triggers Sarvam AI transcription automatically.
    /// </summary>
    [HttpGet("result")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<TranscriptLineDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TranscriptLineDto>>>> GetResult(
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

            // If existing transcript is saved, return it immediately
            if (!string.IsNullOrWhiteSpace(rec.Transcript))
            {
                var existingLines = StructuredContent.FromJson<TranscriptLineDto>(rec.Transcript);
                if (existingLines.Count > 0)
                {
                    return Envelope(existingLines);
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

        // Transcribe and persist if recording is present
        var lines = await _sarvamApiService.TranscribeAudioAsync(fullPath, rec?.SourceLanguageCode, ct);
        if (rec is not null && lines.Count > 0)
        {
            rec.Transcript = StructuredContent.ToJson(lines);
            rec.TranscriptionStatus = TranscriptionStatus.Completed;
            rec.UpdatedDate = DateTime.UtcNow;
            _uow.Repository<Recording>().Update(rec);
            await _uow.SaveChangesAsync(ct);
        }

        return Envelope(lines);
    }

    private string ResolveFilePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        return Path.GetFullPath(path, _env.ContentRootPath);
    }
}
