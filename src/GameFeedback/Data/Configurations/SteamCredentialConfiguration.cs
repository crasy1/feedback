using GameFeedback.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameFeedback.Data.Configurations;

public class SteamCredentialConfiguration : IEntityTypeConfiguration<SteamCredential>
{
    public void Configure(EntityTypeBuilder<SteamCredential> builder)
    {
        builder.ToTable("steam_credentials");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name)
            .HasMaxLength(100)
            .IsRequired();

        // Data Protection 密文长度随 key ring 与明文增长，用 text 不设上限。
        builder.Property(c => c.EncryptedApiKey)
            .IsRequired();

        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();
    }
}
