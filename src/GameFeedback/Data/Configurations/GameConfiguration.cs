using GameFeedback.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameFeedback.Data.Configurations;

public class GameConfiguration : IEntityTypeConfiguration<Game>
{
    public void Configure(EntityTypeBuilder<Game> builder)
    {
        builder.ToTable("games");

        builder.HasKey(g => g.Id);

        builder.Property(g => g.Name)
            .HasMaxLength(100)
            .IsRequired();

        // AppID 既是 Steam 侧的标识，也是玩家 API 的路径段，所以必须唯一。
        // 可空：迁移为存量数据建的占位游戏当时还不知道 AppID；那种游戏不可寻址。
        // Postgres 的唯一索引允许多个 NULL，所以「多个游戏都还没填 AppID」不冲突。
        builder.Property(g => g.SteamAppId).HasMaxLength(10);
        builder.HasIndex(g => g.SteamAppId).IsUnique();

        builder.Property(g => g.Identity)
            .HasColumnName("steam_identity")
            .HasMaxLength(64)
            .IsRequired()
            .HasDefaultValue("feedback-api");

        builder.Property(g => g.IsActive)
            .IsRequired()
            .HasDefaultValue(true);

        // 凭据被引用的游戏是必需关系：默认删除行为会连坐删掉游戏（乃至反馈），必须显式 Restrict。
        builder.HasOne(g => g.Credential)
            .WithMany()
            .HasForeignKey(g => g.CredentialId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(g => g.CreatedAt).IsRequired();
        builder.Property(g => g.UpdatedAt).IsRequired();
    }
}
