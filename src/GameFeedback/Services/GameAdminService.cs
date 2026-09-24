using GameFeedback.Data;
using GameFeedback.Domain;
using Microsoft.EntityFrameworkCore;

namespace GameFeedback.Services;

/// <summary>管理端列表/详情用的 Game 视图。刻意不是 EF 实体，避免把凭据导航属性带进页面。</summary>
public sealed record GameAdminView(
    int Id,
    string Name,
    string? SteamAppId,
    string Identity,
    bool IsActive,
    int? CredentialId,
    string? CredentialName,
    int FeedbackCount,
    int PlayerCount,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    /// <summary>
    /// AppID 与凭据都齐备才算配置完成（凭据能否解密是另一回事，由探测动作回答）。
    /// AppID 为空的行只可能是升级迁移为存量数据建的占位游戏——它不可寻址。
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SteamAppId) && CredentialId is not null;
}

/// <summary>新增/编辑 Game 的输入。全部为字符串，由服务层统一校验。</summary>
public sealed record GameEditRequest(
    string? Name,
    string? SteamAppId,
    string? Identity,
    int? CredentialId,
    bool IsActive);

public sealed record GameSaveResult(GameAdminView? Game, string? Error)
{
    public bool Succeeded => Error is null;
}

public enum GameDeleteOutcome
{
    Deleted,
    NotFound,

    /// <summary>名下已有反馈或玩家——拒绝硬删，管理端应引导改用「停用」。</summary>
    HasData,
}

/// <summary>
/// 管理端对 Game 的增删改查。管理员是全局身份（没有按游戏授权），所以这里不做作用域过滤。
/// </summary>
public class GameAdminService(IDbContextFactory<AppDbContext> dbFactory, ILogger<GameAdminService> logger)
{
    /// <summary>games 表里一条都没有——管理端据此把管理员强制送到「添加游戏」页。</summary>
    public async Task<bool> HasAnyAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Games.AnyAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GameAdminView>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // 排序必须作用在实体查询上：投影成 DTO 之后再排序，EF Core 翻译不了（投影里含相关子查询）。
        return await Project(db, db.Games.AsNoTracking().OrderBy(g => g.Name).ThenBy(g => g.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<GameAdminView?> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Project(db, db.Games.AsNoTracking().Where(g => g.Id == id))
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>删除前必须先知道有没有数据，所以行数计数跟着视图一起取（相关子查询）。</summary>
    private static IQueryable<GameAdminView> Project(AppDbContext db, IQueryable<Game> games) =>
        games.Select(g => new GameAdminView(
            g.Id,
            g.Name,
            g.SteamAppId,
            g.Identity,
            g.IsActive,
            g.CredentialId,
            g.Credential == null ? null : g.Credential.Name,
            db.Feedbacks.Count(f => f.GameId == g.Id),
            db.Players.Count(p => p.GameId == g.Id),
            g.CreatedAt,
            g.UpdatedAt));

    public async Task<GameSaveResult> CreateAsync(GameEditRequest request, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var validation = await ValidateAsync(db, request, excludeGameId: null, cancellationToken);
        if (validation.Error is not null)
        {
            return new GameSaveResult(null, validation.Error);
        }

        var now = DateTime.UtcNow;
        var game = new Game
        {
            Name = validation.Name,
            SteamAppId = validation.SteamAppId,
            Identity = validation.Identity,
            CredentialId = validation.CredentialId,
            IsActive = request.IsActive,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("管理员创建了 Game GameId={GameId} AppId={AppId}", game.Id, game.SteamAppId);
        return new GameSaveResult(await GetAsync(game.Id, cancellationToken), null);
    }

    public async Task<GameSaveResult> UpdateAsync(int id, GameEditRequest request, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var game = await db.Games.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (game is null)
        {
            return new GameSaveResult(null, "游戏不存在");
        }

        var validation = await ValidateAsync(db, request, excludeGameId: id, cancellationToken);
        if (validation.Error is not null)
        {
            return new GameSaveResult(null, validation.Error);
        }

        // 审计：只记谁改了什么，绝不记凭据内容（凭据根本不在这个请求里）。
        logger.LogInformation(
            "管理员修改了 Game GameId={GameId} AppIdBefore={AppIdBefore} AppIdAfter={AppIdAfter} IsActive={IsActive} CredentialId={CredentialId}",
            game.Id,
            game.SteamAppId,
            validation.SteamAppId,
            request.IsActive,
            validation.CredentialId);

        game.Name = validation.Name;
        game.SteamAppId = validation.SteamAppId;
        game.Identity = validation.Identity;
        game.CredentialId = validation.CredentialId;
        game.IsActive = request.IsActive;
        game.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return new GameSaveResult(await GetAsync(id, cancellationToken), null);
    }

    /// <summary>删除。名下有任何反馈或玩家一律拒绝——删除游戏绝不该成为数据丢失的捷径。</summary>
    public async Task<GameDeleteOutcome> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var game = await db.Games.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (game is null)
        {
            return GameDeleteOutcome.NotFound;
        }

        var hasData = await db.Feedbacks.AnyAsync(f => f.GameId == id, cancellationToken)
            || await db.Players.AnyAsync(p => p.GameId == id, cancellationToken);
        if (hasData)
        {
            return GameDeleteOutcome.HasData;
        }

        db.Games.Remove(game);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("管理员删除了 Game GameId={GameId} AppId={AppId}", id, game.SteamAppId);
        return GameDeleteOutcome.Deleted;
    }

    private sealed record ValidatedGame(
        string Name,
        string SteamAppId,
        string Identity,
        int? CredentialId,
        string? Error);

    private static async Task<ValidatedGame> ValidateAsync(
        AppDbContext db, GameEditRequest request, int? excludeGameId, CancellationToken cancellationToken)
    {
        ValidatedGame Fail(string error) => new(string.Empty, string.Empty, string.Empty, null, error);

        var nameError = GameValidation.ValidateName(request.Name);
        if (nameError is not null)
        {
            return Fail(nameError);
        }
        var appIdError = GameValidation.ValidateSteamAppId(request.SteamAppId);
        if (appIdError is not null)
        {
            return Fail(appIdError);
        }
        var identityError = GameValidation.ValidateIdentity(request.Identity);
        if (identityError is not null)
        {
            return Fail(identityError);
        }

        var steamAppId = GameValidation.NormalizeAppId(request.SteamAppId);
        var identity = request.Identity!.Trim();

        if (await db.Games.AnyAsync(g => g.SteamAppId == steamAppId && g.Id != excludeGameId, cancellationToken))
        {
            return Fail($"Steam AppID「{steamAppId}」已被另一个游戏使用");
        }
        if (request.CredentialId is not null
            && !await db.SteamCredentials.AnyAsync(c => c.Id == request.CredentialId, cancellationToken))
        {
            return Fail("所选凭据不存在");
        }

        return new ValidatedGame(request.Name!.Trim(), steamAppId, identity, request.CredentialId, null);
    }
}
