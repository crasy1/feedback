using GameFeedback.Data;
using GameFeedback.Domain;
using Microsoft.EntityFrameworkCore;

namespace GameFeedback.Services;

/// <summary>玩家账号的创建与更新（按 SteamID upsert）。</summary>
public class PlayerService(AppDbContext db)
{
    /// <summary>按 SteamID 查找或创建玩家，并刷新登录资料。一个 Steam 账号对应一个 Player。</summary>
    public async Task<Player> UpsertFromSteamLoginAsync(string steamId, string? steamName, string? avatarUrl, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var player = await db.Players.SingleOrDefaultAsync(p => p.SteamId == steamId, cancellationToken);

        if (player is null)
        {
            player = new Player
            {
                SteamId = steamId,
                SteamName = steamName,
                AvatarUrl = avatarUrl,
                CreatedAt = now,
                UpdatedAt = now,
                LastLoginAt = now,
            };
            db.Players.Add(player);
        }
        else
        {
            player.UpdatedAt = now;
            player.LastLoginAt = now;
            // 资料为尽力而为：仅在获取到新值时覆盖，保留旧值。
            if (steamName is not null)
            {
                player.SteamName = steamName;
            }
            if (avatarUrl is not null)
            {
                player.AvatarUrl = avatarUrl;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return player;
    }
}
