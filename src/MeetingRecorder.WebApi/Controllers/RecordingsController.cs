using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.DTOs.Common;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Services;
using MeetingRecorder.WebApi.Common;
using MeetingRecorder.WebApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace MeetingRecorder.WebApi.Controllers;

[Authorize]
public class RecordingsController : ApiControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly IRecordingService _recordingService;
    private readonly ISignedUrlService _signedUrls;
    private readonly SignedUrlOptions _urlOptions;
    private readonly IWebHostEnvironment _env;

    public RecordingsController(IRecordingService recordingService, ISignedUrlService signedUrls,
        IOptions<SignedUrlOptions> urlOptions, IWebHostEnvironment env)
    {
        _recordingService = recordingService;
        _signedUrls = signedUrls;
        _urlOptions = urlOptions.Value;
        _env = env;
    }

    /// <summary>
    /// Paged list with search + sorting. Regular users see only their own
    /// recordings; admins may pass any userId or none for all.
    /// sortBy is validated server-side (created_at, title, duration, updated_at).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResult<RecordingResponse>>>> GetAll(
        CancellationToken ct,
        [FromQuery] Guid? userId, [FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null, [FromQuery] string? sortBy = null, [FromQuery] string sortOrder = "asc",
        [FromQuery] DateTime? createdAfter = null, [FromQuery] DateTime? createdBefore = null)
    {
        var effectiveUserId = CurrentUser.IsAdmin ? userId : CurrentUser.UserId;
        var result = await _recordingService.GetRecordingsAsync(effectiveUserId, new QueryParameters
        {
            Page = page, PageSize = pageSize, Search = search, SortBy = sortBy, SortOrder = sortOrder,
            CreatedAfter = createdAfter, CreatedBefore = createdBefore
        }, ct);
        return Envelope(result);
    }

    /// <summary>Batch fetch by ids — GET variant. Order preserved; missing/unauthorized ids skipped.</summary>
    [HttpGet("batch")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<RecordingResponse>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RecordingResponse>>>> GetBatch(
        CancellationToken ct, [FromQuery] List<Guid> ids)
    {
        if (ids.Count == 0)
            return BadRequest(ApiResponseFactory.Fail("At least one recording id is required.", 400));

        var effectiveUserId = CurrentUser.IsAdmin ? null : CurrentUser.UserId;
        return Envelope(await _recordingService.GetRecordingsByIdsAsync(effectiveUserId, ids, ct));
    }

    /// <summary>Batch fetch by ids — POST variant (JSON body, avoids URL length limits).</summary>
    [HttpPost("batch")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<RecordingResponse>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RecordingResponse>>>> PostBatch(
        [FromBody] BatchRecordingsRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        var effectiveUserId = CurrentUser.IsAdmin ? null : CurrentUser.UserId;
        return Envelope(await _recordingService.GetRecordingsByIdsAsync(effectiveUserId, request.Ids, ct));
    }

    /// <summary>
    /// Build a playable playlist from recording ids (order preserved). Each item carries
    /// the metadata plus a short-lived signed <c>streamUrl</c> (HMAC, 10-minute expiry)
    /// so media players that cannot attach an Authorization header can stream.
    /// </summary>
    [HttpPost("play")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<RecordingPlaylistItem>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RecordingPlaylistItem>>>> Play(
        [FromBody] BatchRecordingsRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        var effectiveUserId = CurrentUser.IsAdmin ? null : CurrentUser.UserId;
        var recordings = await _recordingService.GetRecordingsByIdsAsync(effectiveUserId, request.Ids, ct);

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        // Whole-second expiry: the URL carries the unix timestamp, and the
        // signature must be computed over exactly the same instant.
        var expiresUnix = DateTimeOffset.UtcNow.AddMinutes(_urlOptions.ExpiryMinutes).ToUnixTimeSeconds();
        var expiresUtc = DateTimeOffset.FromUnixTimeSeconds(expiresUnix).UtcDateTime;
        IReadOnlyList<RecordingPlaylistItem> playlist = recordings.Select(r =>
        {
            // Signature is user-independent: it proves server issuance, and the
            // URL is only obtainable through an authenticated /play call.
            var sig = _signedUrls.Sign(r.Id, expiresUtc, null);
            return new RecordingPlaylistItem(
                r.Id, r.Title, r.Type, r.Duration, r.DurationSeconds,
                $"{baseUrl}/api/recordings/{r.Id}/stream?expires={expiresUnix}&sig={Uri.EscapeDataString(sig)}",
                ContentTypeFor(r.FilePath),
                r.SourceLanguageCode,
                r.TranscriptionStatus);
        }).ToList();

        return Envelope(playlist);
    }

    /// <summary>
    /// Stream a recording file with HTTP Range support (206 Partial Content) so audio/video
    /// players can seek and scrub. Accepts EITHER a valid short-lived signature
    /// (?expires=&amp;sig=, from /play) OR a JWT of the owner/admin.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{id:guid}/stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status416RangeNotSatisfiable)]
    public async Task<IActionResult> Stream(Guid id, CancellationToken ct)
    {
        var rec = await _recordingService.GetRecordingAsync(id, ct);

        // Path A — signed URL from /play (media players without a JWT).
        if (long.TryParse(Request.Query["expires"], out var expiresUnix))
        {
            var sig = Request.Query["sig"].ToString();
            var expiresUtc = DateTimeOffset.FromUnixTimeSeconds(expiresUnix).UtcDateTime;
            if (!_signedUrls.TryValidate(id, sig, expiresUtc, null))
                return StatusCode(403, ApiResponseFactory.Fail("Invalid or expired stream link.", 403));
        }
        else
        {
            // Path B — authenticated owner/admin.
            if (!CurrentUser.IsAuthenticated)
                return Unauthorized(ApiResponseFactory.Fail("Authentication required.", 401));
            AccessPolicies.EnsureCanActOnUser(CurrentUser, rec.UserId);
        }

        if (string.IsNullOrEmpty(rec.FilePath))
            throw new NotFoundException("Recording file", id);

        var fullPath = Path.GetFullPath(rec.FilePath, _env.ContentRootPath);
        if (!System.IO.File.Exists(fullPath))
            throw new NotFoundException("Recording file", id);

        var contentType = ContentTypeFor(rec.FilePath) ?? "application/octet-stream";
        return PhysicalFile(fullPath, contentType, enableRangeProcessing: true);
    }

    /// <summary>Get a single recording. Owner or admin.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<RecordingResponse>>> Get(Guid id, CancellationToken ct)
    {
        var rec = await _recordingService.GetRecordingAsync(id, ct);
        AccessPolicies.EnsureCanActOnUser(CurrentUser, rec.UserId);
        return Envelope(rec);
    }

    /// <summary>Create a recording. Regular users can only create for themselves,
    /// and file_path is system-managed (ignored from client input).</summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<RecordingResponse>>> Create([FromBody] CreateRecordingRequest request, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin)
            request = request with { UserId = CurrentUser.UserId!.Value, FilePath = null };
        await ValidateAsync(request, ct);
        return Envelope(await _recordingService.CreateRecordingAsync(request, ct), "Recording created.", StatusCodes.Status201Created);
    }

    /// <summary>Update a recording. Owner or admin. file_path is system-managed
    /// and stripped from non-admin input; transcript/transcription_status remain
    /// owner-writable (the client owns the local transcription pipeline).</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResponse<RecordingResponse>>> Update(Guid id, [FromBody] UpdateRecordingRequest request, CancellationToken ct)
    {
        var existing = await _recordingService.GetRecordingAsync(id, ct);
        AccessPolicies.EnsureCanActOnUser(CurrentUser, existing.UserId);
        if (!CurrentUser.IsAdmin)
            request = request with { FilePath = null };
        await ValidateAsync(request, ct);
        return Envelope(await _recordingService.UpdateRecordingAsync(id, request, ct), "Recording updated.");
    }

    /// <summary>Bookmark / un-bookmark. Owner or admin.</summary>
    [HttpPatch("{id:guid}/bookmark")]
    public async Task<ActionResult<ApiResponse<RecordingResponse>>> SetBookmark(Guid id, [FromBody] BookmarkRequest request, CancellationToken ct)
    {
        var existing = await _recordingService.GetRecordingAsync(id, ct);
        AccessPolicies.EnsureCanActOnUser(CurrentUser, existing.UserId);
        return Envelope(await _recordingService.SetBookmarkAsync(id, request.Bookmarked, ct),
            request.Bookmarked ? "Recording bookmarked." : "Bookmark removed.");
    }

    /// <summary>Soft-delete a recording. Owner or admin.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(Guid id, CancellationToken ct)
    {
        var existing = await _recordingService.GetRecordingAsync(id, ct);
        AccessPolicies.EnsureCanActOnUser(CurrentUser, existing.UserId);
        await _recordingService.DeleteRecordingAsync(id, ct);
        return Ok("Recording deleted.");
    }

    private static string? ContentTypeFor(string? path)
        => !string.IsNullOrEmpty(path) && ContentTypes.TryGetContentType(path, out var contentType)
            ? contentType : null;
}
