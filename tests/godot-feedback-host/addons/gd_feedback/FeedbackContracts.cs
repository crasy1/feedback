namespace GdFeedback;

/// <summary>反馈类型；服务端只接受名称（Bug / Suggestion / Other），拒绝数字与逗号组合。</summary>
public enum PlayerFeedbackType
{
    Bug,
    Suggestion,
    Other,
}

/// <summary>
/// 稳定错误码。调用方按码分支，不要解析 message 文本；message 只用于日志与兜底提示。
/// </summary>
public static class FeedbackErrorCode
{
    /// <summary>插件配置无效（缺少 BaseUrl 或 Identity）。</summary>
    public const string InvalidConfiguration = "invalid_configuration";

    /// <summary>BaseUrl 不是合法的绝对 http/https 地址。</summary>
    public const string InvalidBaseUrl = "invalid_base_url";

    /// <summary>宿主票据提供者没有交出票据（Steam 不可用或未登录）。</summary>
    public const string TicketUnavailable = "ticket_unavailable";

    /// <summary>票据形状非法（空、超长）。</summary>
    public const string TicketInvalid = "ticket_invalid";

    /// <summary>调试登录声明的 SteamID64 形状非法。</summary>
    public const string InvalidSteamId = "invalid_steam_id";

    /// <summary>Steam 验票未通过（服务端 401，且 Steam 明确否决了这张票）。</summary>
    public const string SteamVerificationRejected = "steam_verification_rejected";

    /// <summary>
    /// 服务端没能完成验票：网络、超时，或该游戏在反馈服务后台还没配好 Steam 凭据
    /// （AppID 或 Web API Key 缺失，凭据已改由后台按游戏管理）。
    /// 与本张票据是否有效无关，因此可重试——别把它当成"票不对"让玩家重登。
    /// </summary>
    public const string SteamVerificationUnavailable = "steam_verification_unavailable";

    /// <summary>
    /// 登录端点 404：反馈服务上没有这个 AppID 的游戏——该游戏还没在后台建出来，
    /// 或宿主交出的 AppID 与该游戏配置的不一致。属于<b>配置</b>问题，重试无用，
    /// 也不代表票据有问题。
    /// </summary>
    public const string GameNotFound = "game_not_found";

    /// <summary>
    /// 登录端点 403：BaseUrl 里的游戏在服务端已被停用。属于<b>配置</b>问题，重试无用，
    /// 需要运营在后台重新启用；票据本身可能完全正常。
    /// </summary>
    public const string GameDisabled = "game_disabled";

    /// <summary>访问令牌缺失、过期或被拒（非登录端点的 401）。</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>本地校验未通过，请求未发出。</summary>
    public const string ValidationFailed = "validation_failed";

    /// <summary>资源不存在或不属于当前玩家（服务端对两者一律 404）。</summary>
    public const string NotFound = "not_found";

    /// <summary>命中服务端限流（429），可稍后重试。</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>服务端以 4xx 拒绝了请求，原因不属于上面任何一类。</summary>
    public const string ServerRejected = "server_rejected";

    /// <summary>服务端 5xx。</summary>
    public const string ServerError = "server_error";

    /// <summary>响应不是期望的 JSON 形状。</summary>
    public const string InvalidResponse = "invalid_response";

    /// <summary>请求未送达服务端（网络失败或超时）。</summary>
    public const string TransportFailed = "transport_failed";
}

/// <summary>失败详情；<see cref="StatusCode"/> 为 0 表示请求没有到达服务端。</summary>
public sealed record FeedbackFailure(string Code, string Message, int StatusCode, bool Retryable, string? Detail = null);

/// <summary>操作结果：成功带值，失败带 <see cref="FeedbackFailure"/>，不会两者皆有。</summary>
public sealed record FeedbackResult<T> where T : class
{
    private FeedbackResult(T? value, FeedbackFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    public T? Value { get; }

    public FeedbackFailure? Failure { get; }

    public bool Succeeded => Failure is null;

    public static FeedbackResult<T> Ok(T value) => new(value, null);

    public static FeedbackResult<T> Fail(FeedbackFailure failure) => new(null, failure);
}

/// <summary>玩家公开资料（服务端只返回客户端需要的数据）。</summary>
public sealed record PlayerProfile(string SteamId, string? SteamName, string? AvatarUrl);

/// <summary>
/// 反馈的公共字段：列表项与详情共享，让适配器只写一份转换（两个真实实现，不是投机抽象）。
/// </summary>
public interface IPlayerFeedbackView
{
    int Id { get; }

    string Type { get; }

    string Title { get; }

    string Content { get; }

    string Status { get; }

    string? GameVersion { get; }

    string? BuildNumber { get; }

    string? OperatingSystem { get; }

    string? Gpu { get; }

    /// <summary>CPU 型号；由客户端自动采集，属咨询性数据，不可信。</summary>
    string? Cpu { get; }

    /// <summary>物理内存总量（MB）；由客户端自动采集，属咨询性数据，不可信。</summary>
    int? MemoryTotalMb { get; }

    /// <summary>提交这条反馈时该玩家的累计游玩时长（分钟）；由服务端从 Steam 取后快照，取不到为 null。</summary>
    int? PlaytimeMinutes { get; }

    string? Locale { get; }

    string? Map { get; }

    string? Character { get; }

