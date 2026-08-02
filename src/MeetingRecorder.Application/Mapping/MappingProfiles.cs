using AutoMapper;
using MeetingRecorder.Domain.Entities;

namespace MeetingRecorder.Application.Mapping;

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
        CreateMap<Recording, DTOs.RecordingResponse>();
    }
}
