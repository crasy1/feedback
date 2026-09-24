using GameFeedback.Domain;

namespace GameFeedback.Services;

/// <summary>
/// 管理端反馈列表的筛选状态。
/// <para>
/// 存在的理由（两条都是真实的坑）：
/// </para>
/// <list type="number">
/// <item>
/// 原先 <c>ApplyFilters</c> 与 <c>GoToPage</c> 各自拼查询串，翻页那个只写了 <c>page</c>/<c>pageSize</c>，
/// 于是翻页会静默丢掉 status / type / gameVersion 全部筛选。现在两处都走
/// <see cref="ToQueryParameters"/>，"两个出口各自漂移"在结构上不可能再发生。
/// </item>
/// <item>
/// <see cref="AdminFeedbackService.ListAsync"/> 原先收 4 个平铺参数，其中 <c>gameVersion</c> 与
/// <c>player</c> 都是 <c>string?</c>，相邻且同型，很容易传反。收一个记录就消灭了这类错误。
/// </item>
/// </list>
/// </summary>
public sealed record AdminFeedbackQuery(
    FeedbackStatus? Status = null,
    FeedbackType? Type = null,
    string? GameVersion = null,
    string? Player = null)
{
    /// <summary>
    /// 构造 <c>NavigationManager.GetUriWithQueryParameters</c> 用的参数字典。
    /// 值为 null 的项不会写进 URL（由该方法负责省略）。
    /// </summary>
    public Dictionary<string, object?> ToQueryParameters(int page, int pageSize) => new()
    {
        ["page"] = page,
        ["pageSize"] = pageSize,
        ["status"] = Status?.ToString(),
        ["type"] = Type?.ToString(),
        ["gameVersion"] = GameVersion,
        ["player"] = Player,
    };
}
