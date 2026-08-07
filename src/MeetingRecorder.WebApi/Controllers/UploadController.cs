using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Services;
using MeetingRecorder.WebApi.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MeetingRecorder.WebApi.Controllers;

/// <summary>
/// Resumable chunked upload pipeline: start → chunk (×N, retry anytime) → status → complete.
/// Chunk payloads are sent as multipart/form-data (binary file + metadata fields).
/// </summary>
[Authorize]
[EnableRateLimiting("upload")]
public class UploadController : ApiControllerBase
{
    private readonly IBatchUploadService _uploadService;

    public UploadController(IBatchUploadService uploadService)
    {
        _uploadService = uploadService;
    }

    /// <summary>Step 1 — begin a batch and receive its BatchId.</summary>
    [HttpPost("start")]
    public async Task<ActionResult<ApiResponse<StartUploadResponse>>> Start([FromBody] StartUploadRequest request, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin)
            request = request with { UserId = CurrentUser.UserId!.Value };
        await ValidateAsync(request, ct);
        return Envelope(await _uploadService.StartUploadAsync(request, ct), "Upload batch started.", StatusCodes.Status201Created);
    }

    /// <summary>
    /// Step 2 — upload one chunk. Fields: batchId, chunkNumber, totalChunks, userId,
    /// uploadedAt, summary, transcript, actions, notes + the binary file. Idempotent:
    /// re-sending the same chunkNumber overwrites it (this is also the retry mechanism).
    /// </summary>
    [HttpPost("chunk")]
    [RequestSizeLimit(60L * 1024 * 1024)]   // 60 MB per chunk
    public async Task<ActionResult<ApiResponse<UploadStatusResponse>>> UploadChunk(
        [FromForm] UploadChunkRequest request, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponseFactory.Fail("A non-empty file chunk is required.", 400));

        if (!CurrentUser.IsAdmin && CurrentUser.UserId != request.UserId)
            return StatusCode(403, ApiResponseFactory.Fail("You do not have permission to upload for this user.", 403));

        await ValidateAsync(request, ct);
        await using var stream = file.OpenReadStream();
        var status = await _uploadService.UploadChunkAsync(request, stream, ct);
        return Envelope(status, $"Chunk {request.ChunkNumber} uploaded.");
    }

    /// <summary>Step 3 — check which chunks have arrived and whether the batch is complete.
    /// Non-admin callers may only query their own batches.</summary>
    [HttpGet("status/{batchId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<UploadStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UploadStatusResponse>>> Status(Guid batchId, CancellationToken ct)
    {
        return Envelope(await _uploadService.GetUploadStatusAsync(batchId, AccessorUserId, ct));
    }

    /// <summary>
    /// Step 4 — reset a failed/missing chunk so the client can re-upload it
    /// through POST /api/upload/chunk. Non-admin callers may only reset their own batches.
    /// </summary>
    [HttpPost("retry")]
    [ProducesResponseType(typeof(ApiResponse<UploadStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<UploadStatusResponse>>> Retry([FromBody] RetryChunkRequest request, CancellationToken ct)
    {
        if (request.BatchId == Guid.Empty || request.ChunkNumber < 1)
            return BadRequest(ApiResponseFactory.Fail("Valid batchId and chunkNumber are required.", 400));

        return Envelope(await _uploadService.RetryChunkAsync(request.BatchId, request.ChunkNumber, AccessorUserId, ct),
            $"Chunk {request.ChunkNumber} reset. Re-upload it via POST /api/upload/chunk.");
    }

    /// <summary>
    /// Step 5 — finish the batch. Validates chunk count, merges chunks in order into the
    /// final file, saves the Recording, deletes the temporary chunks and marks the upload completed.
    /// Non-admin callers may only complete their own batches.
    /// </summary>
    [HttpPost("complete/{batchId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<RecordingResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<RecordingResponse>>> Complete(Guid batchId, CancellationToken ct)
    {
        return Envelope(await _uploadService.CompleteUploadAsync(batchId, AccessorUserId, ct), "Upload completed and recording saved.", StatusCodes.Status201Created);
    }

    /// <summary>Non-admin callers are scoped to their own batches; admins pass null (bypass).</summary>
    private Guid? AccessorUserId => CurrentUser.IsAdmin ? null : CurrentUser.UserId;
}
