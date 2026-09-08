using System.ComponentModel.DataAnnotations;

namespace GameFeedback.Services;

public sealed class SteamOptions
{
    [Required]
    public string ApiKey { get; init; } = string.Empty;

    [Required]
    public string AppId { get; init; } = string.Empty;

    public string Identity { get; init; } = "feedback-api";

    /// <summary>
    /// 调试开关：跳过 Steam 验票，登录请求可直接声明 SteamID64。
    /// 启动校验强制只能在 Development 环境启用，生产配置该项会拒绝启动。
    /// </summary>
    public bool DebugSkipTicketValidation { get; init; }
}
