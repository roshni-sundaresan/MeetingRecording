using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.DTOs.Common;
using MeetingRecorder.Application.Services;
using MeetingRecorder.WebApi.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingRecorder.WebApi.Controllers;

[Authorize]
public class RecordingsController : ApiControllerBase
{
    private readonly IRecordingService _recordingService;

    public RecordingsController(IRecordingService recordingService)
    {
        _recordingService = recordingService;
    }

    /// <summary>
    /// Paged list with search + sorting. Regular users see only their own
    /// recordings; admins may pass any userId or none for all.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResult<RecordingResponse>>>> GetAll(
        CancellationToken ct,
        [FromQuery] Guid? userId, [FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null, [FromQuery] string? sortBy = null, [FromQuery] string sortOrder = "asc")
    {
        var effectiveUserId = CurrentUser.IsAdmin ? userId : CurrentUser.UserId;
        var result = await _recordingService.GetRecordingsAsync(effectiveUserId, new QueryParameters
        {
            Page = page, PageSize = pageSize, Search = search, SortBy = sortBy, SortOrder = sortOrder
        }, ct);
        return Envelope(result);
    }

    /// <summary>Get a single recording. Owner or admin.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<RecordingResponse>>> Get(Guid id, CancellationToken ct)
    {
        var rec = await _recordingService.GetRecordingAsync(id, ct);
        AccessPolicies.EnsureCanActOnUser(CurrentUser, rec.UserId);
        return Envelope(rec);
    }

    /// <summary>Create a recording. Regular users can only create for themselves.</summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<RecordingResponse>>> Create([FromBody] CreateRecordingRequest request, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin)
            request = request with { UserId = CurrentUser.UserId!.Value };
        await ValidateAsync(request, ct);
        return Envelope(await _recordingService.CreateRecordingAsync(request, ct), "Recording created.", StatusCodes.Status201Created);
    }

    /// <summary>Update a recording. Owner or admin.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResponse<RecordingResponse>>> Update(Guid id, [FromBody] UpdateRecordingRequest request, CancellationToken ct)
    {
        var existing = await _recordingService.GetRecordingAsync(id, ct);
        AccessPolicies.EnsureCanActOnUser(CurrentUser, existing.UserId);
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
}
