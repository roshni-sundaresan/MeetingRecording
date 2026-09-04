using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.DTOs.Common;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.WebApi.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingRecorder.WebApi.Controllers;

/// <summary>
/// Meeting scheduling and calendar conference management (Google Meet and Microsoft Teams).
/// </summary>
[Authorize]
public class MeetingsController : ApiControllerBase
{
    private readonly IMeetingSchedulerService _meetingSchedulerService;

    public MeetingsController(IMeetingSchedulerService meetingSchedulerService)
    {
        _meetingSchedulerService = meetingSchedulerService;
    }

    private Guid GetUserId() => CurrentUser.UserId ?? throw new Application.Exceptions.AppException("Unauthorized.", StatusCodes.Status401Unauthorized);

    /// <summary>
    /// Schedules a meeting on Google Meet or Microsoft Teams.
    /// </summary>
    /// <remarks>
    /// **Required Parameters from Front-End:**
    /// - **`title`** (string): Subject/topic of the meeting (e.g., "Sprint Planning &amp; Demo"). Max 200 chars.
    /// - **`provider`** (string): Target platform. Allowed values: `"google_meet"` or `"teams"`.
    /// - **`start_time`** (string): Meeting start time in local wall-clock format (e.g., "2026-09-04T14:30:00") or ISO 8601.
    /// - **`end_time`** (string): Meeting end time in local wall-clock format (e.g., "2026-09-04T16:30:00") or ISO 8601. Must be after `start_time`.
    /// 
    /// **Optional Parameters from Front-End:**
    /// - **`description`** (string): Meeting agenda or description notes.
    /// - **`attendees`** (string array): Participant email addresses to invite (e.g., `["alice@example.com", "bob@example.com"]`).
    /// - **`time_zone`** (string): Time zone identifier (e.g., "Asia/Kolkata", "UTC", "America/New_York"). Default: "Asia/Kolkata".
    /// - **`provider_access_token`** (string): Delegated OAuth 2.0 access token (from Google Sign-In or Microsoft MSAL) if available in front-end client session.
    /// </remarks>
    /// <param name="request">Meeting schedule parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Scheduled meeting details including join URL, meeting code, and passcode.</returns>
    [HttpPost("schedule")]
    [ProducesResponseType(typeof(ApiResponse<ScheduledMeetingResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<IDictionary<string, string[]>>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<ScheduledMeetingResponse>>> Schedule(
        [FromBody] ScheduleMeetingRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);

        var result = await _meetingSchedulerService.ScheduleMeetingAsync(GetUserId(), request, ct);

        var providerName = request.Provider == Domain.Enums.MeetingProvider.GoogleMeet ? "Google Meet" : "Microsoft Teams";
        return Envelope(result, $"Meeting scheduled successfully on {providerName}.", StatusCodes.Status201Created);
    }

    /// <summary>
    /// Retrieves a paginated list of scheduled meetings for the authenticated user.
    /// </summary>
    /// <param name="page">Page number (default: 1).</param>
    /// <param name="pageSize">Page size (1-100, default: 10).</param>
    /// <param name="search">Filter by title or description keyword.</param>
    /// <param name="sortBy">Sort column (title, start_time, end_time, created_at, provider).</param>
    /// <param name="sortOrder">Sort direction: "asc" or "desc" (default: "desc").</param>
    /// <param name="createdAfter">Filter meetings created on or after this timestamp.</param>
    /// <param name="createdBefore">Filter meetings created on or before this timestamp.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ScheduledMeetingResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<PagedResult<ScheduledMeetingResponse>>>> GetAll(
        CancellationToken ct,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = "created_at",
        [FromQuery] string sortOrder = "desc",
        [FromQuery] DateTime? createdAfter = null,
        [FromQuery] DateTime? createdBefore = null)
    {
        var result = await _meetingSchedulerService.GetScheduledMeetingsAsync(GetUserId(), new QueryParameters
        {
            Page = page,
            PageSize = pageSize,
            Search = search,
            SortBy = sortBy,
            SortOrder = sortOrder,
            CreatedAfter = createdAfter,
            CreatedBefore = createdBefore
        }, ct);

        return Envelope(result);
    }

    /// <summary>
    /// Gets details of a specific scheduled meeting by its ID.
    /// </summary>
    /// <param name="id">Meeting unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<ScheduledMeetingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<ScheduledMeetingResponse>>> GetById(
        Guid id, CancellationToken ct)
    {
        var result = await _meetingSchedulerService.GetScheduledMeetingByIdAsync(GetUserId(), id, ct);
        return Envelope(result);
    }

    /// <summary>
    /// Cancels a scheduled meeting.
    /// </summary>
    /// <param name="id">Meeting unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<bool>>> Cancel(
        Guid id, CancellationToken ct)
    {
        await _meetingSchedulerService.CancelScheduledMeetingAsync(GetUserId(), id, ct);
        return Ok("Meeting has been cancelled successfully.");
    }
}
