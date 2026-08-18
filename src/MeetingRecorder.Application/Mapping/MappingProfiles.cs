using System.Text.Json;
using AutoMapper;
using MeetingRecorder.Domain.Entities;

namespace MeetingRecorder.Application.Mapping;

/// <summary>
/// Converts structured DTO blocks to/from the JSON strings the database stores.
/// </summary>
public static class StructuredContent
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static string? ToJson<T>(IReadOnlyList<T>? items)
        => items is null ? null : (items.Count == 0 ? "[]" : JsonSerializer.Serialize(items, Options));

    public static IReadOnlyList<T> FromJson<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        var trimmed = json.Trim();
        try
        {
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                return JsonSerializer.Deserialize<List<T>>(trimmed, Options) ?? [];
            }

            if (typeof(T) == typeof(DTOs.TranscriptLineDto))
            {
                var line = new DTOs.TranscriptLineDto("Speaker 1", trimmed.Trim('"'), 0, 0, null);
                return (IReadOnlyList<T>)(object)new List<DTOs.TranscriptLineDto> { line };
            }

            return JsonSerializer.Deserialize<List<T>>(trimmed, Options) ?? [];
        }
        catch (JsonException)
        {
            if (typeof(T) == typeof(DTOs.TranscriptLineDto))
            {
                var line = new DTOs.TranscriptLineDto("Speaker 1", trimmed.Trim('"'), 0, 0, null);
                return (IReadOnlyList<T>)(object)new List<DTOs.TranscriptLineDto> { line };
            }
            return [];
        }
    }
}

public class UserProfile : Profile
{
    public UserProfile()
    {
        CreateMap<User, DTOs.UserResponse>();
    }
}

public class RecordingProfile : Profile
{
    public RecordingProfile()
    {
        CreateMap<Recording, DTOs.RecordingResponse>()
            .ForCtorParam("durationSeconds", o => o.MapFrom(s => (int)Math.Round(s.Duration.TotalSeconds)))
            .ForCtorParam("transcript", o => o.MapFrom(s => StructuredContent.FromJson<DTOs.TranscriptLineDto>(s.Transcript)))
            .ForCtorParam("actions", o => o.MapFrom(s => StructuredContent.FromJson<DTOs.ActionItemDto>(s.Actions)))
            .ForCtorParam("notes", o => o.MapFrom(s => StructuredContent.FromJson<DTOs.RecordingNoteDto>(s.Notes)));
    }
}
