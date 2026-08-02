using MeetingRecorder.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeetingRecorder.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Email).IsRequired().HasMaxLength(320);
        builder.Property(u => u.Name).IsRequired().HasMaxLength(100);
        builder.Property(u => u.Mobile).IsRequired().HasMaxLength(20);
        builder.Property(u => u.ProfilePhotoUrl).HasMaxLength(1000);
        builder.Property(u => u.PasswordHash).IsRequired().HasMaxLength(256);
        builder.Property(u => u.Role).IsRequired().HasMaxLength(20);

        builder.HasIndex(u => u.Email).IsUnique()
            .HasFilter("[IsDeleted] = 0");   // unique among active users (SQL Server)

        builder.HasQueryFilter(u => !u.IsDeleted);
        builder.HasMany(u => u.Recordings).WithOne(r => r.User).HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
