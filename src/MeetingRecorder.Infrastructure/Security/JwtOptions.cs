namespace MeetingRecorder.Infrastructure.Security;

/// <summary>Binds the "Jwt" section of appsettings.json. Key must be at least 32 bytes in production.</summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "MeetingRecorder";
    public string Audience { get; set; } = "MeetingRecorder.Client";
    public string Key { get; set; } = string.Empty;
    public int ExpiryMinutes { get; set; } = 120;
}
