using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameFeedback.Data;
using GameFeedback.Domain;
using GameFeedback.Services;
using GameFeedback.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GameFeedback.Tests;

/// <summary>完整闭环：玩家提交 → 管理员回复并改状态 → 玩家在详情里看到回复与新状态。</summary>
[Collection("Integration")]
public sealed class FeedbackLoopTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Admin_reply_and_status_are_visible_to_owner_player()
    {
        // 玩家通过 API 提交反馈。
        var (factory, playerClient, _) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());
        var created = await playerClient.PostAsJsonAsync("/api/feedback", new Dictionary<string, object?>
        {
            ["type"] = "Bug",
            ["title"] = "回环测试",
            ["content"] = "进副本闪退",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var feedbackId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // 管理员通过应用服务回复并改状态（管理端直调服务，不经 HTTP）。
        _ = factory.CreateClient();
        var scope = factory.Services.CreateScope();
        var adminFeedbacks = scope.ServiceProvider.GetRequiredService<AdminFeedbackService>();
        await adminFeedbacks.ReplyAsync("admin-user-1", feedbackId, "已定位，下个版本修复", CancellationToken.None);
        await adminFeedbacks.ChangeStatusAsync(feedbackId, FeedbackStatus.InProgress, CancellationToken.None);

        // 玩家在自己的详情里看到管理员回复与新状态。
        var detailResponse = await playerClient.GetAsync($"/api/feedback/{feedbackId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("InProgress", detail.GetProperty("status").GetString());
        var comments = detail.GetProperty("comments");
        Assert.Equal(1, comments.GetArrayLength());
        Assert.Equal("Admin", comments[0].GetProperty("authorType").GetString());
        Assert.Equal("已定位，下个版本修复", comments[0].GetProperty("content").GetString());
    }
}
