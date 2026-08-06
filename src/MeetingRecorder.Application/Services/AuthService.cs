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

        return BuildAuthResponse(user);
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var email = request.Email.ToLowerInvariant().Trim();
        var exists = await _uow.Repository<User>().AnyAsync(u => u.Email == email && !u.IsDeleted, ct);
        if (exists)
            throw new ConflictException($"A user with email '{email}' already exists.");

        var user = new User
        {
            Email = email,
            Name = request.Name.Trim(),
            Mobile = request.Mobile.Trim(),
            ProfilePhotoUrl = request.ProfilePhotoUrl,
            PasswordHash = _passwordHasher.Hash(request.Password),
            Role = Roles.User
        };

        _uow.Repository<User>().Add(user);
        await _uow.SaveChangesAsync(ct);

        return BuildAuthResponse(user);
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
            throw new AppException("Invalid or expired reset code.", 400);
        }

        var user = await _uow.Repository<User>()
            .FirstOrDefaultAsync(u => u.Email == email && !u.IsDeleted, ct);
        if (user is null)
            throw new AppException("Invalid or expired reset code.", 400);

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        await _uow.SaveChangesAsync(ct);
        ResetTokens.TryRemove(email, out _);
    }

    private AuthResponse BuildAuthResponse(User user)
    {
        var (token, expiresAt) = _tokenService.GenerateToken(user.Id, user.Email, user.Name, user.Role);
        return new AuthResponse(token, expiresAt, new UserResponse(
            user.Id, user.Email, user.Name, user.Mobile, user.ProfilePhotoUrl, user.CreatedDate, user.UpdatedDate, user.Role));
    }
}
