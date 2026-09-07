using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameFeedback.Tests.Infrastructure;

namespace GameFeedback.Tests;

[Collection("Integration")]
public sealed class FeedbackDetailAndCommentTests(IntegrationTestFixture fixture)
{
    private async Task<(GameFeedbackApplicationFactory Factory, HttpClient Owner, HttpClient Other)> CreateTwoPlayersAsync()
    {
        var (factory, owner, _) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());
        var (_, other, _) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());
        return (factory, owner, other);
    }

    private static async Task<int> CreateFeedbackAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/feedback", new Dictionary<string, object?>
        {
            ["type"] = "Bug", ["title"] = "标题", ["content"] = "内容",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Owner_gets_detail_with_comments()
    {
        var (_, owner, _) = await CreateTwoPlayersAsync();
        var id = await CreateFeedbackAsync(owner);

        await owner.PostAsJsonAsync($"/api/feedback/{id}/comments", new { content = "补充信息" });

        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/feedback/{id}");
        Assert.Equal("标题", detail.GetProperty("title").GetString());
        Assert.Equal(1, detail.GetProperty("comments").GetArrayLength());
        Assert.Equal("Player", detail.GetProperty("comments")[0].GetProperty("authorType").GetString());
        Assert.Equal("补充信息", detail.GetProperty("comments")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Foreign_or_missing_feedback_reads_as_404()
    {
        var (_, owner, other) = await CreateTwoPlayersAsync();
        var id = await CreateFeedbackAsync(owner);

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/feedback/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/feedback/999999")).StatusCode);

        var foreignComment = await other.PostAsJsonAsync($"/api/feedback/{id}/comments", new { content = "越权" });
        Assert.Equal(HttpStatusCode.NotFound, foreignComment.StatusCode);
    }

    [Fact]
    public async Task Comment_length_is_validated()
    {
        var (_, owner, _) = await CreateTwoPlayersAsync();
        var id = await CreateFeedbackAsync(owner);

        var oversized = await owner.PostAsJsonAsync($"/api/feedback/{id}/comments",
            new { content = new string('x', 5001) });
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);

        var empty = await owner.PostAsJsonAsync($"/api/feedback/{id}/comments", new { content = "" });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task Comments_are_rate_limited_per_player()
    {
        var steam = new FakeSteamHandler();
        var factory = fixture.CreateFactory(steam, settings =>
            settings["RateLimit:CommentsPer10Minutes"] = "3");
        var client = factory.CreateClient();
        var token = await PlayerClient.LoginAsync(client, steam, PlayerClient.UniqueSteamId());
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var id = await CreateFeedbackAsync(client);

        for (var i = 0; i < 3; i++)
        {
            var created = await client.PostAsJsonAsync($"/api/feedback/{id}/comments", new { content = $"c{i}" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        var limited = await client.PostAsJsonAsync($"/api/feedback/{id}/comments", new { content = "c4" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }
}
