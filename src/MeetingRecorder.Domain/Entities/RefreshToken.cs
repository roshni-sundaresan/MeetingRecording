namespace MeetingRecorder.Domain.Entities;

/// <summary>
/// A long-lived session credential issued alongside the short-lived JWT.
/// Only the SHA-256 hash is stored; the raw token is returned to the client
/// once and cannot be recovered from the database.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
