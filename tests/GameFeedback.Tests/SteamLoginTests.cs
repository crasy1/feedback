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
        const string steamId = "76561198000000001";
        var (factory, steam) = CreateSteamFactory();
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketOk(steamId));
        steam.SetProfileResponse(FakeSteamHandler.Profile("Gordon", "https://cdn.example/avatar.png"));

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "valid-ticket-hex" });

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = body.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrEmpty(accessToken));
        Assert.Equal(steamId, PayloadSub(accessToken!));
        Assert.Equal(steamId, body.GetProperty("player").GetProperty("steamId").GetString());
        Assert.Equal("Gordon", body.GetProperty("player").GetProperty("steamName").GetString());
    }

    [Fact]
    public async Task Invalid_ticket_is_rejected_and_creates_no_player()
    {
        var (factory, steam) = CreateSteamFactory();
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketRejected());

        var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var playersBeforeLogin = await db.Players.CountAsync();
        var response = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "bad-ticket" });
        var debugBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Unauthorized, $"status={response.StatusCode} body={debugBody}");
        Assert.Equal(playersBeforeLogin, await db.Players.CountAsync());
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

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"response\":null}")]
    [InlineData("{\"response\":[]}")]
    [InlineData("{\"response\":{\"params\":null}}")]
    [InlineData("{\"response\":{\"params\":[]}}")]
    [InlineData("{\"response\":{\"params\":{\"result\":true,\"steamid\":\"76561198000000002\"}}}")]
    [InlineData("{\"response\":{\"params\":{\"result\":null,\"steamid\":\"76561198000000002\"}}}")]
    [InlineData("{\"response\":{\"params\":{\"result\":\"OK\",\"steamid\":76561198000000002}}}")]
    [InlineData("{\"response\":{\"params\":{\"result\":\"OK\",\"steamid\":[]}}}")]
    public async Task Wrong_steam_ticket_payload_shape_fails_closed(string payload)
    {
        var (factory, steam) = CreateSteamFactory();
        steam.EnqueueTicketResponse(FakeSteamHandler.Payload(payload));
        var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var playersBeforeLogin = await db.Players.CountAsync();

        var response = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "ticket" });

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(playersBeforeLogin, await db.Players.CountAsync());
        Assert.Single(steam.RequestedPaths);
    }

    [Theory]
    [InlineData(1, "null")]
    [InlineData(2, "[]")]
    [InlineData(3, "{\"response\":null}")]
    [InlineData(4, "{\"response\":[]}")]
    [InlineData(5, "{\"response\":{\"players\":null}}")]
    [InlineData(6, "{\"response\":{\"players\":{}}}")]
    [InlineData(7, "{\"response\":{\"players\":[null]}}")]
    [InlineData(8, "{\"response\":{\"players\":[[]]}}")]
    [InlineData(9, "{\"response\":{\"players\":[{\"personaname\":123,\"avatarfull\":\"https://cdn.example/new.png\"}]}}")]
    [InlineData(10, "{\"response\":{\"players\":[{\"personaname\":null,\"avatarfull\":\"https://cdn.example/new.png\"}]}}")]
    [InlineData(11, "{\"response\":{\"players\":[{\"personaname\":\"New name\",\"avatarfull\":false}]}}")]
    [InlineData(12, "{not-json")]
    public async Task Wrong_profile_payload_keeps_existing_profile_and_allows_login(int caseId, string payload)
    {
        var steamId = $"765611980001{caseId:D5}";
        var (factory, steam) = CreateSteamFactory();
        steam.EnqueueTicketResponses(FakeSteamHandler.TicketOk(steamId), 2);
        steam.SetProfileResponse(FakeSteamHandler.Profile("Stored name", "https://cdn.example/stored.png"));
        var client = factory.CreateClient();
        var firstLogin = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "first-ticket" });
        Assert.Equal(System.Net.HttpStatusCode.OK, firstLogin.StatusCode);
        steam.SetProfileResponse(FakeSteamHandler.Payload(payload));

        var response = await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "second-ticket" });

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(steamId, PayloadSub(body.GetProperty("accessToken").GetString()!));
        Assert.Equal("Stored name", body.GetProperty("player").GetProperty("steamName").GetString());
        Assert.Equal("https://cdn.example/stored.png", body.GetProperty("player").GetProperty("avatarUrl").GetString());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var player = await db.Players.SingleAsync(p => p.SteamId == steamId);
        Assert.Equal("Stored name", player.SteamName);
        Assert.Equal("https://cdn.example/stored.png", player.AvatarUrl);
    }

    [Fact]
    public async Task Profile_fetch_failure_does_not_fail_login()
    {
        var (factory, steam) = CreateSteamFactory();
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketOk("76561198000000003"));
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
        const string steamId = "76561198000000004";
        var (factory, steam) = CreateSteamFactory();
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketOk(steamId));
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketOk(steamId));
        steam.SetProfileResponse(FakeSteamHandler.Profile("Renamed", "https://cdn.example/new.png"));

        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "t1" });
        await client.PostAsJsonAsync("/api/auth/steam", new { ticket = "t2" });

        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var players = await db.Players.Where(p => p.SteamId == steamId).ToListAsync();
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
