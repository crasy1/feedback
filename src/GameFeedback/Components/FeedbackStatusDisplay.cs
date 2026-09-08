using GameFeedback.Domain;

namespace GameFeedback.Components;

internal static class FeedbackStatusDisplay
{
    public static string GetText(FeedbackStatus status) => status switch
    {
        FeedbackStatus.Open => "待处理",
        FeedbackStatus.InProgress => "处理中",
        FeedbackStatus.Resolved => "已解决",
        FeedbackStatus.Closed => "已关闭",
        _ => status.ToString(),
    };
}
