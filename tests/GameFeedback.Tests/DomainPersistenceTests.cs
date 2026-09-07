using GameFeedback.Data;
using GameFeedback.Domain;
using GameFeedback.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameFeedback.Tests;

[Collection("Integration")]
public sealed class DomainPersistenceTests(IntegrationTestFixture fixture)
{
    private async Task<AppDbContext> CreateContextAsync()
    {
        // 触发应用启动（自动迁移在此执行），再从容器解析 DbContext。
        _ = fixture.DefaultFactory.CreateClient();
        var scope = fixture.DefaultFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.CanConnectAsync();
        return db;
    }

    [Fact]
    public async Task Entities_round_trip_through_real_provider()
    {
        using var db = await CreateContextAsync();
        var steamId = $"100000{Guid.NewGuid():N}"[..20];

        var player = new Player
        {
            SteamId = steamId,
            SteamName = "tester",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Players.Add(player);
        var feedback = new Feedback
        {
            Player = player,
            Type = FeedbackType.Bug,
            Title = "崩溃",
            Content = "进地图崩溃",
            GameVersion = "1.2.3",
            CreatedAt = DateTime.UtcNow,
        };
        db.Feedbacks.Add(feedback);
        db.FeedbackComments.Add(new FeedbackComment
        {
            Feedback = feedback,
            AuthorType = CommentAuthorType.Player,
            PlayerId = player.Id,
            Content = "补充：每次都崩",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var reloaded = await db.Feedbacks
            .Include(f => f.Player)
            .Include(f => f.Comments)
            .SingleAsync(f => f.Id == feedback.Id);

        Assert.Equal(FeedbackType.Bug, reloaded.Type);
        Assert.Equal(FeedbackStatus.Open, reloaded.Status);
        Assert.Equal(steamId, reloaded.Player!.SteamId);
        Assert.Equal(CommentAuthorType.Player, reloaded.Comments.Single().AuthorType);
    }

    [Fact]
    public async Task Duplicate_steam_id_is_rejected_by_database()
    {
        using var db = await CreateContextAsync();
        var steamId = $"200000{Guid.NewGuid():N}"[..20];

        db.Players.Add(new Player { SteamId = steamId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        db.Players.Add(new Player { SteamId = steamId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
