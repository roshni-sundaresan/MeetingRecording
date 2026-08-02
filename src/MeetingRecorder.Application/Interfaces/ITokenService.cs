using System.Security.Claims;

namespace MeetingRecorder.Application.Interfaces;

public interface ITokenService
{
    (string Token, DateTime ExpiresAt) GenerateToken(Guid userId, string email, string name, string role);
    ClaimsPrincipal? ValidateToken(string token);
}