    DateTimeOffset CreatedAt { get; }
}

/// <summary>一条反馈（列表视图）。</summary>
public sealed record PlayerFeedback(
    int Id,
    string Type,
    string Title,
    string Content,
    string Status,
    string? GameVersion,
    string? BuildNumber,
    string? OperatingSystem,
    string? Gpu,
    string? Cpu,
    int? MemoryTotalMb,
    int? PlaytimeMinutes,
    string? Locale,
    string? Map,
    string? Character,
    DateTimeOffset CreatedAt) : IPlayerFeedbackView;

/// <summary>一条评论。</summary>
public sealed record PlayerFeedbackComment(int Id, string AuthorType, string Content, DateTimeOffset CreatedAt);

/// <summary>反馈详情（含全部评论）。</summary>
public sealed record PlayerFeedbackDetail(
    int Id,
    string Type,
    string Title,
    string Content,
    string Status,
    string? GameVersion,
    string? BuildNumber,
    string? OperatingSystem,
    string? Gpu,
    string? Cpu,
    int? MemoryTotalMb,
    int? PlaytimeMinutes,
    string? Locale,
    string? Map,
    string? Character,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PlayerFeedbackComment> Comments) : IPlayerFeedbackView;

/// <summary>本地登录结果：访问令牌 + 当前玩家资料。</summary>
public sealed record PlayerSession(string AccessToken, DateTimeOffset ExpiresAtUtc, PlayerProfile Player);

/// <summary>
/// 待提交的反馈。元数据全部可选，由调用方按当前游戏状态填写；插件不猜游戏语义。
/// </summary>
public sealed record PlayerFeedbackDraft(
    PlayerFeedbackType Type,
    string? Title,
    string? Content,
    string? GameVersion = null,
    string? BuildNumber = null,
    string? OperatingSystem = null,
    string? Gpu = null,
    string? Cpu = null,
    int? MemoryTotalMb = null,
    string? Locale = null,
    string? Map = null,
    string? Character = null);

/// <summary>
/// 与服务端 <c>docs/specs/player-api.md</c> 相同的限额；本地先挡一遍，
/// 避免让玩家等一次网络往返才拿到 400。错误消息为英文，面向上层 UI 时请按错误码本地化。
/// </summary>
public static class PlayerFeedbackValidation
{
    public const int TitleMaxLength = 200;
    public const int ContentMaxLength = 10_000;
    public const int CommentMaxLength = 5_000;
    public const int GameVersionMaxLength = 64;
    public const int BuildNumberMaxLength = 64;
    public const int OperatingSystemMaxLength = 100;
    public const int GpuMaxLength = 100;
    public const int CpuMaxLength = 120;
    public const int LocaleMaxLength = 32;
    public const int MapMaxLength = 100;
    public const int CharacterMaxLength = 100;

    /// <summary>物理内存总量上限（MB）= 4 TiB，与服务端一致。</summary>
    public const int MemoryMaxMb = 4 * 1024 * 1024;

    /// <summary>
    /// 归一化自动采集的 CPU 型号。
    /// <b>刻意不放进 <see cref="Validate"/></b>：这个字段玩家既没有输入、也无法修正，
    /// 所以越界应当是"丢弃"而不是让整条反馈提交失败——与服务端的规则一致。
    /// </summary>
    public static string? NormalizeCpu(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length > CpuMaxLength ? null : trimmed;
    }

    /// <summary>归一化自动采集的内存总量（MB）；越界一律丢弃，理由同 <see cref="NormalizeCpu"/>。</summary>
    public static int? NormalizeMemoryTotalMb(int? value) =>
        value is int megabytes && megabytes > 0 && megabytes <= MemoryMaxMb ? megabytes : null;

    /// <summary>校验草稿；返回错误消息，null 表示通过。</summary>
    public static string? Validate(PlayerFeedbackDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (!Enum.IsDefined(draft.Type))
        {
            return "type must be Bug, Suggestion or Other";
        }
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > TitleMaxLength)
        {
            return $"title length must be 1-{TitleMaxLength}";
        }
        if (string.IsNullOrWhiteSpace(draft.Content) || draft.Content.Length > ContentMaxLength)
        {
            return $"content length must be 1-{ContentMaxLength}";
        }
        if (IsOverLong(draft.GameVersion, GameVersionMaxLength)
            || IsOverLong(draft.BuildNumber, BuildNumberMaxLength)
            || IsOverLong(draft.OperatingSystem, OperatingSystemMaxLength)
            || IsOverLong(draft.Gpu, GpuMaxLength)
            || IsOverLong(draft.Locale, LocaleMaxLength)
            || IsOverLong(draft.Map, MapMaxLength)
            || IsOverLong(draft.Character, CharacterMaxLength))
        {
            return "metadata field is too long";
        }
        return null;
    }

    /// <summary>校验评论内容；返回错误消息，null 表示通过。</summary>
    public static string? ValidateComment(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > CommentMaxLength)
        {
            return $"comment length must be 1-{CommentMaxLength}";
        }
        return null;
    }

    /// <summary>按服务端口径校验 SteamID64：17 位数字且不低于 SteamID64 起始值。</summary>
    public static bool IsValidSteamId64(string? value)
    {
        const ulong minSteamId64 = 7_656_119_796_026_5728;
        return value is { Length: 17 }
            && ulong.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong parsed)
            && parsed >= minSteamId64;
    }

    private static bool IsOverLong(string? value, int maxLength) => value?.Length > maxLength;
}
