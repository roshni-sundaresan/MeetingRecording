namespace MeetingRecorder.Infrastructure.ExternalServices.Meetings;

public class MeetingIntegrationOptions
{
    public const string SectionName = "MeetingIntegrations";

    public GoogleMeetingOptions Google { get; set; } = new();
    public TeamsMeetingOptions MicrosoftTeams { get; set; } = new();
}

public class GoogleMeetingOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string ServiceAccountKeyJson { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";
}

public class TeamsMeetingOptions
{
    public string TenantId { get; set; } = "common";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
}
