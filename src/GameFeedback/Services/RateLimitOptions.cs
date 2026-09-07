namespace GameFeedback.Services;

public sealed class RateLimitOptions
{
    /// <summary>登录端点：每分钟每 IP 允许的请求数。</summary>
    public int AuthPerMinute { get; init; } = 10;

    /// <summary>创建反馈：每 10 分钟每玩家。</summary>
    public int FeedbackPer10Minutes { get; init; } = 5;

    /// <summary>评论：每 10 分钟每玩家。</summary>
    public int CommentsPer10Minutes { get; init; } = 20;
}
