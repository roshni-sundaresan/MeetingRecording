using MeetingRecorder.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingRecorder.Infrastructure.Persistence.Configurations;

public class RecordingConfiguration : IEntityTypeConfiguration<Recording>
{
    public void Configure(EntityTypeBuilder<Recording> builder)
    {
        builder.ToTable("Recordings");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Title).IsRequired().HasMaxLength(200);
        builder.Property(r => r.Type).IsRequired().HasConversion<int>();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.Duration).HasConversion<TimeSpan>();
        builder.Property(r => r.Summary).HasMaxLength(4000);
        builder.Property(r => r.Transcript).HasMaxLength(20000);
        builder.Property(r => r.Actions).HasMaxLength(4000);
        builder.Property(r => r.Notes).HasMaxLength(4000);
        builder.Property(r => r.FilePath).HasMaxLength(1000);
        builder.Property(r => r.SourceLanguageCode).HasMaxLength(16);
        builder.Property(r => r.TranscriptionStatus).IsRequired().HasConversion<int>();

        builder.HasIndex(r => r.UserId);
        builder.HasIndex(r => r.CreatedAt);
        builder.HasQueryFilter(r => !r.IsDeleted);
    }
}
