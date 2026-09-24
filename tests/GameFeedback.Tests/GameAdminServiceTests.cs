using System.Net;
using System.Net.Http.Json;
using GameFeedback.Data;
using GameFeedback.Domain;
using GameFeedback.Services;
using GameFeedback.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameFeedback.Tests;

/// <summary>
/// 管理端的 Game 增删改查。管理端是唯一的游戏来源（没有环境变量、没有种子），
/// 所以这里的校验与删除保护就是"多游戏"能安全运行的地基。
/// </summary>
[Collection("Integration")]
public sealed class GameAdminServiceTests(IntegrationTestFixture fixture)
{
    private async Task<T> GetServiceAsync<T>(GameFeedbackApplicationFactory? factory = null)
        where T : notnull
    {
        var target = factory ?? fixture.DefaultFactory;
        _ = target.CreateClient();
        var scope = target.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    private static GameEditRequest Request(
        string? steamAppId,
        string? name = "测试游戏",
        string? identity = TestGames.DefaultIdentity,
        int? credentialId = null,
        bool isActive = true) =>
        new(name, steamAppId, identity, credentialId, isActive);

    // ------------------------------------------------------- 首次启动的空库状态

    /// <summary>
    /// 一个游戏都没有时管理端必须把管理员送去"添加游戏"（守卫在 Blazor 布局里，
    /// 因为 InteractiveServer 页面 prerender 关闭、HTTP 响应里没有渲染标记，见 AdminAssetTests 的说明）。
    /// 这里覆盖它能依赖的那一半：<see cref="GameAdminService.HasAnyAsync"/>。
    /// </summary>
    [Fact]
    public async Task HasAny_is_false_on_an_empty_database_and_true_after_the_first_game()
    {
        var factory = await fixture.CreateFactoryOnEmptyDatabaseAsync();
        var games = await GetServiceAsync<GameAdminService>(factory);

        Assert.False(await games.HasAnyAsync(CancellationToken.None));
        Assert.Empty(await games.ListAsync(CancellationToken.None));

        var created = await games.CreateAsync(Request(TestGames.UniqueAppId(), "第一个游戏"), CancellationToken.None);

        Assert.True(created.Succeeded, created.Error);
        Assert.True(await games.HasAnyAsync(CancellationToken.None));
        Assert.Single(await games.ListAsync(CancellationToken.None));
    }

    // ------------------------------------------------------- 创建

    [Fact]
    public async Task Create_persists_the_game_and_returns_its_view()
    {
        var credential = await SeedCredentialAsync();
        var games = await GetServiceAsync<GameAdminService>();
        var appId = TestGames.UniqueAppId();

        var result = await games.CreateAsync(
            Request(appId, "新游戏", "custom-identity", credential.Id), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        var view = result.Game!;
        Assert.True(view.Id > 0);
        Assert.Equal("新游戏", view.Name);
        Assert.Equal(appId, view.SteamAppId);
        Assert.Equal("custom-identity", view.Identity);
        Assert.Equal(credential.Id, view.CredentialId);
        Assert.Equal(credential.Name, view.CredentialName);
        Assert.True(view.IsActive);
        Assert.True(view.IsConfigured);
        Assert.Equal(0, view.FeedbackCount);
        Assert.Equal(0, view.PlayerCount);

        var reloaded = await games.GetAsync(view.Id, CancellationToken.None);
        Assert.Equal(view, reloaded);
    }

    /// <summary>
    /// AppID 是必填的：它同时是玩家 API 的路径段，没有它的游戏根本不能被任何客户端寻址
    /// （只有升级迁移为存量数据建的占位行会是空的）。拒绝要走"返回错误"而不是抛异常，
    /// 否则管理端表单会被 500 掉。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_without_an_app_id_is_rejected_without_throwing(string? appId)
    {
        var games = await GetServiceAsync<GameAdminService>();

        var result = await games.CreateAsync(Request(appId, "还没配好"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Game);
        Assert.NotNull(result.Error);
    }

    /// <summary>手工录入的 AppID 常带空白，统一去掉首尾空白后再存（否则会出现"看起来一样"的两个游戏）。</summary>
    [Fact]
    public async Task App_id_is_trimmed_before_it_is_stored()
    {
        var games = await GetServiceAsync<GameAdminService>();
        var appId = TestGames.UniqueAppId();

        var result = await games.CreateAsync(
            Request($"  {appId}  ", "带空白的 AppID"), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(appId, result.Game!.SteamAppId);
    }

    [Fact]
    public async Task Duplicate_steam_app_id_is_rejected_without_throwing()
    {
        var existing = await fixture.SeedGameAsync();
        var games = await GetServiceAsync<GameAdminService>();

        var result = await games.CreateAsync(
            Request(existing.AppId, "抢 AppID"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Game);
        Assert.Contains(existing.AppId!, result.Error);
    }

    /// <summary>
    /// AppID 的唯一性只由 games.SteamAppId 上的唯一索引（+ 服务层查询）保证，没有软删除：
    /// 游戏删掉之后它的 AppID 就该能被另一个游戏接手，否则"删错了再建回来"会永久卡住。
    /// </summary>
    [Fact]
    public async Task An_app_id_can_be_reused_after_its_game_is_deleted()
    {
        var games = await GetServiceAsync<GameAdminService>();
        var appId = TestGames.UniqueAppId();
        var first = await games.CreateAsync(Request(appId, "第一个"), CancellationToken.None);
        Assert.True(first.Succeeded, first.Error);

        Assert.Equal(GameDeleteOutcome.Deleted, await games.DeleteAsync(first.Game!.Id, CancellationToken.None));

        var reused = await games.CreateAsync(Request(appId, "接手这个名字的游戏"), CancellationToken.None);

        Assert.True(reused.Succeeded, reused.Error);
        Assert.Equal(appId, reused.Game!.SteamAppId);
        Assert.NotEqual(first.Game.Id, reused.Game.Id);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("not-a-number")]
    [InlineData("480abc")]
    [InlineData("-480")]
    [InlineData("48.0")]
    [InlineData("0")]
    [InlineData("000")]
    [InlineData("12345678901")]
    public async Task Invalid_steam_app_id_is_rejected(string appId)
    {
        var games = await GetServiceAsync<GameAdminService>();

        var result = await games.CreateAsync(Request(appId, "坏 AppID"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Game);
    }

    [Fact]
    public async Task Missing_identity_is_rejected()
    {
        var games = await GetServiceAsync<GameAdminService>();

        var result = await games.CreateAsync(
            Request(TestGames.UniqueAppId(), "缺 identity", identity: "  "), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Game);
    }

    [Fact]
    public async Task Unknown_credential_is_rejected()
    {
        var games = await GetServiceAsync<GameAdminService>();

        var result = await games.CreateAsync(
            Request(TestGames.UniqueAppId(), "凭据不存在", credentialId: 999_999), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Game);
    }

    // ------------------------------------------------------- 更新

    [Fact]
    public async Task Update_changes_fields_and_keeps_identity_rules()
    {
        var game = await fixture.SeedGameAsync(isActive: true);
        var games = await GetServiceAsync<GameAdminService>();
        var newAppId = TestGames.UniqueAppId();

        var result = await games.UpdateAsync(
            game.Id, Request(newAppId, "改名后的游戏", isActive: false), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("改名后的游戏", result.Game!.Name);
        Assert.Equal(newAppId, result.Game.SteamAppId);
        Assert.False(result.Game.IsActive);

        // 停用不影响数据，只是拒绝玩家流量（见 MultiGameEndpointTests）。
        Assert.True(await games.HasAnyAsync(CancellationToken.None));
    }

    /// <summary>编辑现有的游戏时不能把 AppID 清空——那等于把这个游戏从玩家 API 上摘下来。</summary>
    [Fact]
    public async Task Update_cannot_clear_the_app_id()
    {
        var game = await fixture.SeedGameAsync();
        var games = await GetServiceAsync<GameAdminService>();

        var result = await games.UpdateAsync(
            game.Id, Request(null, "想清空 AppID", credentialId: game.CredentialId), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Game);

        // 原值原封不动。
        var unchanged = await games.GetAsync(game.Id, CancellationToken.None);
        Assert.Equal(game.AppId, unchanged!.SteamAppId);
    }

    [Fact]
    public async Task Update_can_keep_its_own_app_id()
    {
        var game = await fixture.SeedGameAsync();
        var games = await GetServiceAsync<GameAdminService>();

        var result = await games.UpdateAsync(
            game.Id,
            Request(game.AppId, "只改名字", credentialId: game.CredentialId),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(game.AppId, result.Game!.SteamAppId);
    }

    [Fact]
    public async Task Update_rejects_an_app_id_taken_by_another_game()
    {
        var first = await fixture.SeedGameAsync();
        var second = await fixture.SeedGameAsync();
        var games = await GetServiceAsync<GameAdminService>();

        var result = await games.UpdateAsync(
            second.Id, Request(first.AppId, "和另一个游戏抢 AppID"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(first.AppId!, result.Error);
    }

    [Fact]
    public async Task Update_unknown_game_returns_an_error()
    {
        var games = await GetServiceAsync<GameAdminService>();

        var result = await games.UpdateAsync(999_999, Request(TestGames.UniqueAppId()), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Game);
    }

    /// <summary>
    /// 改 AppID 立刻生效（解析器每请求读库，没有进程内缓存），旧路径立刻变成 404 game_not_found。
    /// 这也是为什么管理端要警告"改 AppID 等于让所有已发布构建失效"——
    /// 客户端本来就在某个 AppID 下运行，改了它就等于换了门牌号。
    /// </summary>
    [Fact]
    public async Task Renaming_an_app_id_takes_effect_immediately_and_retires_the_old_path()
    {
        var game = await fixture.SeedGameAsync();
        var newAppId = TestGames.UniqueAppId();
        var steam = new FakeSteamHandler();
        var factory = fixture.CreateFactory(steam);
        var games = await GetServiceAsync<GameAdminService>(factory);

        var updated = await games.UpdateAsync(
            game.Id,
            Request(newAppId, "改名后的游戏", credentialId: game.CredentialId),
            CancellationToken.None);
        Assert.True(updated.Succeeded, updated.Error);
        Assert.Equal(newAppId, updated.Game!.SteamAppId);

        var client = factory.CreateClient();
        // 新 AppID 立刻可用。
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketOk("76561198000000055"));
        steam.SetProfileResponse(FakeSteamHandler.Profile("Renamed", null));
        var login = await client.PostAsJsonAsync(
            TestGame.PathForAppId(newAppId, "auth/steam"), new { ticket = "ticket" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // 旧 AppID 立刻不可用。
        var oldPath = await client.PostAsJsonAsync(game.AuthPath, new { ticket = "ticket" });
        Assert.Equal(HttpStatusCode.NotFound, oldPath.StatusCode);
        Assert.Equal("game_not_found", await TestGames.ReadProblemCodeAsync(oldPath));
    }

    // ------------------------------------------------------- 删除

    [Fact]
    public async Task Delete_of_an_empty_game_succeeds()
    {
        var game = await fixture.SeedGameAsync();
        var games = await GetServiceAsync<GameAdminService>();

        Assert.Equal(GameDeleteOutcome.Deleted, await games.DeleteAsync(game.Id, CancellationToken.None));
        Assert.Null(await games.GetAsync(game.Id, CancellationToken.None));

        using var scope = fixture.DefaultFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Games.AnyAsync(g => g.Id == game.Id));
    }

    [Fact]
    public async Task Delete_of_an_unknown_game_returns_not_found()
    {
        var games = await GetServiceAsync<GameAdminService>();

        Assert.Equal(GameDeleteOutcome.NotFound, await games.DeleteAsync(999_999, CancellationToken.None));
    }

    /// <summary>
    /// 名下有反馈的游戏拒绝硬删：删除游戏绝不该成为数据丢失的捷径（外键也是 Restrict）。
    /// </summary>
    [Fact]
    public async Task Delete_of_a_game_with_feedback_is_refused_and_keeps_the_data()
    {
        var game = await fixture.SeedGameAsync();
        var (_, feedbackId) = await SeedFeedbackAsync(game);
        var games = await GetServiceAsync<GameAdminService>();

        Assert.Equal(GameDeleteOutcome.HasData, await games.DeleteAsync(game.Id, CancellationToken.None));

        using var scope = fixture.DefaultFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Games.AnyAsync(g => g.Id == game.Id));
        Assert.True(await db.Feedbacks.AnyAsync(f => f.Id == feedbackId));
    }

    [Fact]
    public async Task Delete_of_a_game_with_players_is_refused_and_keeps_the_players()
    {
        var game = await fixture.SeedGameAsync();
        var steamId = PlayerClient.UniqueSteamId();
        using (var scope = fixture.DefaultFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Players.Add(new Player
            {
                GameId = game.Id,
                SteamId = steamId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var games = await GetServiceAsync<GameAdminService>();
        Assert.Equal(GameDeleteOutcome.HasData, await games.DeleteAsync(game.Id, CancellationToken.None));

        using var verifyScope = fixture.DefaultFactory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await verifyDb.Players.AnyAsync(p => p.GameId == game.Id && p.SteamId == steamId));
    }

    /// <summary>删除游戏不会连带删掉别的游戏的数据。</summary>
    [Fact]
    public async Task Deleting_one_game_leaves_other_games_untouched()
    {
        var doomed = await fixture.SeedGameAsync();
        var keeper = await fixture.SeedGameAsync();
        var (_, keeperFeedbackId) = await SeedFeedbackAsync(keeper);
        var games = await GetServiceAsync<GameAdminService>();

        Assert.Equal(GameDeleteOutcome.Deleted, await games.DeleteAsync(doomed.Id, CancellationToken.None));

        using var scope = fixture.DefaultFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Games.AnyAsync(g => g.Id == keeper.Id));
        Assert.True(await db.Feedbacks.AnyAsync(f => f.Id == keeperFeedbackId));
    }

    [Fact]
    public async Task List_is_ordered_by_name()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var later = await fixture.SeedGameAsync(name: $"zz-{suffix}");
        var earlier = await fixture.SeedGameAsync(name: $"aa-{suffix}");
        var games = await GetServiceAsync<GameAdminService>();

        var all = await games.ListAsync(CancellationToken.None);

        var ours = all.Where(g => g.Id == later.Id || g.Id == earlier.Id).ToList();
        Assert.Equal(new[] { earlier.Id, later.Id }, ours.Select(g => g.Id));
    }

    private async Task<(Player Player, int FeedbackId)> SeedFeedbackAsync(TestGame game)
    {
        using var scope = fixture.DefaultFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var player = new Player
        {
            GameId = game.Id,
            SteamId = PlayerClient.UniqueSteamId(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var feedback = new Feedback
        {
            GameId = game.Id,
            Player = player,
            Type = FeedbackType.Bug,
            Title = "删除保护",
            Content = "内容",
            CreatedAt = DateTime.UtcNow,
        };
        db.Feedbacks.Add(feedback);
        await db.SaveChangesAsync();
        return (player, feedback.Id);
    }

    private async Task<CredentialAdminView> SeedCredentialAsync() =>
        await TestCredentials.CreateAsync(fixture, $"cred-{Guid.NewGuid():N}"[..12]);
}
