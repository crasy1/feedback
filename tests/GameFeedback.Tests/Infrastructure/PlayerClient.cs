using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GameFeedback.Tests.Infrastructure;
namespace GameFeedback.Tests.Infrastructure;

/// <summary>测试中模拟游戏客户端完成 Steam 登录并取得带 Bearer 令牌的 HttpClient。</summary>
public static class PlayerClient
{
    /// <summary>生成 17 位纯数字的 SteamID64 形状唯一值，避免共享数据库串扰。</summary>
    public static string UniqueSteamId() =>
        "7656" + new string(Guid.NewGuid().ToByteArray().Select(b => (char)('0' + b % 10)).Take(13).ToArray());
    public static async Task<string> LoginAsync(HttpClient client, FakeSteamHandler steam, string steamId)
    {
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketOk(steamId));
        steam.SetProfileResponse(FakeSteamHandler.Profile($"Player-{steamId}", $"https://cdn.example/{steamId}.png"));

        var response = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "ticket" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()
            ?? throw new InvalidOperationException("登录响应缺少 accessToken");
    }

    /// <summary>创建带独立 Steam 桩的工厂，并返回已登录指定 SteamID 的客户端。</summary>
    public static async Task<(GameFeedbackApplicationFactory Factory, HttpClient Client, FakeSteamHandler Steam)> CreateAsync(
        IntegrationTestFixture fixture, string steamId)
    {
        var steam = new FakeSteamHandler();
        var factory = fixture.CreateFactory(steam);
        var client = factory.CreateClient();
        var token = await LoginAsync(client, steam, steamId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (factory, client, steam);
    }
}
