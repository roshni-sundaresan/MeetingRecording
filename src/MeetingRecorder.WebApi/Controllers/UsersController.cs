using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.DTOs.Common;
using MeetingRecorder.Application.Services;
using MeetingRecorder.WebApi.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingRecorder.WebApi.Controllers;

[Authorize]
public class UsersController : ApiControllerBase
{
    private readonly IUserService _userService;

    public UsersController(IUserService userService)
    {
        _userService = userService;
    }

    /// <summary>Paged list with search + sorting. Admin only (privacy).</summary>
    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<PagedResult<UserResponse>>>> GetAll(
        CancellationToken ct,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null, [FromQuery] string? sortBy = null, [FromQuery] string sortOrder = "asc")
    {
        var result = await _userService.GetUsersAsync(new QueryParameters
        {
            Page = page, PageSize = pageSize, Search = search, SortBy = sortBy, SortOrder = sortOrder
        }, ct);
        return Envelope(result);
    }

    /// <summary>Get a single user. Self or admin.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<UserResponse>>> Get(Guid id, CancellationToken ct)
    {
        AccessPolicies.EnsureCanActOnUser(CurrentUser, id);
        return Envelope(await _userService.GetUserAsync(id, ct));
    }

    /// <summary>Create a user (admin only; users self-register via /api/auth/register).</summary>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<UserResponse>>> Create([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        return Envelope(await _userService.CreateUserAsync(request, ct), "User created.", StatusCodes.Status201Created);
    }

    /// <summary>Update profile. Self or admin.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResponse<UserResponse>>> Update(Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        AccessPolicies.EnsureCanActOnUser(CurrentUser, id);
        await ValidateAsync(request, ct);
        return Envelope(await _userService.UpdateUserAsync(id, request, ct), "User updated.");
    }

    /// <summary>Soft-delete a user. Self or admin.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(Guid id, CancellationToken ct)
    {
        AccessPolicies.EnsureCanActOnUser(CurrentUser, id);
        await _userService.DeleteUserAsync(id, ct);
        return Ok("User deleted.");
    }
}
