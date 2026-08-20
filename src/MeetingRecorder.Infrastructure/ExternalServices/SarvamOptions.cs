namespace MeetingRecorder.Infrastructure.ExternalServices;

public class SarvamOptions
{
    public const string SectionName = "Sarvam";

    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.sarvam.ai";
    public string SttModel { get; set; } = "saaras:v3";
    public string TtsModel { get; set; } = "bulbul:v3";
    public string TtsSpeaker { get; set; } = "pooja";
    public string SummaryModel { get; set; } = "sarvam-105b";
    public string LanguageCode { get; set; } = "en-IN";
}
