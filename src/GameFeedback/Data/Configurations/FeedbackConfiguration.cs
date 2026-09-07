using GameFeedback.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameFeedback.Data.Configurations;

public class FeedbackConfiguration : IEntityTypeConfiguration<Feedback>
{
    public void Configure(EntityTypeBuilder<Feedback> builder)
    {
        builder.ToTable("feedbacks");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.PlayerId).IsRequired();
        builder.HasOne(f => f.Player)
            .WithMany()
            .HasForeignKey(f => f.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(f => f.Type)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(f => f.Title).HasMaxLength(200).IsRequired();
        builder.Property(f => f.Content).HasMaxLength(10_000).IsRequired();

        builder.Property(f => f.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(f => f.GameVersion).HasMaxLength(64);
        builder.Property(f => f.BuildNumber).HasMaxLength(64);
        builder.Property(f => f.OperatingSystem).HasMaxLength(100);
        builder.Property(f => f.Gpu).HasMaxLength(100);
        builder.Property(f => f.Locale).HasMaxLength(32);
        builder.Property(f => f.Map).HasMaxLength(100);
        builder.Property(f => f.Character).HasMaxLength(100);

        builder.HasIndex(f => f.PlayerId);
        builder.HasIndex(f => f.Status);
        builder.HasIndex(f => f.CreatedAt);
        builder.HasIndex(f => f.GameVersion);
    }
}
