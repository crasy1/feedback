using GameFeedback.Data;
using GameFeedback.Domain;
using GameFeedback.Services;
using GameFeedback.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GameFeedback.Tests;

[Collection("Integration")]
public sealed class AdminFeedbackServiceTests(IntegrationTestFixture fixture)
{
    private async Task<(Player Player, int FeedbackId)> SeedPlayerWithFeedbacksAsync(
        int count,
        string? gameVersion = "9.9.9",
        FeedbackType type = FeedbackType.Bug,
        FeedbackStatus status = FeedbackStatus.Open,
        string? steamId = null,
        string? steamName = "seeded")
    {
        var scope = fixture.DefaultFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var player = new Player
        {
            SteamId = steamId ?? PlayerClient.UniqueSteamId(),
            SteamName = steamName,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Players.Add(player);
        Feedback? last = null;
        for (var i = 0; i < count; i++)
        {
            last = new Feedback
            {
                Player = player,
                Type = type,
                Title = $"反馈{i}",
                Content = "内容",
                Status = status,
                GameVersion = gameVersion,
                CreatedAt = DateTime.UtcNow.AddMinutes(i),
            };
            db.Feedbacks.Add(last);
        }
        await db.SaveChangesAsync();
        return (player, last!.Id);
    }

    private async Task<AdminFeedbackService> CreateServiceAsync()
    {
        _ = fixture.DefaultFactory.CreateClient();
        var scope = fixture.DefaultFactory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AdminFeedbackService>();
    }

    /// <summary>唯一的 15 位数字前缀，用于隔离"按 SteamID 前缀筛选"的测试数据（共享库不能互相串扰）。</summary>
    private static string UniqueSteamIdPrefix() =>
        "76561" + new string(Guid.NewGuid().ToByteArray().Select(b => (char)('0' + b % 10)).Take(10).ToArray());

    /// <summary>唯一的数字串，用来给昵称打上不会与其它用例冲突的标记。</summary>
    private static string UniqueToken() =>
        new(Guid.NewGuid().ToByteArray().Select(b => (char)('0' + b % 10)).Take(12).ToArray());

    [Fact]
    public async Task List_filters_by_status_type_and_game_version()
    {
        await SeedPlayerWithFeedbacksAsync(3, gameVersion: "8.8.8", type: FeedbackType.Suggestion, status: FeedbackStatus.Closed);
        var service = await CreateServiceAsync();

        var (items, total) = await service.ListAsync(
            new AdminFeedbackQuery(FeedbackStatus.Closed, FeedbackType.Suggestion, "8.8.8"), 1, 50, CancellationToken.None);

        Assert.Equal(3, total);
        Assert.All(items, f =>
        {
            Assert.Equal(FeedbackStatus.Closed, f.Status);
            Assert.Equal(FeedbackType.Suggestion, f.Type);
            Assert.Equal("8.8.8", f.GameVersion);
        });
    }

    [Fact]
    public async Task List_paginates_newest_first()
    {
        await SeedPlayerWithFeedbacksAsync(120, gameVersion: "7.7.7");
        var service = await CreateServiceAsync();

        var (page1, total) = await service.ListAsync(new AdminFeedbackQuery(GameVersion: "7.7.7"), 1, 50, CancellationToken.None);
        Assert.Equal(120, total);
        Assert.Equal(50, page1.Count);

        var (page3, _) = await service.ListAsync(new AdminFeedbackQuery(GameVersion: "7.7.7"), 3, 50, CancellationToken.None);
        Assert.Equal(20, page3.Count);

        Assert.True(page1[0].CreatedAt >= page1[^1].CreatedAt);
    }

    /// <summary>纯数字输入按 SteamID64 前缀匹配：允许只记得前几位。</summary>
    [Fact]
    public async Task List_filters_by_player_steam_id_prefix()
    {
        var prefix = UniqueSteamIdPrefix();
        await SeedPlayerWithFeedbacksAsync(2, gameVersion: "6.6.6", steamId: prefix + "11");
        await SeedPlayerWithFeedbacksAsync(1, gameVersion: "6.6.6", steamId: prefix + "22");
        await SeedPlayerWithFeedbacksAsync(1, gameVersion: "6.6.6"); // 无关玩家，SteamID 前缀不同
        var service = await CreateServiceAsync();

        var (items, total) = await service.ListAsync(new AdminFeedbackQuery(Player: prefix), 1, 50, CancellationToken.None);

        Assert.Equal(3, total);
        Assert.All(items, f => Assert.StartsWith(prefix, f.Player!.SteamId));
    }

    /// <summary>非数字输入按昵称包含匹配，且不区分大小写（ILIKE）。</summary>
    [Fact]
    public async Task List_filters_by_player_nickname_case_insensitively()
    {
        var token = UniqueToken();
        await SeedPlayerWithFeedbacksAsync(2, gameVersion: "5.5.5", steamName: $"Veteran-{token}");
        await SeedPlayerWithFeedbacksAsync(1, gameVersion: "5.5.5", steamName: $"Rookie-{token}");
        var service = await CreateServiceAsync();

        var (items, total) = await service.ListAsync(
            new AdminFeedbackQuery(Player: $"veteran-{token}"), 1, 50, CancellationToken.None);

        Assert.Equal(2, total);
        Assert.All(items, f => Assert.Equal($"Veteran-{token}", f.Player!.SteamName));
    }

    /// <summary>
    /// LIKE 元字符必须按字面处理：不转义的话 <c>_</c> 会匹配任意单字符、<c>%</c> 会匹配任意串，
    /// 于是搜昵称会莫名其妙命中别的玩家。
    /// </summary>
    [Theory]
    [InlineData('_', 'X')]
    [InlineData('%', 'Z')]
    public async Task List_player_filter_treats_like_wildcards_literally(char wildcard, char decoy)
    {
        var token = UniqueToken();
        var literal = $"w{wildcard}x{token}";
        await SeedPlayerWithFeedbacksAsync(1, gameVersion: "4.4.4", steamName: literal);
        await SeedPlayerWithFeedbacksAsync(1, gameVersion: "4.4.4", steamName: $"w{decoy}x{token}");
        var service = await CreateServiceAsync();

        var (items, total) = await service.ListAsync(new AdminFeedbackQuery(Player: literal), 1, 50, CancellationToken.None);

        Assert.Equal(1, total);
        Assert.Equal(literal, items[0].Player!.SteamName);
    }

    /// <summary>以反斜杠结尾的输入若不转义，Postgres 会直接报 "pattern must not end with escape character"。</summary>
    [Fact]
    public async Task List_player_filter_handles_trailing_backslash()
    {
        var token = UniqueToken();
        var nickname = $"trailing\\{token}\\";
        await SeedPlayerWithFeedbacksAsync(1, gameVersion: "3.3.3", steamName: nickname);
        var service = await CreateServiceAsync();

        var (items, total) = await service.ListAsync(new AdminFeedbackQuery(Player: nickname), 1, 50, CancellationToken.None);

        Assert.Equal(1, total);
        Assert.Equal(nickname, items[0].Player!.SteamName);
    }

    /// <summary>玩家筛选与状态 / 类型 / 版本是 AND 关系。</summary>
    [Fact]
    public async Task List_combines_player_filter_with_other_filters()
    {
        var token = UniqueToken();
        await SeedPlayerWithFeedbacksAsync(2, gameVersion: "2.2.2", type: FeedbackType.Bug, status: FeedbackStatus.Open, steamName: $"Combo-{token}");
        await SeedPlayerWithFeedbacksAsync(1, gameVersion: "2.2.2", type: FeedbackType.Suggestion, status: FeedbackStatus.Open, steamName: $"Combo-{token}");
        await SeedPlayerWithFeedbacksAsync(1, gameVersion: "2.2.2", type: FeedbackType.Bug, status: FeedbackStatus.Closed, steamName: $"Combo-{token}");
        var service = await CreateServiceAsync();

        var (items, total) = await service.ListAsync(
            new AdminFeedbackQuery(FeedbackStatus.Open, FeedbackType.Bug, "2.2.2", $"Combo-{token}"),
            1, 50, CancellationToken.None);

        Assert.Equal(2, total);
        Assert.All(items, f =>
        {
            Assert.Equal(FeedbackStatus.Open, f.Status);
            Assert.Equal(FeedbackType.Bug, f.Type);
            Assert.Equal($"Combo-{token}", f.Player!.SteamName);
        });
    }

    /// <summary>
    /// 翻页必须原样保留全部筛选条件。
    /// 曾经的 bug：GoToPage 自己拼查询串、只写 page/pageSize，翻页会静默把查询范围放大。
    /// 现在筛选与翻页共用 <see cref="AdminFeedbackQuery.ToQueryParameters"/>，这条测试盯着它不要退化。
    /// </summary>
    [Fact]
    public void Query_parameters_preserve_every_filter_when_paging()
    {
        var query = new AdminFeedbackQuery(FeedbackStatus.InProgress, FeedbackType.Suggestion, "1.2.3", "76561198");

        var parameters = query.ToQueryParameters(page: 3, pageSize: 20);

        Assert.Equal(3, parameters["page"]);
        Assert.Equal(20, parameters["pageSize"]);
        Assert.Equal("InProgress", parameters["status"]);
        Assert.Equal("Suggestion", parameters["type"]);
        Assert.Equal("1.2.3", parameters["gameVersion"]);
        Assert.Equal("76561198", parameters["player"]);
    }

    [Fact]
    public void Query_parameters_omit_empty_filters()
    {
        var parameters = new AdminFeedbackQuery().ToQueryParameters(page: 1, pageSize: 50);

        Assert.Null(parameters["status"]);
        Assert.Null(parameters["type"]);
        Assert.Null(parameters["gameVersion"]);
        Assert.Null(parameters["player"]);
    }

    [Fact]
    public async Task Reply_creates_admin_comment()
    {
        var (_, feedbackId) = await SeedPlayerWithFeedbacksAsync(1);
        var service = await CreateServiceAsync();

        var comment = await service.ReplyAsync("admin-user-1", feedbackId, "已收到，正在排查", CancellationToken.None);

        Assert.Equal(CommentAuthorType.Admin, comment.AuthorType);
        Assert.Equal("admin-user-1", comment.AdminUserId);
        Assert.Null(comment.PlayerId);

        var detail = await service.GetAsync(feedbackId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Contains(detail!.Comments, c => c.Content == "已收到，正在排查" && c.AuthorType == CommentAuthorType.Admin);
    }

    [Fact]
    public async Task Change_status_persists_and_touches_updated_at()
    {
        var (_, feedbackId) = await SeedPlayerWithFeedbacksAsync(1, status: FeedbackStatus.Open);
        var service = await CreateServiceAsync();

        var changed = await service.ChangeStatusAsync(feedbackId, FeedbackStatus.InProgress, CancellationToken.None);

        Assert.True(changed);
        var detail = await service.GetAsync(feedbackId, CancellationToken.None);
        Assert.Equal(FeedbackStatus.InProgress, detail!.Status);
        Assert.NotNull(detail.UpdatedAt);

        Assert.False(await service.ChangeStatusAsync(999_999, FeedbackStatus.Open, CancellationToken.None));
    }
}
