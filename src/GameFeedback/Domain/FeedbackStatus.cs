namespace GameFeedback.Domain;

/// <summary>反馈处理状态。</summary>
public enum FeedbackStatus
{
    Open = 1,
    InProgress = 2,
    Resolved = 3,
    Closed = 4,
}
