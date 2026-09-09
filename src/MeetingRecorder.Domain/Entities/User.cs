using MeetingRecorder.Domain.Common;

namespace MeetingRecorder.Domain.Entities;

public class User : BaseEntity
{
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string? ProfilePhotoUrl { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = Constants.Roles.User;
    public string? FirebaseUid { get; set; }
    public string? CustomApiKey { get; set; }
    public string? ProviderName { get; set; }
    public string? OAuthKey { get; set; }
    public string? MicrosoftOAuthKey { get; set; }
    public string? GoogleOAuthKey { get; set; }

    public ICollection<Recording> Recordings { get; set; } = new List<Recording>();
}
