using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameFeedback.Tests.Infrastructure;

namespace GameFeedback.Tests;

/// <summary>
/// 寻址契约：玩家 API 的游戏由路径段 <c>/g/{appId}</c> 指定，而 appId 就是该游戏的 Steam AppID
/// （客户端本来就运行在某个 AppID 之下，所以接入时不需要手抄任何标识；自造的 slug 已从模型里删除）。
/// <para>
/// 这里覆盖四件事：
/// </para>
/// <list type="number">
/// <item>种子里的 AppID 决定可达性：命中即用，不命中即 404 game_not_found；</item>
/// <item>非数字 / 未知数字的路径段一律 404 game_not_found——绝不是 500，也不是被状态码页换掉的 HTML；</item>
/// <item>
/// 升级迁移为存量数据建的那行占位游戏（SteamAppId 为 NULL）不可寻址，
/// 也不会因为它的存在而让别的路径意外解析成功；
/// </item>
/// <item>game_not_found 的 detail 点名请求的 AppID，但不泄漏本实例已配置的游戏列表或任何凭据。</item>
/// </list>
/// </summary>
[Collection("Integration")]
public sealed class AppIdAddressingTests(IntegrationTestFixture fixture)
{
    // ---------------------------------------------------------------- 按 AppID 寻址

    /// <summary>种下一个带 AppID 的游戏，就该能在这个 AppID 下登录；打给 Steam 的也是同一个 AppID。</summary>
    [Fact]
    public async Task Game_is_reachable_at_its_own_app_id()
    {
        var game = await fixture.SeedGameAsync();
        var steam = new FakeSteamHandler();
        var client = fixture.CreateFactory(steam).CreateClient();
        var steamId = PlayerClient.UniqueSteamId();
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketOk(steamId));
        steam.SetProfileResponse(FakeSteamHandler.Profile("AppIdPlayer", null));

        var login = await client.PostAsJsonAsync(game.AuthPath, new { ticket = "ticket" });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var ticketCall = Assert.Single(
            steam.RequestedPaths, p => p.Contains("AuthenticateUserTicket", StringComparison.Ordinal));
        Assert.Contains($"appid={game.AppId}", ticketCall);
    }

    /// <summary>另一个（合法但没种下的）AppID 必须 404：可达性只由 games.SteamAppId 决定，没有任何兜底。</summary>
    [Fact]
    public async Task A_different_app_id_answers_404_game_not_found()
    {
        await fixture.SeedGameAsync();
        var missing = TestGames.UniqueAppId();
        // 独立的工厂：auth 端点按 IP 限流（默认 10/分钟），撞上共享夹具的额度会让本用例
        // 变成在测限流而不是在测寻址。
        var client = fixture.CreateFactory().CreateClient();

        var response = await client.PostAsJsonAsync(
            TestGame.PathForAppId(missing, "auth/steam"), new { ticket = "t" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("game_not_found", await TestGames.ReadProblemCodeAsync(response));
    }

    // ---------------------------------------------------------------- 非数字 / 未知路径段

    /// <summary>
    /// 路径段不是数字时解析必然落空。这里同时断言响应仍是 ProblemDetails
    /// （<c>application/problem+json</c>）——玩家 API 的 4xx 不该被状态码页换成 HTML，
    /// 更不该变成 500。
    /// </summary>
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("480abc")]
    [InlineData("abc480")]
    [InlineData("-1")]
    [InlineData("48.0")]
    public async Task Non_numeric_app_id_segment_answers_404_game_not_found(string segment)
    {
        var client = fixture.CreateFactory().CreateClient();

        var response = await client.PostAsJsonAsync(
            TestGame.PathForAppId(segment, "auth/steam"), new { ticket = "t" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("game_not_found", await TestGames.ReadProblemCodeAsync(response));
    }

    /// <summary>数字但没种下的 AppID 同样 404（包括 0 与超过 SteamAppId 长度上限的值）。</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("000")]
    [InlineData("12345678901")]
    public async Task Unknown_numeric_app_id_answers_404_game_not_found(string segment)
    {
        var client = fixture.CreateFactory().CreateClient();

        var response = await client.PostAsJsonAsync(
            TestGame.PathForAppId(segment, "auth/steam"), new { ticket = "t" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("game_not_found", await TestGames.ReadProblemCodeAsync(response));
    }

    // ---------------------------------------------------------------- NULL AppID 的占位游戏

    /// <summary>
    /// SteamAppId 为 NULL 的占位行不可寻址：任何路径都解析不到它，包括用它的数据库 Id 当路径段。
    /// 它也不会遮蔽真正的游戏——真游戏照常在**自己的** AppID 下工作。
    /// </summary>
    [Fact]
    public async Task Placeholder_game_without_an_app_id_is_unaddressable_and_shadows_nothing()
    {
        var real = await fixture.SeedGameAsync();
        var placeholder = await fixture.SeedGameAsync(withAppId: false);
        Assert.Null(placeholder.AppId);

        var steam = new FakeSteamHandler();
        var client = fixture.CreateFactory(steam).CreateClient();

        var candidates = new[] { placeholder.Id.ToString(CultureInfo.InvariantCulture), "0" };
        foreach (var segment in candidates)
        {
            var response = await client.PostAsJsonAsync(
                TestGame.PathForAppId(segment, "auth/steam"), new { ticket = "t" });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("game_not_found", await TestGames.ReadProblemCodeAsync(response));
        }

        // 真游戏照常登录，且没有多余的 Steam 调用被打出去（占位行没有参与解析）。
        var steamId = PlayerClient.UniqueSteamId();
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketOk(steamId));
        steam.SetProfileResponse(FakeSteamHandler.Profile("Real", null));
        var login = await client.PostAsJsonAsync(real.AuthPath, new { ticket = "ticket" });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Single(steam.RequestedPaths, p => p.Contains("AuthenticateUserTicket", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 404 的文案与不泄漏

    /// <summary>
    /// game_not_found 的 detail 必须点名**请求的那个** AppID（客户端据此自查 BaseUrl 与配置），
    /// 但绝不能顺手列出本实例已配置的游戏（名字、AppID）——那等于把"这台服务器上有哪些游戏"
    /// 送给任何会扫描路径的人。凭据当然更不该出现。
    /// </summary>
    [Fact]
    public async Task Game_not_found_detail_names_the_requested_app_id_without_leaking_configured_games()
    {
        var apiKey = $"plain-key-{Guid.NewGuid():N}";
        var gameName = $"已配置的游戏-{Guid.NewGuid():N}"[..18];
        var configured = await fixture.SeedGameAsync(name: gameName, apiKey: apiKey);
        var missing = TestGames.UniqueAppId();
        var client = fixture.CreateFactory().CreateClient();

        var response = await client.PostAsJsonAsync(
            TestGame.PathForAppId(missing, "auth/steam"), new { ticket = "t" });
        // 只读一次响应体：TestServer 的 Content 流读过就关了，拿它再解析一次会炸。
        var raw = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(raw);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "game_not_found", problem.RootElement.GetProperty("code").GetString());
        var detail = problem.RootElement.GetProperty("detail").GetString();
        Assert.NotNull(detail);
        Assert.Contains(missing, detail!);
        Assert.DoesNotContain(configured.AppId!, raw);
        Assert.DoesNotContain(gameName, raw);
        Assert.DoesNotContain(apiKey, raw);
    }
}
