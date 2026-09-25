namespace GdFeedback;

/// <summary>
/// 宿主提供的 Steam 票据来源。插件本身不引用任何 Steam 绑定（见 ADR-0004）：
/// 宿主用 Facepunch 等实现本接口，交出**十六进制**票据串，或返回 null 表示无法出票。
/// </summary>
public interface ITicketProvider
{
    /// <param name="identity">
    /// 票据 identity，必须与反馈服务端为该游戏配置的票据 identity 一致（默认 feedback-api）。
    /// 宿主应把它原样传给 <c>SteamUser.GetAuthTicketForWebApi</c>。
    /// </param>
    Task<string?> GetWebApiTicketAsync(string identity, CancellationToken cancellationToken);
}

/// <summary>
/// 宿主提供的"当前游戏运行在哪个 Steam AppID 之下"。
/// <para>
/// 玩家 API 的路径是 <c>/g/{appId}/api/...</c>。插件不引用任何 Steam 绑定（见 ADR-0004），
/// 所以这个数字必须由宿主给出——宿主本来就在用同一个值调 <c>SteamClient.Init</c>，
/// 对它来说是零成本的；换来的好处是接入的游戏项目<b>不必手抄任何标识字符串</b>，
/// 「BaseUrl 里的标识抄错」这类事故从模型上消失。
/// </para>
/// <para>
/// 返回 null 表示宿主不提供：此时插件退回"BaseUrl 里自带路径"的老行为，什么都不改也能用。
/// </para>
/// </summary>
public interface IGameAppIdProvider
{
    /// <summary>当前游戏运行的 Steam AppID（纯数字字符串）。拿不到就返回 null。</summary>
    string? GetSteamAppId();
}

/// <summary>不提供 AppID 的默认实现：BaseUrl 里自带路径的接法照旧可用。</summary>
public sealed class UnavailableGameAppIdProvider : IGameAppIdProvider
{
    public static readonly UnavailableGameAppIdProvider Instance = new();

    private UnavailableGameAppIdProvider()
    {
    }

    public string? GetSteamAppId() => null;
}

/// <summary>
/// 把配置里的 AppID 与宿主注入的来源组合起来：<paramref name="configuredAppId"/> 填了就以它为准，
/// 留空才问 <paramref name="fallback"/>。
/// <para>
/// 顺序是刻意的：配置来自宿主的配置资源（<c>feedback_config.tres</c>，或项目根下的短名
/// <c>feedback.tres</c>），是项目里看得见、点得动的一份设置——
/// 纯 GDScript 的宿主也能用它，不必为了一个常量去写 C# 实现；而宿主注入的实现给的是
/// "进程此刻运行在哪个 AppID 之下"，只有在没填配置时才需要它。
/// </para>
/// </summary>
public sealed class ConfiguredGameAppIdProvider(string? configuredAppId, IGameAppIdProvider fallback) : IGameAppIdProvider
{
    private readonly string? _configuredAppId =
        string.IsNullOrWhiteSpace(configuredAppId) ? null : configuredAppId.Trim();

    private readonly IGameAppIdProvider _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));

    public string? GetSteamAppId() => _configuredAppId ?? _fallback.GetSteamAppId();
}

/// <summary>访问令牌缓存；默认实现只在内存里保存。</summary>
public interface ITokenStore
{
    Task<CachedAccessToken?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(CachedAccessToken token, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>已缓存的访问令牌。令牌是认证材料，绝不写入日志。</summary>
public sealed record CachedAccessToken(string SteamId, string AccessToken, DateTimeOffset ExpiresAtUtc);

/// <summary>极简日志抽象：保持 Runtime 引擎无关，宿主负责接到 Godot 输出或别的通道。</summary>
public interface IFeedbackLog
{
    void Info(string message);

    void Warning(string message);

    void Error(string message);
}

/// <summary>丢弃全部日志的实现（默认）。</summary>
public sealed class NullFeedbackLog : IFeedbackLog
{
    public static readonly NullFeedbackLog Instance = new();

    private NullFeedbackLog()
    {
    }

    public void Info(string message)
    {
    }

    public void Warning(string message)
    {
    }

    public void Error(string message)
    {
    }
}

/// <summary>进程内令牌缓存（默认）。</summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private CachedAccessToken? _token;

    public Task<CachedAccessToken?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_token);

    public Task SaveAsync(CachedAccessToken token, CancellationToken cancellationToken)
    {
        _token = token;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        _token = null;
        return Task.CompletedTask;
    }
}

/// <summary>永远出不了票的默认提供者：未接 Steam 时登录失败得干净、可诊断。</summary>
public sealed class UnavailableTicketProvider : ITicketProvider
{
    public static readonly UnavailableTicketProvider Instance = new();

    private UnavailableTicketProvider()
    {
    }

    public Task<string?> GetWebApiTicketAsync(string identity, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}
