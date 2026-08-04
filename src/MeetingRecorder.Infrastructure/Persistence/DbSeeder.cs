using MeetingRecorder.Domain;
using MeetingRecorder.Domain.Constants;
using MeetingRecorder.Domain.Entities;
using MeetingRecorder.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeetingRecorder.Infrastructure.Persistence;

/// <summary>
/// Seeds the admin account + demo user and a handful of sample recordings so the API
/// is immediately usable. Idempotent — safe to run on every startup.
/// </summary>
public static class DbSeeder
{
    public static async Task SeedAsync(AppDbContext context, ILogger logger, CancellationToken ct = default)
    {
        if (await context.Users.AnyAsync(ct))
            return;

        var admin = new User
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Email = "admin@meetingrecorder.dev",
            Name = "Admin User",
            Mobile = "+91 90000 00001",
            ProfilePhotoUrl = null,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin@123"),
            Role = Roles.Admin
        };

        var demo = new User
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Email = "demo@meetingrecorder.dev",
            Name = "Demo User",
            Mobile = "+91 90000 00002",
            ProfilePhotoUrl = null,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Demo@123"),
            Role = Roles.User
        };

        context.Users.AddRange(admin, demo);

        context.Recordings.AddRange(
            new Recording
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                UserId = demo.Id,
                Title = "Weekly Sprint Planning",
                Type = RecordingType.Meeting,
                CreatedAt = DateTime.UtcNow.AddDays(-5),
                Duration = TimeSpan.FromMinutes(42),
                Summary = "Reviewed sprint goals, assigned stories, discussed blockers.",
                Transcript = "Team reviewed the board and planned the next sprint.",
                Actions = "Roshni to follow up on the design review.",
                Notes = "Next standup Friday 9 AM.",
                IsRecording = false,
                Bookmarked = true,
                FilePath = null,
                SourceLanguageCode = "en",
                TranscriptionStatus = TranscriptionStatus.Completed
            },
            new Recording
            {
                Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                UserId = demo.Id,
                Title = "Client Demo Walkthrough",
                Type = RecordingType.Screen,
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                Duration = TimeSpan.FromMinutes(18),
                Summary = "Walked the client through the new dashboard.",
                Transcript = "Demonstrated live filtering and export.",
                Actions = "Send pricing sheet.",
                Notes = null,
                IsRecording = false,
                Bookmarked = false,
                FilePath = null,
                SourceLanguageCode = "en",
                TranscriptionStatus = TranscriptionStatus.Completed
            },
            new Recording
            {
                Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                UserId = demo.Id,
                Title = "Product Brainstorm",
                Type = RecordingType.Audio,
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                Duration = TimeSpan.FromMinutes(55),
                Summary = "Ideas for the mobile onboarding flow.",
                Transcript = null,
                Actions = null,
                Notes = "Book follow-up with design team.",
                IsRecording = false,
                Bookmarked = false,
                FilePath = null,
                SourceLanguageCode = "en",
                TranscriptionStatus = TranscriptionStatus.None
            });

        await context.SaveChangesAsync(ct);
        logger.LogInformation("Seeded admin user, demo user and sample recordings.");
    }
}
