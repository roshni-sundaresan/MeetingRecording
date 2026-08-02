namespace MeetingRecorder.Application.Interfaces;

/// <summary>Resolves the authenticated caller's id (from the JWT "sub" claim).</summary>
public interface ICurrentUserService
{
    Guid? UserId { get; }
    string? Email { get; }
    string? Name { get; }
    bool IsAuthenticated { get; }
    bool IsAdmin { get; }
}
