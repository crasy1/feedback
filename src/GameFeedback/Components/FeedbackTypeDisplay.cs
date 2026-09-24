using GameFeedback.Domain;

namespace GameFeedback.Components;

/// <summary>反馈类型的徽章样式映射；与 <see cref="FeedbackStatusDisplay"/> 一样，列表与详情共用一份，避免两处漂移。</summary>
internal static class FeedbackTypeDisplay
{
    /// <summary>类型对应的徽章样式类（定义在 wwwroot/admin.css）。</summary>
    public static string GetCssClass(FeedbackType type) => type switch
    {
        FeedbackType.Bug => "badge-bug",
        FeedbackType.Suggestion => "badge-suggestion",
        _ => "badge-other",
    };
}
