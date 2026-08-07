using System.Security.Claims;

namespace MeetingRecorder.Application.Interfaces;

public interface ITokenService
{
    (string Token, DateTime ExpiresAt) GenerateToken(Guid userId, string email, string name, string role);
    ClaimsPrincipal? ValidateToken(string token);

    /// <summary>Cryptographically random, URL-safe refresh token (single-use).</summary>
    string GenerateRefreshToken();

    /// <summary>SHA-256 hex digest — the only form in which refresh tokens are stored.</summary>
    string HashRefreshToken(string token);
}
