namespace GameFeedback.Domain;

/// <summary>附加在反馈上的评论；作者要么是反馈属主玩家，要么是管理员。</summary>
public class FeedbackComment
{
    public int Id { get; set; }

    public int FeedbackId { get; set; }

    public Feedback? Feedback { get; set; }

    public CommentAuthorType AuthorType { get; set; }

    /// <summary>作者为玩家时的 Player.Id；管理员评论为 null。</summary>
    public int? PlayerId { get; set; }

    /// <summary>作者为管理员时的 Identity 用户 Id；玩家评论为 null。</summary>
    public string? AdminUserId { get; set; }

    public required string Content { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
