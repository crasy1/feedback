namespace GameFeedback.Services;

/// <summary>
/// 仅剩开发用的调试开关。Steam 的凭据（Publisher Web API Key）、AppID 与票据 identity
/// 都已经搬进数据库按 Game 管理，环境变量不再承载任何 Steam 配置。
/// </summary>
public sealed class SteamOptions
{
    /// <summary>
    /// 调试开关：跳过 Steam 验票，登录请求可直接声明 SteamID64。
    /// 启动校验强制只能在 Development 环境启用，生产配置该项会拒绝启动。
    /// </summary>
    public bool DebugSkipTicketValidation { get; init; }
}
