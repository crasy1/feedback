using GameFeedback.Contracts.Requests;
using GameFeedback.Contracts.Responses;
using GameFeedback.Data;
using GameFeedback.Domain;
using Microsoft.EntityFrameworkCore;

namespace GameFeedback.Services;

/// <summary>反馈的创建与玩家侧查询。所有权在查询层强制。</summary>
public class FeedbackService(AppDbContext db, ILogger<FeedbackService> logger)
{
    public const int MineMaxItems = 100;

    /// <summary>CPU 型号上限，与 <c>FeedbackConfiguration</c> 的列长一致。</summary>
    public const int CpuMaxLength = 120;

    /// <summary>物理内存总量上限（MB）= 4 TiB。</summary>
    public const int MemoryMaxMb = 4 * 1024 * 1024;

    /// <summary>
    /// 按玩家可见限额校验请求；返回错误消息，null 表示通过。
    /// 注意：客户端自动采集的字段（<c>Cpu</c>/<c>MemoryTotalMb</c>）<b>不在</b>这里校验——
    /// 玩家既没有输入它们、也无法修正它们，越界只会被丢弃（见 <see cref="CreateAsync"/>），绝不导致 400。
    /// </summary>
    public static string? Validate(CreateFeedbackRequest request)
    {
        // 只接受类型名称，拒绝 Enum.TryParse 支持的数字和逗号组合。
        if (!Enum.TryParse<FeedbackType>(request.Type, ignoreCase: true, out var type)
            || !Enum.IsDefined(type)
            || !string.Equals(request.Type, type.ToString(), StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// 写入一条反馈。<paramref name="playtimeMinutes"/> 由调用方（端点）从
    /// <see cref="SteamPlaytimeService"/> 取好后传入——本服务刻意只依赖 <c>AppDbContext</c>，
    /// 不注入 HTTP 依赖。取不到时长时传 <c>null</c>。
    /// </summary>
    public async Task<Feedback> CreateAsync(int playerId, CreateFeedbackRequest request, int? playtimeMinutes, CancellationToken cancellationToken)
    {
        // 自动采集字段按"不可信、咨询性"处理：越界即丢弃并记 Warning，
        // 绝不因为玩家的机器信息让整条反馈失败（那样玩家会白写一遍正文）。
        var cpu = NormalizeCpu(request.Cpu, playerId);
        var memoryTotalMb = NormalizeMemoryTotalMb(request.MemoryTotalMb, playerId);

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
            Cpu = cpu,
            MemoryTotalMb = memoryTotalMb,
            PlaytimeMinutes = playtimeMinutes,
            Locale = request.Locale,
            Map = request.Map,
            Character = request.Character,
            CreatedAt = DateTime.UtcNow,
        };
        db.Feedbacks.Add(feedback);
        await db.SaveChangesAsync(cancellationToken);
        return feedback;
    }

    /// <summary>CPU 型号：去空白；超长按"丢弃"处理而不是报错（自动采集字段，玩家无法修正）。</summary>
    private string? NormalizeCpu(string? value, int playerId)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > CpuMaxLength)
        {
            logger.LogWarning(
                "反馈的 CPU 字段被丢弃：长度 {Length} 超过上限 {MaxLength}，PlayerId={PlayerId}",
                trimmed.Length,
                CpuMaxLength,
                playerId);
            return null;
        }

        return trimmed;
    }

    /// <summary>物理内存总量（MB）：越界按"丢弃"处理而不是报错。</summary>
    private int? NormalizeMemoryTotalMb(int? value, int playerId)
    {
        if (value is null)
        {
            return null;
        }

        if (value <= 0 || value > MemoryMaxMb)
        {
            logger.LogWarning(
                "反馈的内存字段被丢弃：MemoryTotalMb={MemoryTotalMb} 不在 1..{MaxMb} 内，PlayerId={PlayerId}",
                value,
                MemoryMaxMb,
                playerId);
            return null;
        }

        return value;
    }

    /// <summary>玩家自己的反馈（最新在前，最多 100 条）。查询层即所有权边界。</summary>
    public Task<List<Feedback>> ListOwnAsync(int playerId, CancellationToken cancellationToken) =>
        db.Feedbacks
            .Where(f => f.PlayerId == playerId)
            .OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id)
            .Take(MineMaxItems)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// 读取属主玩家的单条反馈（含评论）。
    /// 不存在或不属于该玩家一律返回 null——对外表现为 404。
    /// </summary>
    public Task<Feedback?> GetOwnAsync(int playerId, int feedbackId, CancellationToken cancellationToken) =>
        db.Feedbacks
            .Include(f => f.Comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id))
            .SingleOrDefaultAsync(f => f.Id == feedbackId && f.PlayerId == playerId, cancellationToken);

    /// <summary>校验评论内容；返回错误消息，null 表示通过。</summary>
    public static string? ValidateComment(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 5_000)
        {
            return "comment 长度必须在 1–5,000 之间";
        }
        return null;
    }

    /// <summary>玩家在自己的反馈上追加评论。所有权由调用方通过 GetOwnAsync 保证。</summary>
    public async Task<FeedbackComment> AddPlayerCommentAsync(int playerId, int feedbackId, string content, CancellationToken cancellationToken)
    {
        var comment = new FeedbackComment
        {
            FeedbackId = feedbackId,
            AuthorType = CommentAuthorType.Player,
            PlayerId = playerId,
            Content = content,
            CreatedAt = DateTime.UtcNow,
        };
        db.FeedbackComments.Add(comment);
        await db.SaveChangesAsync(cancellationToken);
        return comment;
    }

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
        feedback.Cpu,
        feedback.MemoryTotalMb,
        feedback.PlaytimeMinutes,
        feedback.Locale,
        feedback.Map,
        feedback.Character,
        feedback.CreatedAt);

    public static FeedbackDetailDto ToDetailDto(Feedback feedback, IReadOnlyList<CommentDto> comments) => new(
        feedback.Id,
        feedback.Type.ToString(),
        feedback.Title,
        feedback.Content,
        feedback.Status.ToString(),
        feedback.GameVersion,
        feedback.BuildNumber,
        feedback.OperatingSystem,
        feedback.Gpu,
        feedback.Cpu,
        feedback.MemoryTotalMb,
        feedback.PlaytimeMinutes,
        feedback.Locale,
        feedback.Map,
        feedback.Character,
        feedback.CreatedAt,
        comments);
}
