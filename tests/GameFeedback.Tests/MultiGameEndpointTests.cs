using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GameFeedback.Data;
using GameFeedback.Domain;
using GameFeedback.Services;
using GameFeedback.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameFeedback.Tests;

/// <summary>
/// 本次升级的核心行为：一个实例服务多个游戏。
/// <list type="bullet">
/// <item>游戏来自路径 <c>/g/{appId}</c>（Steam AppID），令牌必须是为同一个游戏签发的；</item>
/// <item>根路径的玩家 API 已永久移除，回 404 game_required；</item>
/// <item>游戏之间数据必须完全隔离。</item>
/// </list>
/// </summary>
[Collection("Integration")]
public sealed class MultiGameEndpointTests(IntegrationTestFixture fixture)
{
    /// <summary>未认证地发一个请求，返回响应（不抛异常）。</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await client.SendAsync(request);
    }

    private static void AssertAuthorized(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    // ---------------------------------------------------------------- 根路径

    /// <summary>
    /// 根路径上的玩家 API 已永久移除。最常见的线上故障是"某个游戏的构建还把 BaseUrl 指向根路径"，
    /// 所以这里必须回一个稳定的 game_required，而不是一个空白 404。
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/auth/steam")]
    [InlineData("GET", "/api/feedback/mine")]
    [InlineData("POST", "/api/feedback")]
    [InlineData("GET", "/api/feedback")]
    [InlineData("GET", "/api/feedback/1")]
    [InlineData("POST", "/api/feedback/1/comments")]
    public async Task Root_path_player_api_answers_404_game_required(string method, string path)
    {
        var client = fixture.DefaultFactory.CreateClient();

        var response = await SendAsync(
            client, new HttpMethod(method), path, method == "GET" ? null : new { ticket = "t" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("game_required", await TestGames.ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task Health_still_lives_at_the_root()
    {
        var client = fixture.DefaultFactory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------- 游戏解析

    [Fact]
    public async Task Unknown_app_id_answers_404_game_not_found()
    {
        var client = fixture.DefaultFactory.CreateClient();
        var missing = TestGames.UniqueAppId();

        var login = await SendAsync(client, HttpMethod.Post, TestGame.PathForAppId(missing, "auth/steam"), new { ticket = "t" });
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
        Assert.Equal("game_not_found", await TestGames.ReadProblemCodeAsync(login));

        // 即使带着一个签名合法的令牌也一样：路径里的游戏不存在就没有可授权的对象。
        // （缺令牌会先被授权中间件拦成 401，所以这里必须带上令牌才测得到解析分支；
        // 令牌里的 game 声明在这里无关紧要——解析失败发生在核对声明之前。）
        AssertAuthorized(client, TestGames.MintToken(PlayerClient.UniqueSteamId(), gameId: 999_999));
        var mine = await client.GetAsync(TestGame.PathForAppId(missing, "feedback/mine"));
        Assert.Equal(HttpStatusCode.NotFound, mine.StatusCode);
        Assert.Equal("game_not_found", await TestGames.ReadProblemCodeAsync(mine));
    }

    [Fact]
    public async Task Disabled_game_answers_403_game_disabled()
    {
        var disabled = await fixture.SeedGameAsync(isActive: false);
        var client = fixture.DefaultFactory.CreateClient();

        var login = await client.PostAsJsonAsync(disabled.AuthPath, new { ticket = "t" });

        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("game_disabled", await TestGames.ReadProblemCodeAsync(login));

        // 停用的游戏连令牌都不能用：拿到令牌之后被停用也要立刻失效。
        AssertAuthorized(client, TestGames.MintToken(PlayerClient.UniqueSteamId(), disabled.Id));
        var mine = await client.GetAsync(disabled.MinePath);
        Assert.Equal(HttpStatusCode.Forbidden, mine.StatusCode);
        Assert.Equal("game_disabled", await TestGames.ReadProblemCodeAsync(mine));
    }

    /// <summary>停用只挡流量，绝不动数据：反馈与玩家必须原封不动（改删数据只能靠显式删除，而删除会被拒）。</summary>
    [Fact]
    public async Task Disabling_a_game_leaves_its_feedback_and_players_untouched()
    {
        var game = await fixture.SeedGameAsync(isActive: true);
        var steamId = PlayerClient.UniqueSteamId();
        var steam = new FakeSteamHandler();
        var factory = fixture.CreateFactory(steam);
        var client = factory.CreateClient();
        AssertAuthorized(client, await PlayerClient.LoginAsync(client, steam, steamId, game.AppId!));
        var created = await client.PostAsJsonAsync(game.FeedbackPath,
            new Dictionary<string, object?> { ["type"] = "Bug", ["title"] = "停用前", ["content"] = "内容" });
        var feedbackId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var games = factory.Services.CreateScope().ServiceProvider.GetRequiredService<GameAdminService>();
        var updated = await games.UpdateAsync(
            game.Id,
            new GameEditRequest("停用的游戏", game.AppId, TestGames.DefaultIdentity, game.CredentialId, false),
            CancellationToken.None);
        Assert.True(updated.Succeeded, updated.Error);

        // 停用后玩家流量一律 403。
        Assert.Equal(
            HttpStatusCode.Forbidden, (await client.GetAsync(game.MinePath)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Feedbacks.AnyAsync(f => f.Id == feedbackId));
        Assert.True(await db.Players.AnyAsync(p => p.GameId == game.Id && p.SteamId == steamId));
    }

    /// <summary>
    /// 游戏配了 AppID 但没有可用凭据时登录 fail closed（401 steam_unavailable），且不问 Steam。
    /// 这是唯一可达的"配置未完成"分支：没有 AppID 的游戏根本解析不到（见 AppIdAddressingTests），
    /// 所以那个分支在 404 game_not_found 处就结束了。
    /// </summary>
    [Fact]
    public async Task Game_with_app_id_but_no_credential_answers_401_steam_unavailable()
    {
        var game = await fixture.SeedGameAsync(withCredential: false);
        var steam = new FakeSteamHandler();
        var client = fixture.CreateFactory(steam).CreateClient();

        var response = await client.PostAsJsonAsync(game.AuthPath, new { ticket = "ticket" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("steam_unavailable", await TestGames.ReadProblemCodeAsync(response));
        Assert.Empty(steam.RequestedPaths); // 配置不全时不去问 Steam。
    }

    /// <summary>
    /// 凭据存在但解不开（key ring 丢失）必须与"没配好"分开报：前者恢复 key ring 即可修复，
    /// 后者要管理员去补配置，混在一起就查不出原因。
    /// </summary>
    [Fact]
    public async Task Undecryptable_credential_answers_401_credential_unreadable()
    {
        var game = await fixture.SeedGameAsync();
        await CorruptCredentialAsync(game);

        var steam = new FakeSteamHandler();
        var client = fixture.CreateFactory(steam).CreateClient();

        var response = await client.PostAsJsonAsync(game.AuthPath, new { ticket = "ticket" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("credential_unreadable", await TestGames.ReadProblemCodeAsync(response));
        Assert.Empty(steam.RequestedPaths);
    }

    /// <summary>把该游戏的凭据密文换成解不开的乱码（模拟 key ring 丢失 / application name 变化）。</summary>
    private async Task CorruptCredentialAsync(TestGame game)
    {
        using var scope = fixture.DefaultFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var credential = await db.SteamCredentials.SingleAsync(c => c.Id == game.CredentialId!.Value);
        credential.EncryptedApiKey = "CfDJ8-not-a-real-protected-payload";
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- 令牌与游戏绑定

    [Fact]
    public async Task Token_minted_for_game_a_is_rejected_on_game_b()
    {
        var gameA = await fixture.SeedGameAsync();
        var gameB = await fixture.SeedGameAsync();
        var steam = new FakeSteamHandler();
        var client = fixture.CreateFactory(steam).CreateClient();
        AssertAuthorized(client, await PlayerClient.LoginAsync(client, steam, PlayerClient.UniqueSteamId(), gameA.AppId!));

        var mine = await client.GetAsync(gameB.MinePath);
        Assert.Equal(HttpStatusCode.Unauthorized, mine.StatusCode);
        Assert.Equal("game_mismatch", await TestGames.ReadProblemCodeAsync(mine));

        var create = await client.PostAsJsonAsync(gameB.FeedbackPath,
            new { type = "Bug", title = "越权", content = "越权" });
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        Assert.Equal("game_mismatch", await TestGames.ReadProblemCodeAsync(create));

        // 同一个令牌在自己的游戏里照常可用——拒绝的原因是游戏不匹配，不是令牌坏了。
        var own = await client.GetAsync(gameA.MinePath);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    /// <summary>
    /// 升级前签发的令牌没有 game 声明，无法证明它属于任何游戏，因此一律拒绝。
    /// 这类令牌客户端造不出来，只能用测试 JWT 参数直接签。
    /// </summary>
    [Fact]
    public async Task Legacy_token_without_game_claim_is_rejected()
    {
        var game = fixture.DefaultGame;
        var steam = new FakeSteamHandler();
        var factory = fixture.CreateFactory(steam);
        var client = factory.CreateClient();
        AssertAuthorized(client, TestGames.MintToken(PlayerClient.UniqueSteamId(), gameId: null));

        var mine = await client.GetAsync(game.MinePath);

        Assert.Equal(HttpStatusCode.Unauthorized, mine.StatusCode);
        Assert.Equal("game_mismatch", await TestGames.ReadProblemCodeAsync(mine));
    }

    /// <summary>老客户端拿到 401 game_mismatch 后重新登录一次即可恢复：新令牌带着 game 声明，路径也带前缀。</summary>
    [Fact]
    public async Task Legacy_token_recovers_by_re_authenticating_once()
    {
        var game = fixture.DefaultGame;
        var steam = new FakeSteamHandler();
        var factory = fixture.CreateFactory(steam);
        var client = factory.CreateClient();
        var steamId = PlayerClient.UniqueSteamId();
        AssertAuthorized(client, TestGames.MintToken(steamId, gameId: null));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(game.MinePath)).StatusCode);

        AssertAuthorized(client, await PlayerClient.LoginAsync(client, steam, steamId, game.AppId!));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(game.MinePath)).StatusCode);
    }

    [Fact]
    public async Task Token_with_a_foreign_game_claim_is_rejected_even_for_a_known_steam_id()
    {
        var game = await fixture.SeedGameAsync();
        var client = fixture.DefaultFactory.CreateClient();
        // 声明一个不存在的 GameId 也照样不匹配。
        AssertAuthorized(client, TestGames.MintToken(PlayerClient.UniqueSteamId(), gameId: game.Id + 999_999));

        var mine = await client.GetAsync(game.MinePath);

        Assert.Equal(HttpStatusCode.Unauthorized, mine.StatusCode);
        Assert.Equal("game_mismatch", await TestGames.ReadProblemCodeAsync(mine));
    }

    [Fact]
    public async Task Authenticated_token_without_players_row_is_rejected()
    {
        // 令牌游戏对得上，但库里没有这个 Steam 账号的玩家行：fail closed 成 401。
        var game = fixture.DefaultGame;
        var client = fixture.DefaultFactory.CreateClient();
        AssertAuthorized(client, TestGames.MintToken(PlayerClient.UniqueSteamId(), game.Id));

        var mine = await client.GetAsync(game.MinePath);

        Assert.Equal(HttpStatusCode.Unauthorized, mine.StatusCode);
    }

    // ---------------------------------------------------------------- 跨游戏隔离

    [Fact]
    public async Task Same_steam_id_in_two_games_gets_two_players_and_isolated_feedback()
    {
        var gameA = await fixture.SeedGameAsync();
        var gameB = await fixture.SeedGameAsync();
        var steamId = PlayerClient.UniqueSteamId();
        var steam = new FakeSteamHandler();
        var factory = fixture.CreateFactory(steam);
        var client = factory.CreateClient();

        var tokenA = await PlayerClient.LoginAsync(client, steam, steamId, gameA.AppId!);
        var tokenB = await PlayerClient.LoginAsync(client, steam, steamId, gameB.AppId!);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.Players.Where(p => p.SteamId == steamId).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(
                new[] { gameA.Id, gameB.Id }.OrderBy(id => id),
                rows.Select(r => r.GameId).OrderBy(id => id));
        }

        // 在 B 下建一条反馈。
        AssertAuthorized(client, tokenB);
        var created = await client.PostAsJsonAsync(gameB.FeedbackPath,
            new Dictionary<string, object?> { ["type"] = "Bug", ["title"] = "B 的反馈", ["content"] = "只在 B" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var feedbackB = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // A 下的"我的反馈"绝不该看到 B 的数据。
        AssertAuthorized(client, tokenA);
        var mineA = await client.GetFromJsonAsync<JsonElement>(gameA.MinePath);
        Assert.Equal(0, mineA.GetArrayLength());

        AssertAuthorized(client, tokenB);
        var mineB = await client.GetFromJsonAsync<JsonElement>(gameB.MinePath);
        Assert.Equal(1, mineB.GetArrayLength());
        Assert.Equal(feedbackB, mineB[0].GetProperty("id").GetInt32());

        // 拿着 A 的令牌去读 B 的路径：令牌与游戏不符 → 401 game_mismatch。
        AssertAuthorized(client, tokenA);
        var crossRead = await client.GetAsync(gameB.Api($"feedback/{feedbackB}"));
        Assert.Equal(HttpStatusCode.Unauthorized, crossRead.StatusCode);
        Assert.Equal("game_mismatch", await TestGames.ReadProblemCodeAsync(crossRead));
    }

    /// <summary>
    /// 同一个 SteamID 在两个游戏里是两个独立的 Player 行，所以"反馈 id"是游戏内的：
    /// 拿着 B 的令牌（与路径匹配）去读 A 的反馈 id，必须 404——既不是 200，也不泄漏"它存在"。
    /// </summary>
    [Fact]
    public async Task Feedback_id_from_another_game_reads_as_404()
    {
        var gameA = await fixture.SeedGameAsync();
        var gameB = await fixture.SeedGameAsync();
        var steamId = PlayerClient.UniqueSteamId();
        var steam = new FakeSteamHandler();
        var client = fixture.CreateFactory(steam).CreateClient();

        AssertAuthorized(client, await PlayerClient.LoginAsync(client, steam, steamId, gameA.AppId!));
        var created = await client.PostAsJsonAsync(gameA.FeedbackPath,
            new Dictionary<string, object?> { ["type"] = "Bug", ["title"] = "A 的反馈", ["content"] = "只在 A" });
        var feedbackA = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // 游戏对得上（B 的令牌 + B 的路径），但这条反馈不属于 B：404。
        AssertAuthorized(client, await PlayerClient.LoginAsync(client, steam, steamId, gameB.AppId!));
        var crossGame = await client.GetAsync(gameB.Api($"feedback/{feedbackA}"));
        Assert.Equal(HttpStatusCode.NotFound, crossGame.StatusCode);

        // 也不能在别人的反馈上追加评论。
        var crossComment = await client.PostAsJsonAsync(
            gameB.Api($"feedback/{feedbackA}/comments"), new { content = "越权" });
        Assert.Equal(HttpStatusCode.NotFound, crossComment.StatusCode);

        // 同游戏内换个玩家读：同样 404。
        AssertAuthorized(client, await PlayerClient.LoginAsync(client, steam, PlayerClient.UniqueSteamId(), gameA.AppId!));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(gameA.Api($"feedback/{feedbackA}"))).StatusCode);

        // 属主自己读得到。
        AssertAuthorized(client, TestGames.MintToken(steamId, gameA.Id));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(gameA.Api($"feedback/{feedbackA}"))).StatusCode);
    }

    // ---------------------------------------------------------------- 游玩时长按游戏

    [Fact]
    public async Task Playtime_snapshot_uses_each_games_own_app_id()
    {
        var appIdA = TestGames.UniqueAppId();
        var appIdB = TestGames.UniqueAppId();
        var gameA = await fixture.SeedGameAsync(steamAppId: appIdA);
        var gameB = await fixture.SeedGameAsync(steamAppId: appIdB);
        var steamId = PlayerClient.UniqueSteamId();
        var steam = new FakeSteamHandler();
        // 两个 AppID 各自应答不同的时长：这样"每条反馈快照的是自己游戏的时长"才是被证明的，
        // 而不是只证明了 URL 里的 appid 看起来对。
        steam.SetPlaytimeResponse(appIdA, FakeSteamHandler.Playtime(111));
        steam.SetPlaytimeResponse(appIdB, FakeSteamHandler.Playtime(222));
        var client = fixture.CreateFactory(steam).CreateClient();

        AssertAuthorized(client, await PlayerClient.LoginAsync(client, steam, steamId, gameA.AppId!));
        var createdA = await client.PostAsJsonAsync(gameA.FeedbackPath,
            new Dictionary<string, object?> { ["type"] = "Bug", ["title"] = "A", ["content"] = "A" });
        Assert.Equal(HttpStatusCode.Created, createdA.StatusCode);

        AssertAuthorized(client, await PlayerClient.LoginAsync(client, steam, steamId, gameB.AppId!));
        var createdB = await client.PostAsJsonAsync(gameB.FeedbackPath,
            new Dictionary<string, object?> { ["type"] = "Bug", ["title"] = "B", ["content"] = "B" });
        Assert.Equal(HttpStatusCode.Created, createdB.StatusCode);

        var playtimeCalls = steam.RequestedPaths
            .Where(p => p.Contains("GetSingleGamePlaytime", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, playtimeCalls.Count);
        Assert.Single(playtimeCalls, p => p.Contains($"appid={appIdA}"));
        Assert.Single(playtimeCalls, p => p.Contains($"appid={appIdB}"));
        Assert.All(playtimeCalls, p => Assert.Contains($"steamid={steamId}", p));

        // 每个游戏各自快照到自己那份值。
        Assert.Equal(111, (await createdA.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("playtimeMinutes").GetInt32());
        Assert.Equal(222, (await createdB.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("playtimeMinutes").GetInt32());
    }

    /// <summary>Steam 给不出时长时只让该字段为 null，提交本身照常成功（绝不因此丢掉玩家写好的正文）。</summary>
    [Fact]
    public async Task Playtime_is_null_but_submission_succeeds_when_steam_fails()
    {
        var game = await fixture.SeedGameAsync();
        var steam = new FakeSteamHandler();
        steam.SetPlaytimeResponse(FakeSteamHandler.SteamServerError());
        var client = fixture.CreateFactory(steam).CreateClient();
        AssertAuthorized(client, await PlayerClient.LoginAsync(client, steam, PlayerClient.UniqueSteamId(), game.AppId!));

        var created = await client.PostAsJsonAsync(game.FeedbackPath,
            new Dictionary<string, object?> { ["type"] = "Bug", ["title"] = "t", ["content"] = "c" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("playtimeMinutes").ValueKind);
    }

    // ---------------------------------------------------------------- 限流按 (Game, SteamID) 分区

    /// <summary>
    /// 限流分区键是 "{appId}:{steamId}"，不是一个全局的 SteamID 额度：
    /// 同一个账号在一个游戏里把额度用光，绝不该连累它在另一个游戏里提交反馈或评论。
    /// </summary>
    [Fact]
    public async Task Rate_limits_are_partitioned_per_game_and_steam_id()
    {
        var gameA = await fixture.SeedGameAsync();
        var gameB = await fixture.SeedGameAsync();
        var steamId = PlayerClient.UniqueSteamId();
        var steam = new FakeSteamHandler();
        var factory = fixture.CreateFactory(steam, settings =>
        {
            settings["RateLimit:FeedbackPer10Minutes"] = "2";
            settings["RateLimit:CommentsPer10Minutes"] = "2";
        });
        var client = factory.CreateClient();
        var tokenA = await PlayerClient.LoginAsync(client, steam, steamId, gameA.AppId!);
        var tokenB = await PlayerClient.LoginAsync(client, steam, steamId, gameB.AppId!);

        static Dictionary<string, object?> Body(string title) =>
            new() { ["type"] = "Bug", ["title"] = title, ["content"] = "内容" };

        // 把 A 的提交额度用光。
        AssertAuthorized(client, tokenA);
        var feedbackA = 0;
        for (var i = 0; i < 2; i++)
        {
            var created = await client.PostAsJsonAsync(gameA.FeedbackPath, Body($"A{i}"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            feedbackA = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        }
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await client.PostAsJsonAsync(gameA.FeedbackPath, Body("A-over"))).StatusCode);

        // B 不受影响：同一个 SteamID、同一个工厂、同一个限流实例。
        AssertAuthorized(client, tokenB);
        var createdInB = await client.PostAsJsonAsync(gameB.FeedbackPath, Body("B0"));
        Assert.Equal(HttpStatusCode.Created, createdInB.StatusCode);
        var feedbackB = (await createdInB.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // 评论额度同样按 (Game, SteamID) 分区。
        AssertAuthorized(client, tokenA);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(
                HttpStatusCode.Created,
                (await client.PostAsJsonAsync(gameA.Api($"feedback/{feedbackA}/comments"), new { content = $"c{i}" })).StatusCode);
        }
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await client.PostAsJsonAsync(gameA.Api($"feedback/{feedbackA}/comments"), new { content = "c-over" })).StatusCode);

        AssertAuthorized(client, tokenB);
        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync(gameB.Api($"feedback/{feedbackB}/comments"), new { content = "B comment" })).StatusCode);
    }

    /// <summary>
    /// 同一个游戏里，额度也是按 SteamID 分开的：一个账号耗尽额度不会连累另一个账号。
    /// 提交与评论两条策略各自独立分区，都要覆盖。
    /// </summary>
    [Fact]
    public async Task Rate_limits_are_partitioned_per_steam_id()
    {
        var game = await fixture.SeedGameAsync();
        var steam = new FakeSteamHandler();
        var factory = fixture.CreateFactory(steam, settings =>
        {
            settings["RateLimit:FeedbackPer10Minutes"] = "2";
            settings["RateLimit:CommentsPer10Minutes"] = "2";
        });
        var client = factory.CreateClient();
        var tokenA = await PlayerClient.LoginAsync(client, steam, PlayerClient.UniqueSteamId(), game.AppId!);
        var tokenB = await PlayerClient.LoginAsync(client, steam, PlayerClient.UniqueSteamId(), game.AppId!);

        static Dictionary<string, object?> Body(string title) =>
            new() { ["type"] = "Bug", ["title"] = title, ["content"] = "内容" };

        // 把 A 的提交额度用光。
        AssertAuthorized(client, tokenA);
        var feedbackA = 0;
        for (var i = 0; i < 2; i++)
        {
            var created = await client.PostAsJsonAsync(game.FeedbackPath, Body($"A{i}"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            feedbackA = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        }
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await client.PostAsJsonAsync(game.FeedbackPath, Body("A-over"))).StatusCode);

        // B 是另一个账号，额度独立。
        AssertAuthorized(client, tokenB);
        var createdB = await client.PostAsJsonAsync(game.FeedbackPath, Body("B0"));
        Assert.Equal(HttpStatusCode.Created, createdB.StatusCode);
        var feedbackB = (await createdB.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // A 用光评论额度。
        AssertAuthorized(client, tokenA);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(
                HttpStatusCode.Created,
                (await client.PostAsJsonAsync(game.Api($"feedback/{feedbackA}/comments"), new { content = $"c{i}" })).StatusCode);
        }
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await client.PostAsJsonAsync(game.Api($"feedback/{feedbackA}/comments"), new { content = "c-over" })).StatusCode);

        // B 照常能评论。
        AssertAuthorized(client, tokenB);
        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync(game.Api($"feedback/{feedbackB}/comments"), new { content = "B comment" })).StatusCode);
    }

    // ---------------------------------------------------------------- 管理端模型

    /// <summary>同一个 SteamID 在两个游戏里是两行 Player，各自计数互不影响。</summary>
    [Fact]
    public async Task Game_admin_view_counts_players_and_feedback_per_game()
    {
        var gameA = await fixture.SeedGameAsync();
        var gameB = await fixture.SeedGameAsync();
        var steamId = PlayerClient.UniqueSteamId();
        var steam = new FakeSteamHandler();
        var factory = fixture.CreateFactory(steam);
        var client = factory.CreateClient();

        AssertAuthorized(client, await PlayerClient.LoginAsync(client, steam, steamId, gameA.AppId!));
        await client.PostAsJsonAsync(gameA.FeedbackPath,
            new Dictionary<string, object?> { ["type"] = "Bug", ["title"] = "A", ["content"] = "A" });
        AssertAuthorized(client, await PlayerClient.LoginAsync(client, steam, steamId, gameB.AppId!));

        var scope = factory.Services.CreateScope();
        var games = scope.ServiceProvider.GetRequiredService<GameAdminService>();
        var viewA = await games.GetAsync(gameA.Id, CancellationToken.None);
        var viewB = await games.GetAsync(gameB.Id, CancellationToken.None);

        Assert.NotNull(viewA);
        Assert.NotNull(viewB);
        Assert.Equal(1, viewA!.FeedbackCount);
        Assert.Equal(1, viewA.PlayerCount);
        Assert.Equal(0, viewB!.FeedbackCount);
        Assert.Equal(1, viewB.PlayerCount);
    }
}
