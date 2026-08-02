using FluentValidation;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Application.Services;
using MeetingRecorder.Application.Validators;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingRecorder.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddAutoMapper(cfg => cfg.AddMaps(typeof(DependencyInjection).Assembly));

        // FluentValidation validators
        services.AddScoped<IValidator<DTOs.LoginRequest>, LoginValidator>();
        services.AddScoped<IValidator<DTOs.RegisterRequest>, RegisterValidator>();
        services.AddScoped<IValidator<DTOs.CreateUserRequest>, CreateUserValidator>();
        services.AddScoped<IValidator<DTOs.UpdateUserRequest>, UpdateUserValidator>();
        services.AddScoped<IValidator<DTOs.CreateRecordingRequest>, CreateRecordingValidator>();
        services.AddScoped<IValidator<DTOs.UpdateRecordingRequest>, UpdateRecordingValidator>();
        services.AddScoped<IValidator<DTOs.StartUploadRequest>, StartUploadValidator>();
        services.AddScoped<IValidator<DTOs.UploadChunkRequest>, UploadChunkValidator>();
        services.AddScoped<IValidator<Guid>, CompleteUploadValidator>();

        // Services
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IRecordingService, RecordingService>();
        services.AddScoped<IBatchUploadService, BatchUploadService>();

        return services;
    }
}
