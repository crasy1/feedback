using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameFeedback.Data;
using GameFeedback.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameFeedback.Tests;

[Collection("Integration")]
public sealed class SteamLoginTests(IntegrationTestFixture fixture)
{
    private const string ValidSteamId = "76561198000000001";

    private (GameFeedbackApplicationFactory Factory, FakeSteamHandler Steam) CreateSteamFactory()
    {
        var steam = new FakeSteamHandler();
        return (fixture.CreateFactory(steam), steam);
    }

    private static async Task<JsonElement> PostLoginAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/auth/steam", body);
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(content);
    }

    private static string? PayloadSub(string accessToken)
    {
        // 解码 JWT 载荷（测试专有），校验对外契约：sub = SteamID64。
        var parts = accessToken.Split('.');
        Assert.Equal(3, parts.Length);
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2: payload += "=="; break;
            case 3: payload += "="; break;
        }
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        return JsonDocument.Parse(json).RootElement.TryGetProperty("sub", out var sub) ? sub.GetString() : null;
    }

    [Fact]
    public async Task Valid_ticket_returns_token_and_creates_player()
    {
        var (factory, steam) = CreateSteamFactory();
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketOk(ValidSteamId));
        steam.SetProfileResponse(FakeSteamHandler.Profile("Gordon", "https://cdn.example/avatar.png"));

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "valid-ticket-hex" });

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = body.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrEmpty(accessToken));
        Assert.Equal(ValidSteamId, PayloadSub(accessToken!));
        Assert.Equal(ValidSteamId, body.GetProperty("player").GetProperty("steamId").GetString());
        Assert.Equal("Gordon", body.GetProperty("player").GetProperty("steamName").GetString());
    }

    [Fact]
    public async Task Invalid_ticket_is_rejected_and_creates_no_player()
    {
        var (factory, steam) = CreateSteamFactory();
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketRejected());

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "bad-ticket" });
        var debugBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Unauthorized, $"status={response.StatusCode} body={debugBody}");
        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Players.AnyAsync(p => p.SteamId == ValidSteamId));
    }

    [Fact]
    public async Task Steam_response_without_steam_id_fails_closed()
    {
        var (factory, steam) = CreateSteamFactory();
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketMissingSteamId());

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "ticket" });

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Steam_api_failure_fails_closed()
    {
        var (factory, steam) = CreateSteamFactory();
        steam.EnqueueTicketResponse(FakeSteamHandler.SteamServerError());

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "ticket" });

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_steam_payload_fails_closed()
    {
        var (factory, steam) = CreateSteamFactory();
        steam.EnqueueTicketResponse(FakeSteamHandler.MalformedJson());

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "ticket" });

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Profile_fetch_failure_does_not_fail_login()
    {
        var (factory, steam) = CreateSteamFactory();
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketOk(ValidSteamId));
        steam.SetProfileResponse(FakeSteamHandler.SteamServerError());

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "ticket" });

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("player").GetProperty("steamName").ValueKind is JsonValueKind.Null or JsonValueKind.String);
    }

    [Fact]
    public async Task Second_login_updates_existing_player_without_duplicate()
    {
        var (factory, steam) = CreateSteamFactory();
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketOk(ValidSteamId));
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketOk(ValidSteamId));
        steam.SetProfileResponse(FakeSteamHandler.Profile("Renamed", "https://cdn.example/new.png"));

        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "t1" });
        await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "t2" });

        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var players = await db.Players.Where(p => p.SteamId == ValidSteamId).ToListAsync();
        var player = Assert.Single(players);
        Assert.Equal("Renamed", player.SteamName);
        Assert.Equal("https://cdn.example/new.png", player.AvatarUrl);
        Assert.NotNull(player.LastLoginAt);
    }

    [Fact]
    public async Task Auth_endpoint_is_rate_limited_per_ip()
    {
        var (factory, steam) = CreateSteamFactory();
        steam.EnqueueTicketResponses(FakeSteamHandler.TicketRejected(), 12);

        var client = factory.CreateClient();
        // 前 10 次请求正常处理（票据被拒 → 401），第 11 次被限流 → 429。
        for (var i = 0; i < 10; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = $"t{i}" });
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var limited = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "t11" });
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Missing_or_oversized_ticket_returns_400()
    {
        var (factory, steam) = CreateSteamFactory();
        var client = factory.CreateClient();

        var missing = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, missing.StatusCode);

        var oversized = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = new string('x', 5000) });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, oversized.StatusCode);

        Assert.Empty(steam.RequestedPaths);
    }
}
