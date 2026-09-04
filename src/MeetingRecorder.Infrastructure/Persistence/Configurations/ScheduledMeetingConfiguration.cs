using MeetingRecorder.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingRecorder.Infrastructure.Persistence.Configurations;

public class ScheduledMeetingConfiguration : IEntityTypeConfiguration<ScheduledMeeting>
{
    public void Configure(EntityTypeBuilder<ScheduledMeeting> builder)
    {
        builder.ToTable("ScheduledMeetings");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Title).IsRequired().HasMaxLength(200);
        builder.Property(m => m.Description).HasMaxLength(4000);
        builder.Property(m => m.Provider).IsRequired().HasConversion<int>();
        builder.Property(m => m.StartTime).IsRequired();
        builder.Property(m => m.EndTime).IsRequired();
        builder.Property(m => m.TimeZone).IsRequired().HasMaxLength(100);
        builder.Property(m => m.JoinUrl).IsRequired().HasMaxLength(2000);
        builder.Property(m => m.MeetingCode).HasMaxLength(200);
        builder.Property(m => m.Passcode).HasMaxLength(100);
        builder.Property(m => m.ExternalMeetingId).HasMaxLength(500);
        builder.Property(m => m.AttendeesJson).HasMaxLength(8000);
        builder.Property(m => m.Status).IsRequired().HasConversion<int>();
        builder.Property(m => m.CreatedAt).IsRequired();

        builder.HasIndex(m => m.UserId);
        builder.HasIndex(m => m.StartTime);
        builder.HasQueryFilter(m => !m.IsDeleted);

        builder.HasOne(m => m.User)
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
