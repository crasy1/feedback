using System.Globalization;
using GameFeedback.Services;
using Microsoft.AspNetCore.Mvc;

namespace GameFeedback.Api;

/// <summary>
/// 玩家 API 的稳定错误码。客户端按 ProblemDetails 的扩展成员 <c>code</c> 分支，不要按文案分支。
/// </summary>
public static class ApiCodes
{
    /// <summary>请求没写明游戏（根路径的 /api/...）。玩家 API 的路径段是游戏的 Steam AppID。</summary>
    public const string GameRequired = "game_required";

    /// <summary>路径里的 Steam AppID 没有对应的游戏。</summary>
    public const string GameNotFound = "game_not_found";

    /// <summary>游戏被管理员停用。</summary>
    public const string GameDisabled = "game_disabled";

    /// <summary>凭据存在但解不开（Data Protection key ring 丢失）。与 Steam 故障要能区分。</summary>
    public const string CredentialUnreadable = "credential_unreadable";

    /// <summary>Steam 明确否决了这张票（无效、过期、或 appid/identity 不匹配）。</summary>
    public const string SteamTicketRejected = "steam_ticket_rejected";

    /// <summary>没能完成验票（网络、超时，或该游戏的 Steam 配置问题），可重试。</summary>
    public const string SteamUnavailable = "steam_unavailable";

    /// <summary>令牌不是为这个游戏签发的。</summary>
    public const string GameMismatch = "game_mismatch";
}

/// <summary>构造带稳定 <c>code</c> 的 ProblemDetails。</summary>
public static class ApiProblems
{
    public static IResult GameRequired() => Problem(
        StatusCodes.Status404NotFound,
        "请求没有指定游戏",
        "玩家 API 只在 /g/{appId}/api/... 下提供，其中 {appId} 是该游戏的 Steam AppID。"
        + "客户端由宿主交出当前运行的 AppID、自行补全这段路径（见 Feedback Client 的 IGameAppIdProvider）。",
        ApiCodes.GameRequired);

    public static IResult GameNotFound(string? appId) => Problem(
        StatusCodes.Status404NotFound,
        "游戏不存在",
        string.IsNullOrWhiteSpace(appId)
            ? "请求路径里没有可用的游戏标识。"
            : $"本实例没有 Steam AppID 为「{appId}」的游戏。请确认客户端 BaseUrl 指向的反馈服务上，"
              + "该游戏已在管理后台添加、且 Steam AppID 与客户端实际运行的 AppID 一致。",
        ApiCodes.GameNotFound);

    public static IResult GameDisabled() => Problem(
        StatusCodes.Status403Forbidden,
        "游戏已停用",
        "这个游戏已被管理员停用，不再接受登录与反馈。",
        ApiCodes.GameDisabled);

    public static IResult GameMismatch() => Problem(
        StatusCodes.Status401Unauthorized,
        "访问令牌与游戏不匹配",
        "该访问令牌是为另一个游戏签发的，请在本游戏内重新登录。",
        ApiCodes.GameMismatch);

    public static IResult CredentialUnreadable() => Problem(
        StatusCodes.Status401Unauthorized,
        "Steam 凭据无法解密",
        "服务器无法解开这个游戏的 Steam 凭据（Data Protection key ring 可能已丢失），稍后可以重试。",
        ApiCodes.CredentialUnreadable);

    public static IResult SteamUnavailable(string detail) => Problem(
        StatusCodes.Status401Unauthorized,
        "Steam 验票服务不可用",
        detail,
        ApiCodes.SteamUnavailable);

    public static IResult SteamTicketRejected() => Problem(
        StatusCodes.Status401Unauthorized,
        "Steam 验票未通过",
        "票据无效、已过期，或与服务端配置的 appid/identity 不匹配。",
        ApiCodes.SteamTicketRejected);

    private static IResult Problem(int statusCode, string title, string detail, string code) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}

/// <summary>把解析出的 Game 挂到当前请求上，供端点处理器读取。</summary>
public static class GameContextExtensions
{
    private const string ItemKey = "GameFeedback.ResolvedGame";

    public static void SetResolvedGame(this HttpContext context, ResolvedGame game) => context.Items[ItemKey] = game;

    /// <summary>解析过滤器保证存在；返回 null 只可能是过滤器没挂上（编程错误）。</summary>
    public static ResolvedGame? GetResolvedGame(this HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) ? value as ResolvedGame : null;

    /// <summary>令牌里的游戏声明；缺失或不是整数返回 null。</summary>
    public static int? GetTokenGameId(this HttpContext context)
    {
        var raw = context.User.FindFirst(TokenService.GameClaimType)?.Value;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
    }
}
