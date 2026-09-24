using GameFeedback.Data;
using Microsoft.EntityFrameworkCore;

namespace GameFeedback.Services;

/// <summary>
/// 按 URL 路径段里的 Steam AppID 解析 Game。每请求读库，不做内存缓存——
/// 凭据与启用状态改了就要立刻生效，而多实例部署下任何进程内缓存都会读到过期配置。
/// </summary>
public class GameResolver(AppDbContext db, ApiKeyProtector protector)
{
    public async Task<ResolvedGame?> ResolveAsync(string? appId, CancellationToken cancellationToken)
    {
        var normalized = GameValidation.NormalizeAppId(appId);
        if (normalized.Length == 0 || !normalized.All(char.IsAsciiDigit))
        {
            return null;
        }

        var game = await db.Games
            .AsNoTracking()
            .Include(g => g.Credential)
            .SingleOrDefaultAsync(g => g.SteamAppId == normalized, cancellationToken);
        if (game is null)
        {
            return null;
        }

        string? apiKey = null;
        var unreadable = false;
        if (game.Credential is not null)
        {
            apiKey = protector.TryUnprotect(game.Credential.EncryptedApiKey, game.Credential.Id);
            unreadable = apiKey is null;
        }

        return new ResolvedGame
        {
            Id = game.Id,
            AppId = game.SteamAppId,
            Name = game.Name,
            IsActive = game.IsActive,
            Identity = game.Identity,
            ApiKey = apiKey,
            CredentialUnreadable = unreadable,
        };
    }
}
