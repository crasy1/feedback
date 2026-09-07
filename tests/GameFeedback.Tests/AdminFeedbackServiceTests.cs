using GameFeedback.Data;
using GameFeedback.Domain;
using GameFeedback.Services;
using GameFeedback.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GameFeedback.Tests;

[Collection("Integration")]
public sealed class AdminFeedbackServiceTests(IntegrationTestFixture fixture)
{
    private async Task<(Player Player, int FeedbackId)> SeedPlayerWithFeedbacksAsync(int count, string? gameVersion = "9.9.9", FeedbackType type = FeedbackType.Bug, FeedbackStatus status = FeedbackStatus.Open)
    {
        var scope = fixture.DefaultFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var player = new Player
        {
            SteamId = PlayerClient.UniqueSteamId(),
            SteamName = "seeded",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Players.Add(player);
        Feedback? last = null;
        for (var i = 0; i < count; i++)
        {
            last = new Feedback
            {
                Player = player,
                Type = type,
                Title = $"反馈{i}",
                Content = "内容",
                Status = status,
                GameVersion = gameVersion,
                CreatedAt = DateTime.UtcNow.AddMinutes(i),
            };
            db.Feedbacks.Add(last);
        }
        await db.SaveChangesAsync();
        return (player, last!.Id);
    }

    private async Task<AdminFeedbackService> CreateServiceAsync()
    {
        _ = fixture.DefaultFactory.CreateClient();
        var scope = fixture.DefaultFactory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AdminFeedbackService>();
    }

    [Fact]
    public async Task List_filters_by_status_type_and_game_version()
    {
        await SeedPlayerWithFeedbacksAsync(3, gameVersion: "8.8.8", type: FeedbackType.Suggestion, status: FeedbackStatus.Closed);
        var service = await CreateServiceAsync();

        var (items, total) = await service.ListAsync(FeedbackStatus.Closed, FeedbackType.Suggestion, "8.8.8", 1, 50, CancellationToken.None);

        Assert.Equal(3, total);
        Assert.All(items, f =>
        {
            Assert.Equal(FeedbackStatus.Closed, f.Status);
            Assert.Equal(FeedbackType.Suggestion, f.Type);
            Assert.Equal("8.8.8", f.GameVersion);
        });
    }

    [Fact]
    public async Task List_paginates_newest_first()
    {
        await SeedPlayerWithFeedbacksAsync(120, gameVersion: "7.7.7");
        var service = await CreateServiceAsync();

        var (page1, total) = await service.ListAsync(null, null, "7.7.7", 1, 50, CancellationToken.None);
        Assert.Equal(120, total);
        Assert.Equal(50, page1.Count);

        var (page3, _) = await service.ListAsync(null, null, "7.7.7", 3, 50, CancellationToken.None);
        Assert.Equal(20, page3.Count);

        Assert.True(page1[0].CreatedAt >= page1[^1].CreatedAt);
    }

    [Fact]
    public async Task Reply_creates_admin_comment()
    {
        var (_, feedbackId) = await SeedPlayerWithFeedbacksAsync(1);
        var service = await CreateServiceAsync();

        var comment = await service.ReplyAsync("admin-user-1", feedbackId, "已收到，正在排查", CancellationToken.None);

        Assert.Equal(CommentAuthorType.Admin, comment.AuthorType);
        Assert.Equal("admin-user-1", comment.AdminUserId);
        Assert.Null(comment.PlayerId);

        var detail = await service.GetAsync(feedbackId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Contains(detail!.Comments, c => c.Content == "已收到，正在排查" && c.AuthorType == CommentAuthorType.Admin);
    }

    [Fact]
    public async Task Change_status_persists_and_touches_updated_at()
    {
        var (_, feedbackId) = await SeedPlayerWithFeedbacksAsync(1, status: FeedbackStatus.Open);
        var service = await CreateServiceAsync();

        var changed = await service.ChangeStatusAsync(feedbackId, FeedbackStatus.InProgress, CancellationToken.None);

        Assert.True(changed);
        var detail = await service.GetAsync(feedbackId, CancellationToken.None);
        Assert.Equal(FeedbackStatus.InProgress, detail!.Status);
        Assert.NotNull(detail.UpdatedAt);

        Assert.False(await service.ChangeStatusAsync(999_999, FeedbackStatus.Open, CancellationToken.None));
    }
}
