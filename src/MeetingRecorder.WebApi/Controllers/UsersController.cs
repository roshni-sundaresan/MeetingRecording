using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.DTOs.Common;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
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

    /// <summary>Update profile of current authenticated user.</summary>
    [HttpPut("profile")]
    [HttpPatch("profile")]
    public async Task<ActionResult<ApiResponse<UserResponse>>> UpdateProfile([FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        var userId = CurrentUser.UserId ?? throw new AppException("User is not authenticated.", 401, "UNAUTHORIZED");
        await ValidateAsync(request, ct);
        return Envelope(await _userService.UpdateUserAsync(userId, request, ct), "Profile updated successfully.");
    }

    /// <summary>Update profile by user ID. Self or admin.</summary>
    [HttpPut("{id:guid}")]
    [HttpPatch("{id:guid}")]
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

    /// <summary>Configure or replace custom Sarvam API key for a user (Option 1). Can be called with Bearer token or by passing email/email_id in payload.</summary>
    [HttpPost("api-key")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<UserApiKeyStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<UserApiKeyStatusResponse>>> SetApiKey([FromBody] SetApiKeyRequest request, CancellationToken ct)
    {
        var userId = CurrentUser.UserId;
        if (!userId.HasValue && string.IsNullOrWhiteSpace(request.Email))
        {
            throw new AppException("User is not authenticated or email is missing.", 401, "UNAUTHORIZED");
        }
        await ValidateAsync(request, ct);
        var status = await _userService.SetApiKeyAsync(userId, request, ct);
        return Envelope(status, "Custom API key configured successfully. Your key will now be used for AI features.");
    }

    /// <summary>Get current API key status for a user. Accepts email/email_id query parameter or Bearer token. Returns the stored API key in the masked_key field.</summary>
    [HttpGet("api-key")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<UserApiKeyStatusResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<UserApiKeyStatusResponse>>> GetApiKeyStatus(
        [FromQuery] string? email,
        [FromQuery(Name = "email_id")] string? emailId,
        [FromQuery(Name = "emailId")] string? emailIdCamel,
        CancellationToken ct)
    {
        var effectiveEmail = !string.IsNullOrWhiteSpace(email)
            ? email
            : (!string.IsNullOrWhiteSpace(emailId) ? emailId : emailIdCamel);

        var userId = CurrentUser.UserId;
        if (!userId.HasValue && string.IsNullOrWhiteSpace(effectiveEmail))
        {
            throw new AppException("User is not authenticated or email parameter is missing.", 401, "UNAUTHORIZED");
        }

        var status = await _userService.GetApiKeyStatusAsync(userId, effectiveEmail, ct);
        return Envelope(status);
    }

    /// <summary>Remove custom API key and revert to system-managed / purchased Sarvam key (Option 2).</summary>
    [HttpDelete("api-key")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<UserApiKeyStatusResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<UserApiKeyStatusResponse>>> ResetApiKey(
        [FromQuery] string? email,
        [FromQuery(Name = "email_id")] string? emailId,
        [FromQuery(Name = "emailId")] string? emailIdCamel,
        CancellationToken ct)
    {
        var effectiveEmail = !string.IsNullOrWhiteSpace(email)
            ? email
            : (!string.IsNullOrWhiteSpace(emailId) ? emailId : emailIdCamel);

        var userId = CurrentUser.UserId;
        if (!userId.HasValue && string.IsNullOrWhiteSpace(effectiveEmail))
        {
            throw new AppException("User is not authenticated or email parameter is missing.", 401, "UNAUTHORIZED");
        }

        var status = await _userService.ResetApiKeyAsync(userId, effectiveEmail, ct);
        return Envelope(status, "API key reset to system default / purchased key.");
    }

    /// <summary>Validate a Sarvam API key without saving it.</summary>
    [HttpPost("api-key/validate")]
    [ProducesResponseType(typeof(ApiResponse<ValidateApiKeyResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ValidateApiKeyResponse>>> ValidateApiKey(
        [FromBody] ValidateApiKeyRequest request,
        [FromServices] ISarvamApiService sarvamService,
        CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        var isValid = await sarvamService.ValidateApiKeyAsync(request.ApiKey, ct);
        var response = new ValidateApiKeyResponse(
            IsValid: isValid,
            Message: isValid ? "API key is valid." : "API key is invalid or unauthorized.");

        return Envelope(response);
    }
}
