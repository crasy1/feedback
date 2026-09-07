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
        FeedbackStatus? status, FeedbackType? type, string? gameVersion,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Feedbacks.AsNoTracking().AsQueryable();
        if (status.HasValue)
        {
            query = query.Where(f => f.Status == status.Value);
        }
        if (type.HasValue)
        {
            query = query.Where(f => f.Type == type.Value);
        }
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            query = query.Where(f => f.GameVersion == gameVersion);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(f => f.Player)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

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
