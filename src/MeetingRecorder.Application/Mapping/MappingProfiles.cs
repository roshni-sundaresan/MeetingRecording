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
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
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
