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
        builder.HasIndex(p => p.SteamId).IsUnique();

        builder.Property(p => p.SteamName).HasMaxLength(120);
        builder.Property(p => p.AvatarUrl).HasMaxLength(512);

        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();
    }
}
