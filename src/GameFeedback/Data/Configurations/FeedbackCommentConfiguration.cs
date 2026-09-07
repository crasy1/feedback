using GameFeedback.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameFeedback.Data.Configurations;

public class FeedbackCommentConfiguration : IEntityTypeConfiguration<FeedbackComment>
{
    public void Configure(EntityTypeBuilder<FeedbackComment> builder)
    {
        builder.ToTable("feedback_comments");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.FeedbackId).IsRequired();
        builder.HasOne(c => c.Feedback)
            .WithMany(f => f.Comments)
            .HasForeignKey(c => c.FeedbackId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(c => c.AuthorType)
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(c => c.AdminUserId).HasMaxLength(64);

        builder.Property(c => c.Content).HasMaxLength(5_000).IsRequired();

        builder.HasIndex(c => c.FeedbackId);
    }
}
