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

    /// <summary>状态对应的徽章样式类（定义在 wwwroot/admin.css）。</summary>
    public static string GetCssClass(FeedbackStatus status) => status switch
    {
        FeedbackStatus.Open => "badge-open",
        FeedbackStatus.InProgress => "badge-progress",
        FeedbackStatus.Resolved => "badge-resolved",
        FeedbackStatus.Closed => "badge-closed",
        _ => "badge-closed",
    };
}
