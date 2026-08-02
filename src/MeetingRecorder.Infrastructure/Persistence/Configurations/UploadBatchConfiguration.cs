using MeetingRecorder.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingRecorder.Infrastructure.Persistence.Configurations;

public class UploadBatchConfiguration : IEntityTypeConfiguration<UploadBatch>
{
    public void Configure(EntityTypeBuilder<UploadBatch> builder)
    {
        builder.ToTable("UploadBatches");
        builder.HasKey(b => b.Id);

        builder.Property(b => b.FileName).IsRequired().HasMaxLength(255);
        builder.Property(b => b.Type).IsRequired().HasConversion<int>();
        builder.Property(b => b.SourceLanguageCode).HasMaxLength(16);
        builder.Property(b => b.Status).IsRequired().HasConversion<int>();
        builder.Property(b => b.Summary).HasMaxLength(4000);
        builder.Property(b => b.Transcript).HasMaxLength(20000);
        builder.Property(b => b.Actions).HasMaxLength(4000);
        builder.Property(b => b.Notes).HasMaxLength(4000);

        builder.HasIndex(b => b.UserId);
        builder.HasQueryFilter(b => !b.IsDeleted);
        builder.HasMany(b => b.Chunks).WithOne(c => c.UploadBatch).HasForeignKey(c => c.UploadBatchId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class UploadChunkConfiguration : IEntityTypeConfiguration<UploadChunk>
{
    public void Configure(EntityTypeBuilder<UploadChunk> builder)
    {
        builder.ToTable("UploadChunks");
        builder.HasKey(c => c.Id);

        builder.HasIndex(c => new { c.UploadBatchId, c.ChunkNumber }).IsUnique();
        builder.HasQueryFilter(c => !c.IsDeleted);   // matches UploadBatch/User filters (see EF warning guidance)
    }
}
