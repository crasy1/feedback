using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameFeedback.Tests.Infrastructure;

namespace GameFeedback.Tests;

/// <summary>
/// Steam 验票调试开关（Steam:DebugSkipTicketValidation）：
/// Development 下可跳过验票、以请求声明的 SteamID64 登录；
/// 生产环境配置该开关必须拒绝启动（fail closed）。
/// </summary>
[Collection("Integration")]
public sealed class DebugLoginTests(IntegrationTestFixture fixture)
{
    private TestGame Game => fixture.DefaultGame;

    private static Action<Dictionary<string, string>> DevSettings() => settings =>
    {
        settings["environment"] = "Development";
        settings["Steam:DebugSkipTicketValidation"] = "true";
    };

    [Fact]
    public async Task Debug_steam_id_logs_in_without_calling_steam_ticket_api()
    {
        var steam = new FakeSteamHandler();
        // 只打桩玩家资料；票据接口一旦被调用（未打桩）会直接抛错，等于断言验票被跳过。
        steam.SetProfileResponse(FakeSteamHandler.Profile("Debugger", "https://cdn.example/debug.png"));
        var factory = fixture.CreateFactory(steam, extraSettings: DevSettings());
        var client = factory.CreateClient();

        var steamId = PlayerClient.UniqueSteamId();
        var response = await client.PostAsJsonAsync(Game.AuthPath, new { debugSteamId = steamId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(steamId, body.GetProperty("player").GetProperty("steamId").GetString());
        Assert.DoesNotContain(steam.RequestedPaths, p => p.Contains("AuthenticateUserTicket"));
    }

    /// <summary>
    /// 调试登录不需要该游戏配好凭据：验票本来就被跳过了。
    /// 但游戏仍然必须**可寻址**（有 AppID）且在启用状态——这条路径绕过的只是 Steam，不是寻址。
    /// </summary>
    [Fact]
    public async Task Debug_login_works_on_a_game_without_a_credential()
    {
        var unconfigured = await fixture.SeedGameAsync(withCredential: false);
        var steam = new FakeSteamHandler();
        var factory = fixture.CreateFactory(steam, extraSettings: DevSettings());

        var response = await factory.CreateClient()
            .PostAsJsonAsync(unconfigured.AuthPath, new { debugSteamId = PlayerClient.UniqueSteamId() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(steam.RequestedPaths); // 连资料都不查：没有可用凭据。
    }

    /// <summary>
    /// 升级迁移建的那行占位游戏（SteamAppId 为 NULL）不可寻址：即使打开调试开关也解析不到它，
    /// 因为解析发生在验票开关之前。用它的数据库 Id 拼路径也一样。
    /// </summary>
    [Fact]
    public async Task Debug_login_cannot_reach_a_game_without_an_app_id()
    {
        var placeholder = await fixture.SeedGameAsync(withAppId: false);
        var factory = fixture.CreateFactory(extraSettings: DevSettings());
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            TestGame.PathForAppId(placeholder.Id.ToString(), "auth/steam"),
            new { debugSteamId = PlayerClient.UniqueSteamId() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("game_not_found", await TestGames.ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task Debug_token_can_call_player_api()
    {
        var steam = new FakeSteamHandler();
        steam.SetProfileResponse(FakeSteamHandler.Profile("Debugger", null));
        var factory = fixture.CreateFactory(steam, extraSettings: DevSettings());
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync(Game.AuthPath, new { debugSteamId = PlayerClient.UniqueSteamId() });
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("accessToken").GetString();

        var mine = new System.Net.Http.HttpRequestMessage(HttpMethod.Get, Game.MinePath);
        mine.Headers.Authorization = new("Bearer", token);
        var response = await client.SendAsync(mine);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_debug_steam_id_is_rejected_with_400()
    {
        var factory = fixture.CreateFactory(extraSettings: DevSettings());
        var client = factory.CreateClient();

        foreach (var bad in new[] { "123", "7656119800000000x", "z" })
        {
            var response = await client.PostAsJsonAsync(Game.AuthPath, new { debugSteamId = bad });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Normal_ticket_flow_still_works_when_debug_flag_enabled()
    {
        var steam = new FakeSteamHandler();
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketOk("76561198000000077"));
        steam.SetProfileResponse(FakeSteamHandler.Profile("Real", "https://cdn.example/real.png"));
        var factory = fixture.CreateFactory(steam, extraSettings: DevSettings());
        var client = factory.CreateClient();

        // 开关开启但请求未带 debugSteamId 时，必须仍走真实验票流程。
        var response = await client.PostAsJsonAsync(Game.AuthPath, new { ticket = "ticket" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(steam.RequestedPaths, p => p.Contains("AuthenticateUserTicket"));
    }

    [Fact]
    public void Production_with_debug_flag_fails_startup()
    {
        // fail closed：生产配置了跳过验票的开关，应用必须拒绝启动。
        var factory = fixture.CreateFactory(extraSettings: settings =>
        {
            settings["environment"] = "Production";
            settings["Steam:DebugSkipTicketValidation"] = "true";
        });

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }
}
