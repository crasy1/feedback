namespace GameFeedback.Services;

/// <summary>
/// Game 字段的校验。AppID 既是 Steam 侧标识也是 URL 路径段，所以校验必须保守。
/// </summary>
public static class GameValidation
{
    public const int NameMaxLength = 100;
    public const int SteamAppIdMaxLength = 10;
    public const int IdentityMaxLength = 64;

    /// <summary>默认的票据 identity；客户端 <c>FeedbackConfig.Identity</c> 必须与它一致。</summary>
    public const string DefaultIdentity = "feedback-api";

    /// <summary>AppID 一律无首尾空白；不合法时原样返回（由 <see cref="ValidateSteamAppId"/> 报错）。</summary>
    public static string NormalizeAppId(string? appId) => appId?.Trim() ?? string.Empty;

    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "名称不能为空";
        }
        return name.Trim().Length > NameMaxLength ? $"名称长度不能超过 {NameMaxLength}" : null;
    }

    /// <summary>
    /// AppID 是正整数，而且在管理端<b>必填</b>：它同时是玩家 API 的路径段，
    /// 没有它的游戏根本无法被任何客户端寻址（只有升级迁移建的那行占位游戏会是空的，
    /// 管理员一编辑它就被要求补上）。
    /// </summary>
    public static string? ValidateSteamAppId(string? appId)
    {
        var trimmed = NormalizeAppId(appId);
        if (trimmed.Length == 0)
        {
            return "Steam AppID 不能为空：它同时是玩家 API 的路径段 /g/{appId}/api/...";
        }
        if (trimmed.Length > SteamAppIdMaxLength || !trimmed.All(char.IsAsciiDigit))
        {
            return "Steam AppID 必须是数字";
        }
        return trimmed.All(c => c == '0') ? "Steam AppID 不能是 0" : null;
    }

    public static string? ValidateIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return "票据 identity 不能为空";
        }
        return identity.Trim().Length > IdentityMaxLength ? $"票据 identity 长度不能超过 {IdentityMaxLength}" : null;
    }
}
