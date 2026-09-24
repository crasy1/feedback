using GameFeedback.Data;
using GameFeedback.Domain;
using GameFeedback.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameFeedback.Tests;

[Collection("Integration")]
public sealed class DomainPersistenceTests(IntegrationTestFixture fixture)
{
    private async Task<AppDbContext> CreateContextAsync()
    {
        // 触发应用启动（自动迁移在此执行），再从容器解析 DbContext。
        _ = fixture.DefaultFactory.CreateClient();
        var scope = fixture.DefaultFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.CanConnectAsync();
        return db;
    }

    [Fact]
    public async Task Entities_round_trip_through_real_provider()
    {
        using var db = await CreateContextAsync();
        var game = fixture.DefaultGame;
        var steamId = $"100000{Guid.NewGuid():N}"[..20];

        var player = new Player
        {
            GameId = game.Id,
            SteamId = steamId,
            SteamName = "tester",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Players.Add(player);
        var feedback = new Feedback
        {
            GameId = game.Id,
            Player = player,
            Type = FeedbackType.Bug,
            Title = "崩溃",
            Content = "进地图崩溃",
            GameVersion = "1.2.3",
            CreatedAt = DateTime.UtcNow,
        };
        db.Feedbacks.Add(feedback);
        db.FeedbackComments.Add(new FeedbackComment
        {
            Feedback = feedback,
            AuthorType = CommentAuthorType.Player,
            PlayerId = player.Id,
            Content = "补充：每次都崩",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var reloaded = await db.Feedbacks
            .Include(f => f.Player)
            .Include(f => f.Game)
            .Include(f => f.Comments)
            .SingleAsync(f => f.Id == feedback.Id);

        Assert.Equal(FeedbackType.Bug, reloaded.Type);
        Assert.Equal(FeedbackStatus.Open, reloaded.Status);
        Assert.Equal(steamId, reloaded.Player!.SteamId);
        Assert.Equal(game.Id, reloaded.Game!.Id);
        Assert.Equal(CommentAuthorType.Player, reloaded.Comments.Single().AuthorType);
    }

    [Fact]
    public async Task Environment_and_playtime_fields_round_trip()
    {
        using var db = await CreateContextAsync();
        var steamId = $"300000{Guid.NewGuid():N}"[..20];

        var player = new Player
        {
            GameId = fixture.DefaultGame.Id,
            SteamId = steamId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var feedback = new Feedback
        {
            GameId = fixture.DefaultGame.Id,
            Player = player,
            Type = FeedbackType.Bug,
            Title = "崩溃",
            Content = "进地图崩溃",
            Gpu = "RTX 4070",
            Cpu = "Intel(R) Core(TM) i7-6700K CPU @ 4.00GHz",
            MemoryTotalMb = 16384,
            PlaytimeMinutes = 2361,
            CreatedAt = DateTime.UtcNow,
        };
        db.Feedbacks.Add(feedback);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var reloaded = await db.Feedbacks.SingleAsync(f => f.Id == feedback.Id);

        Assert.Equal("Intel(R) Core(TM) i7-6700K CPU @ 4.00GHz", reloaded.Cpu);
        Assert.Equal(16384, reloaded.MemoryTotalMb);
        Assert.Equal(2361, reloaded.PlaytimeMinutes);
    }

    /// <summary>
    /// 唯一性是复合的 (GameId, SteamId)：同一个 Steam 账号在同一个游戏里只能有一行，
    /// 否则"按 SteamID 找玩家"会选到随机一行；但它在不同游戏里必须能各有一行，
    /// 这正是从单游戏升级到多游戏的核心变化。
    /// </summary>
    [Fact]
    public async Task Duplicate_steam_id_in_same_game_is_rejected_by_database()
    {
        using var db = await CreateContextAsync();
        var steamId = $"200000{Guid.NewGuid():N}"[..20];

        db.Players.Add(new Player
        {
            GameId = fixture.DefaultGame.Id,
            SteamId = steamId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        db.Players.Add(new Player
        {
            GameId = fixture.DefaultGame.Id,
            SteamId = steamId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Same_steam_id_in_two_games_is_accepted_by_database()
    {
        using var db = await CreateContextAsync();
        var otherGame = await fixture.SeedGameAsync();
        var steamId = $"400000{Guid.NewGuid():N}"[..20];

        db.Players.Add(new Player
        {
            GameId = fixture.DefaultGame.Id,
            SteamId = steamId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.Players.Add(new Player
        {
            GameId = otherGame.Id,
            SteamId = steamId,
            SteamName = "同一个账号的另一个游戏",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var rows = await db.Players.Where(p => p.SteamId == steamId).ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(
            new[] { fixture.DefaultGame.Id, otherGame.Id }.OrderBy(id => id),
            rows.Select(r => r.GameId).OrderBy(id => id));
    }

    /// <summary>Game 的 SteamAppId 为 null 是合法状态（未配置完成），且多个 null 不冲突。</summary>
    [Fact]
    public async Task Games_without_steam_app_id_do_not_collide_on_the_unique_index()
    {
        using var db = await CreateContextAsync();
        var first = await fixture.SeedGameAsync(withAppId: false, withCredential: false);
        var second = await fixture.SeedGameAsync(withAppId: false, withCredential: false);

        db.ChangeTracker.Clear();
        var rows = await db.Games
            .Where(g => g.Id == first.Id || g.Id == second.Id)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Null(r.SteamAppId));
        Assert.All(rows, r => Assert.False(r.IsFullyConfigured));
    }
}
