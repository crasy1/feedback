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

        // Game 是必需关系，而 EF 对必需关系的默认删除行为是 Cascade——不显式改成 Restrict，
        // 「删除一个游戏」就会顺手删掉它名下全部玩家反馈。这条是本方案里最容易变成数据丢失的一处。
        builder.Property(f => f.GameId).IsRequired();
        builder.HasOne(f => f.Game)
            .WithMany()
            .HasForeignKey(f => f.GameId)
            .OnDelete(DeleteBehavior.Restrict);

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
        builder.Property(f => f.Cpu).HasMaxLength(120);
        builder.Property(f => f.MemoryTotalMb);
        builder.Property(f => f.PlaytimeMinutes);
        builder.Property(f => f.Locale).HasMaxLength(32);
        builder.Property(f => f.Map).HasMaxLength(100);
        builder.Property(f => f.Character).HasMaxLength(100);

        // 玩家侧：按玩家取自己的反馈（PlayerId 本身已经隐含游戏）。
        builder.HasIndex(f => f.PlayerId);
        // 管理端：默认视图不筛游戏，按时间倒序；筛了游戏就换成 (GameId, CreatedAt)。
        builder.HasIndex(f => f.CreatedAt);
        builder.HasIndex(f => f.Status);
        builder.HasIndex(f => new { f.GameId, f.CreatedAt });
        builder.HasIndex(f => new { f.GameId, f.Status });
        builder.HasIndex(f => new { f.GameId, f.GameVersion });
    }
}
