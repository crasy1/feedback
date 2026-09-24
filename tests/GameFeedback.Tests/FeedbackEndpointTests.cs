using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameFeedback.Services;
using GameFeedback.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GameFeedback.Tests;

[Collection("Integration")]
public sealed class FeedbackEndpointTests(IntegrationTestFixture fixture)
{
    private TestGame Game => fixture.DefaultGame;

    private static Dictionary<string, object?> ValidBody(string title = "崩溃", string content = "进地图必崩", string? steamIdBogus = null)
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

    private async Task<JsonElement> PostFeedbackAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync(Game.FeedbackPath, body);
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(content);
    }

    [Fact]
    public async Task Create_feedback_ignores_body_steam_id_and_uses_principal()
    {
        var (_, client, _) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());

        var response = await client.PostAsJsonAsync(Game.FeedbackPath, ValidBody(steamIdBogus: "76561198000000099"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("id").GetInt32() > 0);
        Assert.Equal("Bug", body.GetProperty("type").GetString());
        Assert.Equal("Open", body.GetProperty("status").GetString());

        var mine = await client.GetFromJsonAsync<JsonElement>(Game.MinePath);
        Assert.Equal(1, mine.GetArrayLength());
        Assert.Equal(body.GetProperty("id").GetInt32(), mine[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Create_rejects_oversized_fields_and_invalid_type()
    {
        var (_, client, _) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());

        var oversizedTitle = await client.PostAsJsonAsync(Game.FeedbackPath,
            ValidBody(title: new string('x', 201)));
        Assert.Equal(HttpStatusCode.BadRequest, oversizedTitle.StatusCode);

        var oversizedContent = await client.PostAsJsonAsync(Game.FeedbackPath,
            ValidBody(content: new string('x', 10_001)));
        Assert.Equal(HttpStatusCode.BadRequest, oversizedContent.StatusCode);

        var invalidType = await PostFeedbackAsync(client, new Dictionary<string, object?>
        {
            ["type"] = "Complaint", ["title"] = "t", ["content"] = "c",
        });
        Assert.Contains("type", invalidType.GetProperty("detail").GetString());

        var missingTitle = await client.PostAsync(Game.FeedbackPath,
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

        var response = await client.PostAsJsonAsync(Game.FeedbackPath, new { type, title = "t", content = "c" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Contains("type", problem.GetProperty("detail").GetString());

        var mine = await client.GetFromJsonAsync<JsonElement>(Game.MinePath);
        Assert.Equal(0, mine.GetArrayLength());
    }

    [Theory]
    [InlineData("bug", "Bug")]
    [InlineData("sUGGESTION", "Suggestion")]
    [InlineData("oTHER", "Other")]
    public async Task Create_accepts_type_names_case_insensitively(string type, string expectedType)
    {
        var (_, client, _) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());

        var response = await client.PostAsJsonAsync(Game.FeedbackPath, new { type, title = "t", content = "c" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedType, body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Mine_returns_only_own_items_newest_first()
    {
        // 两个玩家必须落在同一个游戏里，否则这条测试测的是"不同游戏"而不是"不同玩家"。
        var steamA = new FakeSteamHandler();
        var factoryA = fixture.CreateFactory(steamA);
        var clientA = factoryA.CreateClient();
        var playerA = PlayerClient.UniqueSteamId();
        var playerB = PlayerClient.UniqueSteamId();
        var tokenA = await PlayerClient.LoginAsync(clientA, steamA, playerA, Game.AppId!);
        clientA.DefaultRequestHeaders.Authorization = new("Bearer", tokenA);

        var (_, clientB, _) = await PlayerClient.CreateAsync(fixture, playerB);

        foreach (var i in Enumerable.Range(1, 3))
        {
            var created = await clientA.PostAsJsonAsync(Game.FeedbackPath, ValidBody(title: $"A{i}"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            await Task.Delay(5); // 确保 CreatedAt 严格递增
        }
        var createdB = await clientB.PostAsJsonAsync(Game.FeedbackPath, ValidBody(title: "B1"));
        Assert.Equal(HttpStatusCode.Created, createdB.StatusCode);

        var mineA = await clientA.GetFromJsonAsync<JsonElement>(Game.MinePath);
        Assert.Equal(3, mineA.GetArrayLength());
        Assert.Equal("A3", mineA[0].GetProperty("title").GetString());
        Assert.Equal("A1", mineA[2].GetProperty("title").GetString());

        var mineB = await clientB.GetFromJsonAsync<JsonElement>(Game.MinePath);
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
        var token = await PlayerClient.LoginAsync(client, steam, PlayerClient.UniqueSteamId(), Game.AppId!);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        foreach (var i in Enumerable.Range(1, 105))
        {
            var created = await client.PostAsJsonAsync(Game.FeedbackPath,
                new Dictionary<string, object?> { ["type"] = "Other", ["title"] = $"t{i}", ["content"] = "c" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var mine = await client.GetFromJsonAsync<JsonElement>(Game.MinePath);
        Assert.Equal(100, mine.GetArrayLength());
        Assert.Equal("t105", mine[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Create_is_rate_limited_per_player()
    {
        var (_, client, _) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());

        for (var i = 0; i < 5; i++)
        {
            var created = await client.PostAsJsonAsync(Game.FeedbackPath,
                new Dictionary<string, object?> { ["type"] = "Bug", ["title"] = $"t{i}", ["content"] = "c" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        var limited = await client.PostAsJsonAsync(Game.FeedbackPath,
            new Dictionary<string, object?> { ["type"] = "Bug", ["title"] = "t6", ["content"] = "c" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Create_stores_auto_collected_environment_fields()
    {
        var (_, client, _) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());

        var body = ValidBody();
        body["cpu"] = "Intel(R) Core(TM) i7-6700K CPU @ 4.00GHz";
        body["memoryTotalMb"] = 16384;

        var created = await client.PostAsJsonAsync(Game.FeedbackPath, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Intel(R) Core(TM) i7-6700K CPU @ 4.00GHz", createdBody.GetProperty("cpu").GetString());
        Assert.Equal(16384, createdBody.GetProperty("memoryTotalMb").GetInt32());

        var id = createdBody.GetProperty("id").GetInt32();
        var detail = await client.GetFromJsonAsync<JsonElement>(Game.Api($"feedback/{id}"));
        Assert.Equal("Intel(R) Core(TM) i7-6700K CPU @ 4.00GHz", detail.GetProperty("cpu").GetString());
        Assert.Equal(16384, detail.GetProperty("memoryTotalMb").GetInt32());
    }

    /// <summary>
    /// 自动采集字段越界时只丢弃、不报错：玩家既没有输入它们，也无法修正它们，
    /// 400 只会让他白写一遍正文。手填元数据（title/content 等）的 400 行为不变。
    /// </summary>
    [Fact]
    public async Task Create_drops_overlong_cpu_without_rejecting()
    {
        var (_, client, _) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());

        var body = ValidBody();
        body["cpu"] = new string('x', FeedbackService.CpuMaxLength + 1);

        var created = await client.PostAsJsonAsync(Game.FeedbackPath, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, createdBody.GetProperty("cpu").ValueKind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4_194_305)]
    public async Task Create_drops_out_of_range_memory_without_rejecting(int memoryTotalMb)
    {
        var (_, client, _) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());

        var body = ValidBody();
        body["memoryTotalMb"] = memoryTotalMb;

        var created = await client.PostAsJsonAsync(Game.FeedbackPath, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, createdBody.GetProperty("memoryTotalMb").ValueKind);
    }

    [Fact]
    public async Task Create_snapshots_steam_playtime()
    {
        var steamId = PlayerClient.UniqueSteamId();
        var (_, client, steam) = await PlayerClient.CreateAsync(fixture, steamId);
        steam.SetPlaytimeResponse(FakeSteamHandler.Playtime(2361));

        var created = await client.PostAsJsonAsync(Game.FeedbackPath, ValidBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2361, createdBody.GetProperty("playtimeMinutes").GetInt32());

        var id = createdBody.GetProperty("id").GetInt32();
        var detail = await client.GetFromJsonAsync<JsonElement>(Game.Api($"feedback/{id}"));
        Assert.Equal(2361, detail.GetProperty("playtimeMinutes").GetInt32());

        // 查询参数取自**解析出的那个游戏**与认证主体：appid 来自该 Game 的 SteamAppId，
        // steamid 来自 JWT 的 sub，绝不来自请求体，也绝不来自任何部署级配置（那种配置已不存在）。
        var playtimeCall = Assert.Single(
            steam.RequestedPaths,
            p => p.Contains("GetSingleGamePlaytime", StringComparison.Ordinal));
        Assert.Contains($"steamid={steamId}", playtimeCall);
        Assert.Contains($"appid={Game.AppId}", playtimeCall);
        // 明文密钥绝不进 URL 之外的任何地方仍是服务端行为；这里只确认查询句柄用的是本游戏的凭据。
        Assert.Contains($"key={Game.ApiKey}", playtimeCall);
    }

    /// <summary>0 是合法值（拥有但从未玩过），必须存 0 而不是 null。</summary>
    [Fact]
    public async Task Create_stores_zero_playtime_as_zero()
    {
        var (_, client, steam) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());
        steam.SetPlaytimeResponse(FakeSteamHandler.Playtime(0));

        var created = await client.PostAsJsonAsync(Game.FeedbackPath, ValidBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, createdBody.GetProperty("playtimeMinutes").GetInt32());
    }

    /// <summary>
    /// 服务端查询游玩时长是尽力而为：Steam 故障、资料私密、字段缺失、超时，
    /// 一律只让该字段为 null，**提交本身照常 201**——玩家不该因此白写一遍正文。
    /// </summary>
    [Fact]
    public async Task Create_succeeds_with_null_playtime_when_steam_fails()
    {
        var (_, client, steam) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());
        steam.SetPlaytimeResponse(FakeSteamHandler.SteamServerError());

        var created = await client.PostAsJsonAsync(Game.FeedbackPath, ValidBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, createdBody.GetProperty("playtimeMinutes").ValueKind);
    }

    [Fact]
    public async Task Create_succeeds_with_null_playtime_when_field_is_absent()
    {
        var (_, client, steam) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());
        steam.SetPlaytimeResponse(FakeSteamHandler.PlaytimeMissingField());

        var created = await client.PostAsJsonAsync(Game.FeedbackPath, ValidBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, createdBody.GetProperty("playtimeMinutes").ValueKind);
    }

    [Fact]
    public async Task Create_succeeds_with_null_playtime_when_lookup_times_out()
    {
        var (_, client, steam) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());
        // 比服务端的查询预算多 1 秒：请求必然被预算取消，而不是等满 HttpClient 的 10 秒超时。
        steam.PlaytimeDelay = SteamPlaytimeService.LookupBudget + TimeSpan.FromSeconds(1);

        var created = await client.PostAsJsonAsync(Game.FeedbackPath, ValidBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, createdBody.GetProperty("playtimeMinutes").ValueKind);
    }

    /// <summary>防伪造：请求体里的 playtimeMinutes 必须被忽略，落库值只来自服务端查询。</summary>
    [Fact]
    public async Task Create_ignores_body_playtime_minutes()
    {
        var (_, client, steam) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());
        steam.SetPlaytimeResponse(FakeSteamHandler.Playtime(120));

        var body = ValidBody();
        body["playtimeMinutes"] = 999_999;

        var created = await client.PostAsJsonAsync(Game.FeedbackPath, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(120, createdBody.GetProperty("playtimeMinutes").GetInt32());
    }

    [Fact]
    public async Task Feedback_endpoints_require_authentication()
    {
        var client = fixture.DefaultFactory.CreateClient();

        var created = await client.PostAsJsonAsync(Game.FeedbackPath, ValidBody());
        Assert.Equal(HttpStatusCode.Unauthorized, created.StatusCode);

        var mine = await client.GetAsync(Game.MinePath);
        Assert.Equal(HttpStatusCode.Unauthorized, mine.StatusCode);
    }
}
