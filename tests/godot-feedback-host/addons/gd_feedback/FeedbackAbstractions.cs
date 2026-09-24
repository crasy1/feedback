namespace GdFeedback;

/// <summary>
/// 宿主提供的 Steam 票据来源。插件本身不引用任何 Steam 绑定（见 ADR-0004）：
/// 宿主用 Facepunch 等实现本接口，交出**十六进制**票据串，或返回 null 表示无法出票。
/// </summary>
public interface ITicketProvider
{
    /// <param name="identity">
    /// 票据 identity，必须与服务端 <c>Steam:Identity</c> 一致（默认 feedback-api）。
    /// 宿主应把它原样传给 <c>SteamUser.GetAuthTicketForWebApi</c>。
    /// </param>
    Task<string?> GetWebApiTicketAsync(string identity, CancellationToken cancellationToken);
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
