using GameFeedback.Data;
using GameFeedback.Domain;
using Microsoft.EntityFrameworkCore;

namespace GameFeedback.Services;

/// <summary>管理端反馈操作：列表过滤分页、详情、回复、改状态。由 Blazor 管理端直接调用。</summary>
public class AdminFeedbackService(IDbContextFactory<AppDbContext> dbFactory)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>按条件过滤反馈（最新在前），返回当前页与总数。</summary>
    public async Task<(IReadOnlyList<Feedback> Items, int Total)> ListAsync(
        AdminFeedbackQuery query, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var feedbacks = db.Feedbacks.AsNoTracking().AsQueryable();
        if (query.Status.HasValue)
        {
            feedbacks = feedbacks.Where(f => f.Status == query.Status.Value);
        }
        if (query.Type.HasValue)
        {
            feedbacks = feedbacks.Where(f => f.Type == query.Type.Value);
        }
        if (!string.IsNullOrWhiteSpace(query.GameVersion))
        {
            feedbacks = feedbacks.Where(f => f.GameVersion == query.GameVersion);
        }

        var player = query.Player?.Trim();
        if (!string.IsNullOrEmpty(player))
        {
            // 纯数字输入按 SteamID64 前缀匹配（允许只记得前几位）；其余按昵称包含匹配。
            feedbacks = player.All(char.IsAsciiDigit)
                ? feedbacks.Where(f => f.Player!.SteamId.StartsWith(player))
                : feedbacks.Where(f => f.Player!.SteamName != null
                    && EF.Functions.ILike(f.Player.SteamName, LikeContains(player), LikeEscape));
        }

        var total = await feedbacks.CountAsync(cancellationToken);
        var items = await feedbacks
            .OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(f => f.Player)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    /// <summary>
    /// LIKE 的转义字符。<b>必须显式传给 ILIKE</b>：Npgsql 的两参 <c>ILike</c> 会生成
    /// <c>ILIKE '…' ESCAPE ''</c>，空转义符等于<b>关闭</b>转义处理，此时模式里的
    /// <c>\_</c> 会被当成"字面反斜杠 + 通配下划线"，永远匹配不到含下划线的昵称。
    /// </summary>
    private const string LikeEscape = "\\";

    /// <summary>
    /// 昵称的"包含"匹配模式。必须转义 LIKE 元字符：昵称里的 <c>%</c> / <c>_</c> 会被当成通配符
    /// 而匹配到无关玩家，而**以反斜杠结尾**的输入会让 Postgres 直接报
    /// "pattern must not end with escape character"。反斜杠必须最先转义。
    /// </summary>
    private static string LikeContains(string term) =>
        "%" + term
            .Replace(LikeEscape, LikeEscape + LikeEscape)
            .Replace("%", LikeEscape + "%")
            .Replace("_", LikeEscape + "_")
        + "%";

    /// <summary>读取单条反馈（含玩家与全部评论）；不存在返回 null。</summary>
    public async Task<Feedback?> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Feedbacks
            .AsNoTracking()
            .Include(f => f.Player)
            .Include(f => f.Comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id))
            .SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
    }

    /// <summary>管理员回复：创建 AuthorType=Admin 的评论。</summary>
    public async Task<FeedbackComment> ReplyAsync(string adminUserId, int feedbackId, string content, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var exists = await db.Feedbacks.AnyAsync(f => f.Id == feedbackId, cancellationToken);
        if (!exists)
        {
            throw new InvalidOperationException("反馈不存在");
        }

        var comment = new FeedbackComment
        {
            FeedbackId = feedbackId,
            AuthorType = CommentAuthorType.Admin,
            AdminUserId = adminUserId,
            Content = content,
            CreatedAt = DateTime.UtcNow,
        };
        db.FeedbackComments.Add(comment);
        await db.SaveChangesAsync(cancellationToken);
        return comment;
    }

    /// <summary>修改反馈状态；反馈不存在返回 false。</summary>
    public async Task<bool> ChangeStatusAsync(int feedbackId, FeedbackStatus status, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var feedback = await db.Feedbacks.SingleOrDefaultAsync(f => f.Id == feedbackId, cancellationToken);
        if (feedback is null)
        {
            return false;
        }

        feedback.Status = status;
        feedback.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
