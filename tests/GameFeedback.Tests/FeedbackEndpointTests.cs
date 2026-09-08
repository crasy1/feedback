using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameFeedback.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GameFeedback.Tests;

[Collection("Integration")]
public sealed class FeedbackEndpointTests(IntegrationTestFixture fixture)
{
    private static object ValidBody(string title = "崩溃", string content = "进地图必崩", string? steamIdBogus = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["type"] = "Bug",
            ["title"] = title,
            ["content"] = content,
            ["gameVersion"] = "1.2.3",
            ["buildNumber"] = "456",
            ["operatingSystem"] = "Windows 11",
            ["gpu"] = "RTX 4070",
            ["locale"] = "zh-CN",
            ["map"] = "arena_01",
            ["character"] = "mage",
        };
        if (steamIdBogus is not null)
        {
            body["steamId"] = steamIdBogus; // 请求体中的 SteamID 必须被忽略
        }
        return body;
    }

    private static async Task<JsonElement> PostFeedbackAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/feedback", body);
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(content);
    }

    [Fact]
    public async Task Create_feedback_ignores_body_steam_id_and_uses_principal()
    {
        var (_, client, _) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());

        var response = await client.PostAsJsonAsync("/api/feedback", ValidBody(steamIdBogus: "76561198000000099"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("id").GetInt32() > 0);
        Assert.Equal("Bug", body.GetProperty("type").GetString());
        Assert.Equal("Open", body.GetProperty("status").GetString());

        var mine = await client.GetFromJsonAsync<JsonElement>("/api/feedback/mine");
        Assert.Equal(1, mine.GetArrayLength());
        Assert.Equal(body.GetProperty("id").GetInt32(), mine[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Create_rejects_oversized_fields_and_invalid_type()
    {
        var (_, client, _) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());

        var oversizedTitle = await client.PostAsJsonAsync("/api/feedback",
            ValidBody(title: new string('x', 201)));
        Assert.Equal(HttpStatusCode.BadRequest, oversizedTitle.StatusCode);

        var oversizedContent = await client.PostAsJsonAsync("/api/feedback",
            ValidBody(content: new string('x', 10_001)));
        Assert.Equal(HttpStatusCode.BadRequest, oversizedContent.StatusCode);

        var invalidType = await PostFeedbackAsync(client, new Dictionary<string, object?>
        {
            ["type"] = "Complaint", ["title"] = "t", ["content"] = "c",
        });
        Assert.Contains("type", invalidType.GetProperty("detail").GetString());

        var missingTitle = await client.PostAsync("/api/feedback",
            JsonContent.Create(new Dictionary<string, object?>
            {
                ["type"] = "Bug", ["title"] = "", ["content"] = "c",
            }));
        Assert.Equal(HttpStatusCode.BadRequest, missingTitle.StatusCode);
    }

    [Theory]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("Bug, Suggestion")]
    [InlineData("Bug, Bug")]
    public async Task Create_rejects_invalid_type_without_storing_feedback(string type)
    {
        var (_, client, _) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());

        var response = await client.PostAsJsonAsync("/api/feedback", new { type, title = "t", content = "c" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Contains("type", problem.GetProperty("detail").GetString());

        var mine = await client.GetFromJsonAsync<JsonElement>("/api/feedback/mine");
        Assert.Equal(0, mine.GetArrayLength());
    }

    [Theory]
    [InlineData("bug", "Bug")]
    [InlineData("sUGGESTION", "Suggestion")]
    [InlineData("oTHER", "Other")]
    public async Task Create_accepts_type_names_case_insensitively(string type, string expectedType)
    {
        var (_, client, _) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());

        var response = await client.PostAsJsonAsync("/api/feedback", new { type, title = "t", content = "c" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedType, body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Mine_returns_only_own_items_newest_first()
    {
        var steamA = new FakeSteamHandler();
        var factoryA = fixture.CreateFactory(steamA);
        var clientA = factoryA.CreateClient();
        var playerA = PlayerClient.UniqueSteamId();
        var playerB = PlayerClient.UniqueSteamId();
        var tokenA = await PlayerClient.LoginAsync(clientA, steamA, playerA);
        clientA.DefaultRequestHeaders.Authorization = new("Bearer", tokenA);

        var (_, clientB, _) = await PlayerClient.CreateAsync(fixture, playerB);

        foreach (var i in Enumerable.Range(1, 3))
        {
            var created = await clientA.PostAsJsonAsync("/api/feedback", ValidBody(title: $"A{i}"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            await Task.Delay(5); // 确保 CreatedAt 严格递增
        }
        var createdB = await clientB.PostAsJsonAsync("/api/feedback", ValidBody(title: "B1"));
        Assert.Equal(HttpStatusCode.Created, createdB.StatusCode);

        var mineA = await clientA.GetFromJsonAsync<JsonElement>("/api/feedback/mine");
        Assert.Equal(3, mineA.GetArrayLength());
        Assert.Equal("A3", mineA[0].GetProperty("title").GetString());
        Assert.Equal("A1", mineA[2].GetProperty("title").GetString());

        var mineB = await clientB.GetFromJsonAsync<JsonElement>("/api/feedback/mine");
        Assert.Equal(1, mineB.GetArrayLength());
        Assert.Equal("B1", mineB[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Mine_caps_at_latest_100()
    {
        var steam = new FakeSteamHandler();
        var factory = fixture.CreateFactory(steam, settings =>
            settings["RateLimit:FeedbackPer10Minutes"] = "500");
        var client = factory.CreateClient();
        var token = await PlayerClient.LoginAsync(client, steam, PlayerClient.UniqueSteamId());
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        foreach (var i in Enumerable.Range(1, 105))
        {
            var created = await client.PostAsJsonAsync("/api/feedback",
                new Dictionary<string, object?> { ["type"] = "Other", ["title"] = $"t{i}", ["content"] = "c" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var mine = await client.GetFromJsonAsync<JsonElement>("/api/feedback/mine");
        Assert.Equal(100, mine.GetArrayLength());
        Assert.Equal("t105", mine[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Create_is_rate_limited_per_player()
    {
        var (_, client, _) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());

        for (var i = 0; i < 5; i++)
        {
            var created = await client.PostAsJsonAsync("/api/feedback",
                new Dictionary<string, object?> { ["type"] = "Bug", ["title"] = $"t{i}", ["content"] = "c" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        var limited = await client.PostAsJsonAsync("/api/feedback",
            new Dictionary<string, object?> { ["type"] = "Bug", ["title"] = "t6", ["content"] = "c" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Feedback_endpoints_require_authentication()
    {
        var client = fixture.DefaultFactory.CreateClient();

        var created = await client.PostAsJsonAsync("/api/feedback", ValidBody());
        Assert.Equal(HttpStatusCode.Unauthorized, created.StatusCode);

        var mine = await client.GetAsync("/api/feedback/mine");
        Assert.Equal(HttpStatusCode.Unauthorized, mine.StatusCode);
    }
}
