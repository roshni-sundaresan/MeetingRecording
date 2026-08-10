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

    /// <summary>
    /// Step 1 — request a password reset OTP for a registered username/email.
    /// Response is deliberately generic (no account enumeration). The OTP is
    /// emailed; it is never included in the response outside development
    /// environments (PasswordReset:DevOtpExposure).
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("reset")]
    [HttpPost("password-reset/request")]
    [ProducesResponseType(typeof(ApiResponse<PasswordResetRequestResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<PasswordResetRequestResponse>>> RequestPasswordReset(
        [FromBody] PasswordResetRequestRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        var result = await _authService.RequestPasswordResetAsync(request, ct);
        return Envelope(result, result.Message);
    }

    /// <summary>
    /// Step 2 — verify the emailed OTP. On success issues a short-lived,
    /// single-use reset authorization (reset_token) — never a login token.
    /// The client cannot determine OTP validity; the server is authoritative.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("reset")]
    [HttpPost("password-reset/verify-otp")]
    [ProducesResponseType(typeof(ApiResponse<VerifyOtpResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<VerifyOtpResponse>>> VerifyOtp(
        [FromBody] VerifyOtpRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        var result = await _authService.VerifyOtpAsync(request, ct);
        return Envelope(result, "OTP verified.");
    }

    /// <summary>
    /// Step 3 — resend the OTP. Enforces a 60s cooldown per account, invalidates
    /// the previous OTP and issues a fresh one. Rate-limited per IP.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("reset")]
    [HttpPost("password-reset/resend-otp")]
    [ProducesResponseType(typeof(ApiResponse<PasswordResetRequestResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<PasswordResetRequestResponse>>> ResendOtp(
        [FromBody] ResendOtpRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        var result = await _authService.ResendOtpAsync(request, ct);
        return Envelope(result, result.Message);
    }

    /// <summary>
    /// Step 4 — complete the reset with the authorization from verify-otp.
    /// Validates the token (purpose/expiry/single-use) and the password policy,
    /// updates the hash, invalidates the authorization + sibling requests and
    /// revokes all of the user's sessions.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("reset")]
    [HttpPost("password-reset/complete")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<object>>> CompletePasswordReset(
        [FromBody] CompleteResetRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        await _authService.CompletePasswordResetAsync(request, ct);
        return Envelope<object>(null!, "Password reset successful. Please sign in.");
    }
}
