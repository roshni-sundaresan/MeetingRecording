using System.Collections.Concurrent;
using System.Security.Cryptography;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Domain.Constants;
using MeetingRecorder.Domain.Entities;

namespace MeetingRecorder.Application.Services;

public interface IAuthService
{
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken ct = default);
    Task LogoutAsync(RefreshTokenRequest request, CancellationToken ct = default);
    Task<ForgotPasswordResponse> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct = default);
    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default);
}

internal sealed record ResetEntry(string Token, DateTime ExpiresAt);

public class AuthService : IAuthService
{
    private readonly IUnitOfWork _uow;
    private readonly ITokenService _tokenService;
    private readonly IPasswordHasher _passwordHasher;

    /// In-memory one-time reset tokens (email → token + expiry).
    /// Dev stand-in for an emailed link; swap for a DB/email-backed store in
    /// production. Tokens live 15 minutes and are single-use.
    private static readonly ConcurrentDictionary<string, ResetEntry> ResetTokens = new();
    private static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromMinutes(15);
    private const int ResetTokenLength = 32;

    /// Refresh tokens are single-use and rotate on every refresh; lifetime is
    /// 7 days (mirrors Jwt:RefreshExpiryDays in appsettings).
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);

    public AuthService(IUnitOfWork uow, ITokenService tokenService, IPasswordHasher passwordHasher)
    {
        _uow = uow;
        _tokenService = tokenService;
        _passwordHasher = passwordHasher;
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

    public async Task<ForgotPasswordResponse> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct = default)
    {
        var email = request.Email.ToLowerInvariant().Trim();
        var user = await _uow.Repository<User>()
            .FirstOrDefaultAsync(u => u.Email == email && !u.IsDeleted, ct);

        // Always respond success to avoid account enumeration; unknown emails
        // simply never receive a token.
        if (user is null)
            return new ForgotPasswordResponse("If that email exists, a reset code was issued.", null, (int)ResetTokenLifetime.TotalMinutes);

        var token = RandomNumberGenerator.GetHexString(ResetTokenLength);
        ResetTokens[email] = new ResetEntry(token, DateTime.UtcNow + ResetTokenLifetime);
        return new ForgotPasswordResponse(
            "A password reset code was issued (dev mode: returned inline).",
            token,
            (int)ResetTokenLifetime.TotalMinutes);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        var email = request.Email.ToLowerInvariant().Trim();

        if (!ResetTokens.TryGetValue(email, out var entry) ||
            entry.Token != request.ResetToken ||
            entry.ExpiresAt < DateTime.UtcNow)
        {
            throw new AppException("Invalid or expired reset code.", 400, "INVALID_RESET_CODE");
        }

        var user = await _uow.Repository<User>()
            .FirstOrDefaultAsync(u => u.Email == email && !u.IsDeleted, ct);
        if (user is null)
            throw new AppException("Invalid or expired reset code.", 400, "INVALID_RESET_CODE");

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        await _uow.SaveChangesAsync(ct);
        ResetTokens.TryRemove(email, out _);
    }

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
