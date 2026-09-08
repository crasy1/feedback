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
        var response = await client.PostAsJsonAsync("/api/auth/steam", new { debugSteamId = steamId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(steamId, body.GetProperty("player").GetProperty("steamId").GetString());
        Assert.DoesNotContain(steam.RequestedPaths, p => p.Contains("AuthenticateUserTicket"));
    }

    [Fact]
    public async Task Debug_token_can_call_player_api()
    {
        var steam = new FakeSteamHandler();
        steam.SetProfileResponse(FakeSteamHandler.Profile("Debugger", null));
        var factory = fixture.CreateFactory(steam, extraSettings: DevSettings());
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/steam", new { debugSteamId = PlayerClient.UniqueSteamId() });
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("accessToken").GetString();

        var mine = new System.Net.Http.HttpRequestMessage(HttpMethod.Get, "/api/feedback/mine");
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
            var response = await client.PostAsJsonAsync("/api/auth/steam", new { debugSteamId = bad });
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
        var response = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "ticket" });

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
