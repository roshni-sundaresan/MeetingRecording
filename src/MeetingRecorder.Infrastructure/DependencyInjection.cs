using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Infrastructure.Persistence;
using MeetingRecorder.Infrastructure.Persistence.Repositories;
using MeetingRecorder.Infrastructure.Security;
using MeetingRecorder.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingRecorder.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration.GetValue<string>("Database:Provider") ?? "SqlServer";
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

        services.AddDbContext<AppDbContext>(options =>
        {
            if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase)
                || connectionString.Contains(".db", StringComparison.OrdinalIgnoreCase)
                || connectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase))
            {
                options.UseSqlite(connectionString);
            }
            else
            {
                options.UseSqlServer(connectionString, sql =>
                {
                    sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
                    sql.MigrationsAssembly(typeof(DependencyInjection).Assembly.FullName);
                });
            }
        });

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.Configure<ExternalServices.SarvamOptions>(configuration.GetSection(ExternalServices.SarvamOptions.SectionName));
        services.Configure<ExternalServices.Meetings.MeetingIntegrationOptions>(
            configuration.GetSection(ExternalServices.Meetings.MeetingIntegrationOptions.SectionName));

        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ITokenService, JwtTokenService>();
        services.AddScoped<IPasswordHasher, BcryptPasswordHasher>();
        services.AddScoped<IChunkStorageService, LocalChunkStorageService>();
        services.AddSingleton<IFirebaseAuthService, FirebaseAuthService>();

        services.AddHttpClient<ISarvamApiService, ExternalServices.SarvamApiService>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(4);
        });

        services.AddHttpClient<IMeetingProviderClient, ExternalServices.Meetings.GoogleMeetClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient<IMeetingProviderClient, ExternalServices.Meetings.MicrosoftTeamsClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }
}
