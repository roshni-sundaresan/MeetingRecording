using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Domain.Constants;
using MeetingRecorder.Domain.Entities;
using Microsoft.Extensions.Options;

namespace MeetingRecorder.Application.Services;

public interface IAuthService
{
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken ct = default);
    Task LogoutAsync(RefreshTokenRequest request, CancellationToken ct = default);

    // ── Password reset (server-authoritative OTP flow) ──
    Task<PasswordResetRequestResponse> RequestPasswordResetAsync(PasswordResetRequestRequest request, CancellationToken ct = default);
    Task<VerifyOtpResponse> VerifyOtpAsync(VerifyOtpRequest request, CancellationToken ct = default);
    Task<PasswordResetRequestResponse> ResendOtpAsync(ResendOtpRequest request, CancellationToken ct = default);
    Task CompletePasswordResetAsync(CompleteResetRequest request, CancellationToken ct = default);
}

public class AuthService : IAuthService
{
    public const string PurposePasswordReset = "PASSWORD_RESET";
    private const string GenericRequestMessage = "If the account exists, an OTP has been sent.";
    private const string GenericInvalidMessage = "Invalid or expired code.";

    private readonly IUnitOfWork _uow;
    private readonly ITokenService _tokenService;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IOtpService _otpService;
    private readonly IEmailService _emailService;
    private readonly PasswordResetOptions _resetOptions;

