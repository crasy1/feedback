using GameFeedback.Contracts.Requests;
using GameFeedback.Contracts.Responses;
using GameFeedback.Data;
using GameFeedback.Domain;
using Microsoft.EntityFrameworkCore;

namespace GameFeedback.Services;

/// <summary>反馈的创建与玩家侧查询。所有权在查询层强制。</summary>
public class FeedbackService(AppDbContext db)
{
    public const int MineMaxItems = 100;

    /// <summary>按玩家可见限额校验请求；返回错误消息，null 表示通过。</summary>
    public static string? Validate(CreateFeedbackRequest request)
    {
        if (!Enum.TryParse<FeedbackType>(request.Type, ignoreCase: true, out _) || request.Type is null)
        {
            return "type 必须为 Bug、Suggestion 或 Other";
        }
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200)
        {
            return "title 长度必须在 1–200 之间";
        }
        if (string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > 10_000)
        {
            return "content 长度必须在 1–10,000 之间";
        }
        if (IsOverLong(request.GameVersion, 64)
            || IsOverLong(request.BuildNumber, 64)
            || IsOverLong(request.OperatingSystem, 100)
            || IsOverLong(request.Gpu, 100)
            || IsOverLong(request.Locale, 32)
            || IsOverLong(request.Map, 100)
            || IsOverLong(request.Character, 100))
        {
            return "元数据字段超长";
        }
        return null;
    }

    private static bool IsOverLong(string? value, int maxLength) => value?.Length > maxLength;

    /// <summary>解析 SteamID 对应的玩家 Id；玩家不存在返回 null（fail closed）。</summary>
    public async Task<int?> ResolvePlayerIdAsync(string steamId, CancellationToken cancellationToken)
    {
        var player = await db.Players
            .Where(p => p.SteamId == steamId)
            .Select(p => (int?)p.Id)
            .SingleOrDefaultAsync(cancellationToken);
        return player;
    }

    public async Task<Feedback> CreateAsync(int playerId, CreateFeedbackRequest request, CancellationToken cancellationToken)
    {
        var feedback = new Feedback
        {
            PlayerId = playerId,
            Type = Enum.Parse<FeedbackType>(request.Type!, ignoreCase: true),
            Title = request.Title!,
            Content = request.Content!,
            Status = FeedbackStatus.Open,
            GameVersion = request.GameVersion,
            BuildNumber = request.BuildNumber,
            OperatingSystem = request.OperatingSystem,
            Gpu = request.Gpu,
            Locale = request.Locale,
            Map = request.Map,
            Character = request.Character,
            CreatedAt = DateTime.UtcNow,
        };
        db.Feedbacks.Add(feedback);
        await db.SaveChangesAsync(cancellationToken);
        return feedback;
    }

    /// <summary>玩家自己的反馈（最新在前，最多 100 条）。查询层即所有权边界。</summary>
    public Task<List<Feedback>> ListOwnAsync(int playerId, CancellationToken cancellationToken) =>
        db.Feedbacks
            .Where(f => f.PlayerId == playerId)
            .OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id)
            .Take(MineMaxItems)
            .ToListAsync(cancellationToken);

    public static FeedbackDto ToDto(Feedback feedback) => new(
        feedback.Id,
        feedback.Type.ToString(),
        feedback.Title,
        feedback.Content,
        feedback.Status.ToString(),
        feedback.GameVersion,
        feedback.BuildNumber,
        feedback.OperatingSystem,
        feedback.Gpu,
        feedback.Locale,
        feedback.Map,
        feedback.Character,
        feedback.CreatedAt);
}
