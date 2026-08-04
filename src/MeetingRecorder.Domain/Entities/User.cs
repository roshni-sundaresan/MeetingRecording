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

    public ICollection<Recording> Recordings { get; set; } = new List<Recording>();
}
