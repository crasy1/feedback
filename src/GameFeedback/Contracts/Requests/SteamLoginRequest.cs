using System.ComponentModel;

namespace GameFeedback.Contracts.Requests;

/// <summary>
/// Steam 登录请求。DebugSteamId 仅在 Development 环境的调试开关
/// （Steam:DebugSkipTicketValidation）开启时生效，用于无法出票时联调。
/// </summary>
public record SteamLoginRequest(
    [property: Description("Steam 会话票据：游戏客户端 GetAuthTicketForWebApi(\"feedback-api\") 的返回值；调试开关开启时可省略")]
    string? Ticket,
    [property: Description("调试模式专用：直接声明的 SteamID64（17 位数字），仅 Development + Steam:DebugSkipTicketValidation=true 时生效")]
    string? DebugSteamId);