    /// Refresh tokens are single-use and rotate on every refresh; lifetime is
    /// 7 days (mirrors Jwt:RefreshExpiryDays in appsettings).
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);

    public AuthService(IUnitOfWork uow, ITokenService tokenService, IPasswordHasher passwordHasher,
        IOtpService otpService, IEmailService emailService, IOptions<PasswordResetOptions> resetOptions)
    {
        _uow = uow;
        _tokenService = tokenService;
        _passwordHasher = passwordHasher;
        _otpService = otpService;
        _emailService = emailService;
        _resetOptions = resetOptions.Value;
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var user = await _uow.Repository<User>()
            .FirstOrDefaultAsync(u => u.Email == request.Email.ToLowerInvariant().Trim() && !u.IsDeleted, ct);

        if (user is null || !_passwordHasher.Verify(request.Password, user.PasswordHash))
            throw new AppException("Invalid email or password.", 401);

        return await BuildAuthResponseAsync(user, ct);
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var email = request.Email.ToLowerInvariant().Trim();
        var repo = _uow.Repository<User>();

        if (await repo.AnyAsync(u => u.Email == email && !u.IsDeleted, ct))
            throw new ConflictException($"A user with email '{email}' already exists.", "EMAIL_TAKEN");

        var mobile = request.Mobile.Trim();
        if (await repo.AnyAsync(u => u.Mobile == mobile && !u.IsDeleted, ct))
            throw new ConflictException($"A user with mobile '{mobile}' already exists.", "MOBILE_TAKEN");

        var user = new User
        {
            Email = email,
            Name = request.Name.Trim(),
            Mobile = mobile,
            ProfilePhotoUrl = request.ProfilePhotoUrl,
            PasswordHash = _passwordHasher.Hash(request.Password),
            Role = Roles.User
        };

        _uow.Repository<User>().Add(user);
        await _uow.SaveChangesAsync(ct);

        return await BuildAuthResponseAsync(user, ct);
    }

    /// <summary>
    /// Exchange a still-valid refresh token for a fresh JWT + a rotated
    /// refresh token (single-use rotation: the presented token is revoked).
    /// </summary>
    public async Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken ct = default)
    {
        var stored = await FindActiveRefreshTokenAsync(request.RefreshToken, ct);
        if (stored is null)
            throw new AppException("Invalid or expired refresh token.", 401, "INVALID_REFRESH_TOKEN");

        var user = await _uow.Repository<User>()
            .FirstOrDefaultAsync(u => u.Id == stored.UserId && !u.IsDeleted, ct);
        if (user is null)
            throw new AppException("Invalid or expired refresh token.", 401, "INVALID_REFRESH_TOKEN");

        // Rotate: revoke the presented token, issue a brand-new pair.
        stored.RevokedAt = DateTime.UtcNow;
        _uow.Repository<RefreshToken>().Update(stored);
        await _uow.SaveChangesAsync(ct);

        return await BuildAuthResponseAsync(user, ct);
    }

    /// <summary>Revoke the presented refresh token (end the session).</summary>
    public async Task LogoutAsync(RefreshTokenRequest request, CancellationToken ct = default)
    {
        var stored = await FindActiveRefreshTokenAsync(request.RefreshToken, ct);
        if (stored is null)
            return;   // idempotent — nothing to revoke

        stored.RevokedAt = DateTime.UtcNow;
        _uow.Repository<RefreshToken>().Update(stored);
        await _uow.SaveChangesAsync(ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Password reset — server is the source of truth for OTP generation,
    // storage, expiry, verification, attempt limits, resend cooldown and the
    // reset authorization. The client only relays input and displays state.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Step 1 — validate the username, generate a 6-digit OTP (crypto RNG),
    /// store only its SHA-256 hash (5-minute expiry), email it, and return a
    /// generic message so account existence is never disclosed. Unknown
    /// accounts receive the identical generic response (no OTP issued).
    /// </summary>
    public async Task<PasswordResetRequestResponse> RequestPasswordResetAsync(
        PasswordResetRequestRequest request, CancellationToken ct = default)
    {
        var username = request.Username.ToLowerInvariant().Trim();
        var user = await _uow.Repository<User>()
            .FirstOrDefaultAsync(u => u.Email == username && !u.IsDeleted, ct);

        if (user is null)
            return new PasswordResetRequestResponse(GenericRequestMessage, null, null);

        var (resetRequest, otp) = await CreateResetRequestAsync(user, ct);
        await _emailService.SendPasswordResetOtpAsync(user.Email, otp, _resetOptions.OtpLifetimeMinutes, ct);

        return BuildRequestResponse(GenericRequestMessage, resetRequest, otp);
    }

    /// <summary>
    /// Step 2 — verify the submitted OTP against the stored hash. Enforces
    /// existence, purpose, expiry, single-use and a maximum attempt count.
    /// On success the OTP is consumed and a short-lived, single-use reset
    /// authorization is issued. All failures return the same generic error.
    /// </summary>
    public async Task<VerifyOtpResponse> VerifyOtpAsync(VerifyOtpRequest request, CancellationToken ct = default)
    {
        if (!Guid.TryParse(request.ResetRequestId, out var requestId))
            throw new AppException(GenericInvalidMessage, 400, "INVALID_OTP");

        var reset = await _uow.Repository<PasswordResetRequest>()
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

        if (reset is null || reset.Purpose != PurposePasswordReset || reset.IsUsed || reset.ExpiresAt < DateTime.UtcNow)
            throw new AppException(GenericInvalidMessage, 400, "INVALID_OTP");

        // Attempt limit: once reached the request is locked, so brute-forcing
        // a 6-digit code is bounded to MaxOtpAttempts guesses.
        if (reset.AttemptCount >= _resetOptions.MaxOtpAttempts)
        {
            reset.IsUsed = true;
            _uow.Repository<PasswordResetRequest>().Update(reset);
            await _uow.SaveChangesAsync(ct);
            throw new AppException(GenericInvalidMessage, 400, "INVALID_OTP");
        }

        if (!_otpService.VerifyOtp(request.Otp, reset.OtpHash))
        {
            reset.AttemptCount += 1;
            if (reset.AttemptCount >= _resetOptions.MaxOtpAttempts)
                reset.IsUsed = true;   // lock after the final allowed attempt
            _uow.Repository<PasswordResetRequest>().Update(reset);
            await _uow.SaveChangesAsync(ct);
            throw new AppException(GenericInvalidMessage, 400, "INVALID_OTP");
        }

        // Success: consume the OTP and issue the reset authorization.
        var resetToken = _otpService.GenerateResetToken();
        reset.IsUsed = true;
        reset.ResetTokenHash = _otpService.HashOtp(resetToken);   // same SHA-256 primitive
        reset.ResetTokenExpiresAt = DateTime.UtcNow.AddMinutes(_resetOptions.ResetTokenLifetimeMinutes);
        _uow.Repository<PasswordResetRequest>().Update(reset);
        await _uow.SaveChangesAsync(ct);

        return new VerifyOtpResponse(resetToken, reset.ResetTokenExpiresAt.Value);
    }

    /// <summary>
    /// Step 3 — resend: enforces a 60s cooldown, invalidates the previous OTP
    /// and issues a fresh one with a reset expiry. Response is generic for
    /// unknown accounts (no enumeration).
    /// </summary>
    public async Task<PasswordResetRequestResponse> ResendOtpAsync(ResendOtpRequest request, CancellationToken ct = default)
    {
        var username = request.Username.ToLowerInvariant().Trim();
        var user = await _uow.Repository<User>()
            .FirstOrDefaultAsync(u => u.Email == username && !u.IsDeleted, ct);

        if (user is null)
            return new PasswordResetRequestResponse(GenericRequestMessage, null, null);

        var now = DateTime.UtcNow;
        var latest = await _uow.Repository<PasswordResetRequest>()
            .FirstOrDefaultAsync(r => r.UserId == user.Id && !r.IsUsed && r.Purpose == PurposePasswordReset, ct);

        if (latest is { ResendAt: not null } && latest.ResendAt.Value > now)
            throw new AppException("Please wait before requesting another code.", 429, "RESEND_COOLDOWN");

        // A new OTP invalidates the previous one (spec requirement).
        if (latest is not null)
        {
            latest.IsUsed = true;
            _uow.Repository<PasswordResetRequest>().Update(latest);
        }

        var (resetRequest, otp) = await CreateResetRequestAsync(user, ct);
        await _emailService.SendPasswordResetOtpAsync(user.Email, otp, _resetOptions.OtpLifetimeMinutes, ct);

        return BuildRequestResponse(GenericRequestMessage, resetRequest, otp);
    }

    /// <summary>
    /// Step 4 — complete the reset. Validates the reset authorization
    /// (purpose, expiry, single-use), applies the authoritative password
    /// policy, hashes with the app's BCrypt hasher, updates the password,
    /// consumes the authorization, invalidates sibling reset requests and
    /// revokes all of the user's sessions (refresh tokens).
    /// </summary>
    public async Task CompletePasswordResetAsync(CompleteResetRequest request, CancellationToken ct = default)
    {
        var tokenHash = _otpService.HashOtp(request.ResetToken);
        var reset = await _uow.Repository<PasswordResetRequest>()
            .FirstOrDefaultAsync(r => r.ResetTokenHash == tokenHash, ct);

        if (reset is null || reset.Purpose != PurposePasswordReset
            || reset.ResetTokenExpiresAt is null || reset.ResetTokenExpiresAt < DateTime.UtcNow)
        {
            throw new AppException("Invalid or expired reset token.", 400, "INVALID_RESET_TOKEN");
        }

        var user = await _uow.Repository<User>()
            .FirstOrDefaultAsync(u => u.Id == reset.UserId && !u.IsDeleted, ct);
        if (user is null)
            throw new AppException("Invalid or expired reset token.", 400, "INVALID_RESET_TOKEN");

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        _uow.Repository<User>().Update(user);

        // Consume the authorization (clearing the hash makes reuse impossible).
        reset.ResetTokenHash = null;
        reset.ResetTokenExpiresAt = null;

        // Invalidate any other outstanding reset requests for this user.
        var siblings = _uow.Repository<PasswordResetRequest>().Query()
            .Where(r => r.UserId == user.Id && !r.IsUsed).ToList();
        foreach (var sibling in siblings)
        {
            sibling.IsUsed = true;
            _uow.Repository<PasswordResetRequest>().Update(sibling);
        }

        // Security model: revoke every session on password change.
        var sessions = _uow.Repository<RefreshToken>().Query()
            .Where(t => t.UserId == user.Id && t.RevokedAt == null).ToList();
        foreach (var session in sessions)
        {
            session.RevokedAt = DateTime.UtcNow;
            _uow.Repository<RefreshToken>().Update(session);
        }

        await _uow.SaveChangesAsync(ct);
    }

    // ── private helpers ──────────────────────────────────────────────────────

    private async Task<(PasswordResetRequest Request, string Otp)> CreateResetRequestAsync(User user, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var otp = _otpService.GenerateOtp();

        var reset = new PasswordResetRequest
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            OtpHash = _otpService.HashOtp(otp),
            Purpose = PurposePasswordReset,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(_resetOptions.OtpLifetimeMinutes),
            ResendAt = now.AddSeconds(_resetOptions.ResendCooldownSeconds)
        };

        _uow.Repository<PasswordResetRequest>().Add(reset);
        await _uow.SaveChangesAsync(ct);
        return (reset, otp);
    }

    private PasswordResetRequestResponse BuildRequestResponse(string message, PasswordResetRequest reset, string otp)
        => new(message, reset.Id, reset.ExpiresAt,
            _resetOptions.DevOtpExposure ? otp : null);

    private async Task<RefreshToken?> FindActiveRefreshTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var hash = _tokenService.HashRefreshToken(token);
        var stored = await _uow.Repository<RefreshToken>()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null || stored.RevokedAt is not null || stored.ExpiresAt < DateTime.UtcNow)
            return null;

        return stored;
    }

    private async Task<AuthResponse> BuildAuthResponseAsync(User user, CancellationToken ct)
    {
        var (token, expiresAt) = _tokenService.GenerateToken(user.Id, user.Email, user.Name, user.Role);

        var refreshToken = _tokenService.GenerateRefreshToken();
        var refreshExpiresAt = DateTime.UtcNow + RefreshTokenLifetime;
        _uow.Repository<RefreshToken>().Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = _tokenService.HashRefreshToken(refreshToken),
            ExpiresAt = refreshExpiresAt
        });
        await _uow.SaveChangesAsync(ct);

        return new AuthResponse(token, expiresAt, new UserResponse(
            user.Id, user.Email, user.Name, user.Mobile, user.ProfilePhotoUrl, user.CreatedDate, user.UpdatedDate, user.Role),
            TokenType: "Bearer", RefreshToken: refreshToken, RefreshExpiresAt: refreshExpiresAt);
    }
}
