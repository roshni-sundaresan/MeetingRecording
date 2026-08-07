using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Services;
using MeetingRecorder.WebApi.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MeetingRecorder.WebApi.Controllers;

/// <summary>
/// Public endpoints for obtaining and renewing sessions.
/// Login/register are rate-limited per IP to blunt brute-force attempts.
/// </summary>
[EnableRateLimiting("auth")]
public class AuthController : ApiControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    /// <summary>Exchange email + password for a JWT + refresh token.</summary>
    [AllowAnonymous]
    [HttpPost("login")]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        var result = await _authService.LoginAsync(request, ct);
        return Envelope(result, "Login successful.");
    }

    /// <summary>Create an account and receive a JWT + refresh token immediately.</summary>
    [AllowAnonymous]
    [HttpPost("register")]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        var result = await _authService.RegisterAsync(request, ct);
        return Envelope(result, "Registration successful.", StatusCodes.Status201Created);
    }

    /// <summary>
    /// Rotate a refresh token into a fresh JWT + new refresh token
    /// (single-use rotation — the presented token is revoked).
    /// </summary>
    [AllowAnonymous]
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> Refresh([FromBody] RefreshTokenRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        var result = await _authService.RefreshAsync(request, ct);
        return Envelope(result, "Session refreshed.");
    }

    /// <summary>Revoke a refresh token (ends the session). Idempotent.</summary>
    [AllowAnonymous]
    [HttpPost("logout")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<bool>>> Logout([FromBody] RefreshTokenRequest request, CancellationToken ct)
    {
        await _authService.LogoutAsync(request, ct);
        return Ok("Logged out.");
    }

    /// <summary>Request a password reset code for an email address.</summary>
    /// <remarks>
    /// Dev mode returns the one-time code inline (<c>reset_token</c>, 15 min
    /// validity) since no SMTP provider is configured. Production should email
    /// the code and return the message only.
    /// </remarks>
    [AllowAnonymous]
    [HttpPost("forgot-password")]
    [ProducesResponseType(typeof(ApiResponse<ForgotPasswordResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ForgotPasswordResponse>>> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        var result = await _authService.ForgotPasswordAsync(request, ct);
        return Envelope(result, "Reset code issued.");
    }

    /// <summary>Complete a password reset with the emailed/returned code.</summary>
    [AllowAnonymous]
    [HttpPost("reset-password")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<object>>> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        await _authService.ResetPasswordAsync(request, ct);
        return Envelope<object>(null!, "Password reset successful.");
    }
}
