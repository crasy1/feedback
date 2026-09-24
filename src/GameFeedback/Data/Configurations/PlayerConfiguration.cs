using GameFeedback.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameFeedback.Data.Configurations;

public class PlayerConfiguration : IEntityTypeConfiguration<Player>
{
    public void Configure(EntityTypeBuilder<Player> builder)
    {
        builder.ToTable("players");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.SteamId)
            .HasMaxLength(20)
            .IsRequired();

        // 一个 Steam 账号在每个 Game 下各有一行 Player，所以唯一性必须是复合的：
        // 全局唯一的 SteamId 会让同一个玩家玩第二个游戏时无立足之地。
        builder.HasIndex(p => new { p.GameId, p.SteamId }).IsUnique();

        builder.Property(p => p.GameId).IsRequired();
        builder.HasOne(p => p.Game)
            .WithMany()
            .HasForeignKey(p => p.GameId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(p => p.SteamName).HasMaxLength(120);
        builder.Property(p => p.AvatarUrl).HasMaxLength(512);

        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();
    }
}
