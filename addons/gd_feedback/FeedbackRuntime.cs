using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GdFeedback;

/// <summary>
/// 插件配置（引擎无关）。宿主用 <c>FeedbackConfig</c> 这个 Resource 填充它。
/// </summary>
/// <param name="BaseUrl">反馈服务地址，例如 <c>http://localhost:5087</c>；必须是绝对的 http/https 地址。</param>
/// <param name="Identity">
/// Steam 票据 identity，必须与服务端 <c>Steam:Identity</c> 一致（默认 <c>feedback-api</c>）。
/// 只有宿主出票时用得到，但放在这里是为了让两端不一致时能立刻发现。
/// </param>
/// <param name="RequestTimeoutSeconds">单次请求超时（秒），必须为正数。</param>
/// <param name="AllowDebugLogin">
/// 调试登录开关：为 true 且提供 <paramref name="DebugSteamId"/> 时跳过 Steam 票据。
/// 服务端对应的 <c>Steam:DebugSkipTicketValidation</c> 只在 Development 生效；这里默认关闭。
/// </param>
/// <param name="DebugSteamId">调试登录声明的 SteamID64（17 位数字），仅在调试登录开启时使用。</param>
/// <param name="Proxy">显式代理地址；空表示使用 .NET 默认策略（Windows 会读用户/WPAD 代理，Linux 默认绕过）。</param>
public sealed record FeedbackOptions(
    string BaseUrl,
    string Identity = "feedback-api",
    double RequestTimeoutSeconds = 15,
    bool AllowDebugLogin = false,
    string? DebugSteamId = null,
    string? Proxy = null);

/// <summary>
/// 玩家 API 客户端。引擎无关：不引用 Godot 类型，可直接用纯 .NET 测试驱动。
/// 认证材料（票据、访问令牌）只存在于本类内部，绝不进入日志。
/// </summary>
public sealed class FeedbackRuntime : IDisposable
{
    /// <summary>令牌到期前提前放弃的余量，与服务端 ClockSkew 对齐。</summary>
    private static readonly TimeSpan TokenExpirySkew = TimeSpan.FromSeconds(30);

    /// <summary>仅在传输失败时使用的单次退避；429 不自动重试（见 ADR-0004）。</summary>
    private static readonly TimeSpan TransportRetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>请求体省略 null 字段；响应解析用同一套 web 命名规则。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly FeedbackOptions _options;
    private readonly ITicketProvider _ticketProvider;
    private readonly ITokenStore _tokenStore;
    private readonly IFeedbackLog _log;
    private readonly TimeProvider _timeProvider;

    private readonly HttpClient? _client;
    private readonly HttpMessageHandler? _ownedHandler;
    private readonly FeedbackFailure? _configurationFailure;
    private bool _disposed;

    public FeedbackRuntime(
        FeedbackOptions options,
        ITicketProvider? ticketProvider = null,
        ITokenStore? tokenStore = null,
        IFeedbackLog? log = null,
        HttpMessageHandler? messageHandler = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _ticketProvider = ticketProvider ?? UnavailableTicketProvider.Instance;
        _log = log ?? NullFeedbackLog.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // 令牌只交给宿主注入的存储；默认实现只在内存里保存，落盘与否由宿主决定。
        _tokenStore = tokenStore ?? new InMemoryTokenStore();

        _configurationFailure = ValidateConfiguration(options, out Uri? baseUri);
        if (_configurationFailure is not null)
        {
            return;
        }

        _ownedHandler = messageHandler is null ? CreateDefaultHandler(options.Proxy) : null;
        _client = new HttpClient(messageHandler ?? _ownedHandler!, disposeHandler: false)
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
        };
    }

    /// <summary>用 Steam 票据（或调试 SteamID）换取访问令牌。</summary>
    public async Task<FeedbackResult<PlayerSession>> LoginAsync(CancellationToken cancellationToken = default)
    {
        if (_configurationFailure is not null)
        {
            return FeedbackResult<PlayerSession>.Fail(_configurationFailure);
        }

        string? ticket = null;
        string? debugSteamId = null;

        if (_options.AllowDebugLogin && !string.IsNullOrWhiteSpace(_options.DebugSteamId))
        {
            if (!PlayerFeedbackValidation.IsValidSteamId64(_options.DebugSteamId))
            {
                return FeedbackResult<PlayerSession>.Fail(new FeedbackFailure(
                    FeedbackErrorCode.InvalidSteamId,
                    "debug SteamID64 must be 17 digits and at least 76561197960265728",
                    0,
                    false));
            }
            debugSteamId = _options.DebugSteamId;
            _log.Warning("debug login is enabled; the Steam ticket is not used");
        }
        else
        {
            try
            {
                ticket = await _ticketProvider.GetWebApiTicketAsync(_options.Identity, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error($"ticket provider failed: {ex.GetType().Name}");
                return FeedbackResult<PlayerSession>.Fail(new FeedbackFailure(
                    FeedbackErrorCode.TicketUnavailable,
                    "the ticket provider failed",
                    0,
                    false));
            }

            if (string.IsNullOrWhiteSpace(ticket))
            {
                return FeedbackResult<PlayerSession>.Fail(new FeedbackFailure(
                    FeedbackErrorCode.TicketUnavailable,
                    "no Steam ticket is available",
                    0,
                    false));
            }
            if (ticket.Length > MaxTicketSanityLength)
            {
                return FeedbackResult<PlayerSession>.Fail(new FeedbackFailure(
                    FeedbackErrorCode.TicketInvalid,
                    $"the Steam ticket is implausibly long ({ticket.Length} characters); "
                    + "a Steam web API ticket is at most 2560 bytes, i.e. 5120 hex characters",
                    0,
                    false));
            }
        }

        FeedbackResult<HttpResponseMessage> sent = await SendWithRetryAsync(
            () => JsonRequest(HttpMethod.Post, "api/auth/steam", new LoginRequest(ticket, debugSteamId)),
            cancellationToken);
        if (!sent.Succeeded)
        {
            return FeedbackResult<PlayerSession>.Fail(sent.Failure!);
        }

        using HttpResponseMessage response = sent.Value!;
        if (response.StatusCode != HttpStatusCode.OK)
        {
            FeedbackFailure failure = await DescribeFailureAsync(response, cancellationToken);
            // 登录端点的 401/400 语义比通用映射更具体：出票或验票问题，与"令牌过期"无关。
            // 但"Steam 压根没验成"（服务端 code=steam_unavailable）要保留为可重试的独立错误码。
            failure = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized when failure.Code != FeedbackErrorCode.SteamVerificationUnavailable => failure with
                {
                    Code = FeedbackErrorCode.SteamVerificationRejected,
                    Message = WithDetail("Steam rejected the ticket", failure.Detail),
                },
                HttpStatusCode.BadRequest => failure with
                {
                    Code = FeedbackErrorCode.TicketInvalid,
                    Message = WithDetail("the server rejected the ticket", failure.Detail),
                },
                _ => failure,
            };
            _log.Warning($"login failed with status {failure.StatusCode} ({failure.Code}){DetailForLog(failure)}");
            return FeedbackResult<PlayerSession>.Fail(failure);
        }

        FeedbackResult<PlayerSession> parsed = await ReadAsync<PlayerSession>(response, cancellationToken);
        PlayerSession? session = parsed.Value;
        if (session is not null && !string.IsNullOrWhiteSpace(session.AccessToken))
        {
            await _tokenStore.SaveAsync(
                new CachedAccessToken(session.Player.SteamId, session.AccessToken, session.ExpiresAtUtc),
                cancellationToken);
            _log.Info($"login succeeded for steamId {session.Player.SteamId}");
            return parsed;
        }

        return parsed.Succeeded
            ? FeedbackResult<PlayerSession>.Fail(InvalidResponse("login response did not contain an access token"))
            : parsed;
    }

    /// <summary>创建反馈。</summary>
    public async Task<FeedbackResult<PlayerFeedback>> SubmitAsync(
        PlayerFeedbackDraft draft,
        CancellationToken cancellationToken = default)
    {
        string? validationError = PlayerFeedbackValidation.Validate(draft);
        if (validationError is not null)
        {
            return FeedbackResult<PlayerFeedback>.Fail(ValidationFailure(validationError));
        }

        // 自动采集字段（cpu / memoryTotalMb）只做归一化，不做致命校验：越界就丢弃，
        // 绝不因为玩家的机器信息让整条反馈提交失败。规则与服务端一致。
        PlayerFeedbackDraft normalized = draft with
        {
            Cpu = PlayerFeedbackValidation.NormalizeCpu(draft.Cpu),
            MemoryTotalMb = PlayerFeedbackValidation.NormalizeMemoryTotalMb(draft.MemoryTotalMb),
        };

        FeedbackResult<HttpResponseMessage> sent = await SendAuthorizedAsync(
            accessToken => JsonRequest(
                HttpMethod.Post,
                "api/feedback",
                new CreateFeedbackRequest(
                    normalized.Type.ToString(),
                    normalized.Title,
                    normalized.Content,
                    normalized.GameVersion,
                    normalized.BuildNumber,
                    normalized.OperatingSystem,
                    normalized.Gpu,
                    normalized.Cpu,
                    normalized.MemoryTotalMb,
                    normalized.Locale,
                    normalized.Map,
                    normalized.Character),
                accessToken),
            cancellationToken);

        return await MapAsync<PlayerFeedback>(sent, HttpStatusCode.Created, cancellationToken);
    }

    /// <summary>自己最近 100 条反馈（服务端按最新在前返回）。</summary>
    public async Task<FeedbackResult<IReadOnlyList<PlayerFeedback>>> ListMineAsync(CancellationToken cancellationToken = default)
    {
        FeedbackResult<HttpResponseMessage> sent = await SendAuthorizedAsync(
            accessToken => JsonRequest(HttpMethod.Get, "api/feedback/mine", body: null, accessToken),
            cancellationToken);

        FeedbackResult<List<PlayerFeedback>> mapped = await MapAsync<List<PlayerFeedback>>(sent, HttpStatusCode.OK, cancellationToken);
        return mapped.Value is { } items
            ? FeedbackResult<IReadOnlyList<PlayerFeedback>>.Ok(items)
            : FeedbackResult<IReadOnlyList<PlayerFeedback>>.Fail(mapped.Failure!);
    }

    /// <summary>反馈详情（含评论）。他人或不存在一律得到 <see cref="FeedbackErrorCode.NotFound"/>。</summary>
    public async Task<FeedbackResult<PlayerFeedbackDetail>> GetDetailAsync(int feedbackId, CancellationToken cancellationToken = default)
    {
        FeedbackResult<HttpResponseMessage> sent = await SendAuthorizedAsync(
            accessToken => JsonRequest(HttpMethod.Get, $"api/feedback/{feedbackId}", body: null, accessToken),
            cancellationToken);

        return await MapAsync<PlayerFeedbackDetail>(sent, HttpStatusCode.OK, cancellationToken);
    }

    /// <summary>在自己的反馈上追加评论。</summary>
    public async Task<FeedbackResult<PlayerFeedbackComment>> AddCommentAsync(
        int feedbackId,
        string? content,
        CancellationToken cancellationToken = default)
    {
        string? validationError = PlayerFeedbackValidation.ValidateComment(content);
        if (validationError is not null)
        {
            return FeedbackResult<PlayerFeedbackComment>.Fail(ValidationFailure(validationError));
        }

        FeedbackResult<HttpResponseMessage> sent = await SendAuthorizedAsync(
            accessToken => JsonRequest(
                HttpMethod.Post,
                $"api/feedback/{feedbackId}/comments",
                new CreateCommentRequest(content),
                accessToken),
            cancellationToken);

        return await MapAsync<PlayerFeedbackComment>(sent, HttpStatusCode.Created, cancellationToken);
    }

    /// <summary>丢弃内存与宿主存储中的访问令牌（例如玩家登出或切换账号）。</summary>
    public Task ClearSessionAsync(CancellationToken cancellationToken = default) => _tokenStore.ClearAsync(cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _client?.Dispose();
        _ownedHandler?.Dispose();
    }

    /// <summary>
    /// 客户端的**合理性上限**，不是服务端策略：限额由服务端定（目前 hex 8192），这里只挡
    /// "把一整块 JSON/返回值当票据传进来"这类宿主 bug。故意取得比服务端宽松——曾经跟着服务端
    /// 一起写成 4096，结果把 5120 字符的真实 Steam 票据（票据上限 2560 字节 → hex 5120）挡在门外。
    /// </summary>
    private const int MaxTicketSanityLength = 16 * 1024;

    /// <summary>服务端在 ProblemDetails 扩展成员 <c>code</c> 里声明的"Steam 没验成"（网络/超时/凭据没配）。</summary>
    private const string SteamUnavailableCode = "steam_unavailable";

    private static FeedbackFailure? ValidateConfiguration(FeedbackOptions options, out Uri? baseUri)
    {
        baseUri = null;
        if (string.IsNullOrWhiteSpace(options.BaseUrl)
            || !Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return new FeedbackFailure(
                FeedbackErrorCode.InvalidBaseUrl,
                "BaseUrl must be an absolute http or https address",
                0,
                false);
        }
        if (string.IsNullOrWhiteSpace(options.Identity))
        {
            return new FeedbackFailure(
                FeedbackErrorCode.InvalidConfiguration,
                "Identity must match the server's Steam:Identity and must not be empty",
                0,
                false);
        }
        if (options.RequestTimeoutSeconds <= 0)
        {
            return new FeedbackFailure(
                FeedbackErrorCode.InvalidConfiguration,
                "RequestTimeoutSeconds must be positive",
                0,
                false);
        }

        // 必须带尾斜杠，否则 HttpClient 会把相对路径的末段替换掉。
        string normalized = parsed.AbsoluteUri.EndsWith('/') ? parsed.AbsoluteUri : parsed.AbsoluteUri + "/";
        baseUri = new Uri(normalized, UriKind.Absolute);
        return null;
    }

    private static HttpMessageHandler CreateDefaultHandler(string? proxy)
    {
        HttpClientHandler handler = new();
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }
        return handler;
    }

    private async Task<FeedbackResult<HttpResponseMessage>> SendAuthorizedAsync(
        Func<string, HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        if (_configurationFailure is not null)
        {
            return FeedbackResult<HttpResponseMessage>.Fail(_configurationFailure);
        }

        (string? accessToken, FeedbackFailure? authFailure) = await EnsureAccessTokenAsync(cancellationToken);
        if (accessToken is null)
        {
            return FeedbackResult<HttpResponseMessage>.Fail(authFailure!);
        }

        FeedbackResult<HttpResponseMessage> first = await SendWithRetryAsync(() => requestFactory(accessToken), cancellationToken);
        if (!first.Succeeded || first.Value!.StatusCode != HttpStatusCode.Unauthorized)
        {
            return first;
        }

        // 令牌过期或被吊销：清掉缓存、重新登录一次，且只重试一次。
        first.Value.Dispose();
        await _tokenStore.ClearAsync(cancellationToken);
        FeedbackResult<PlayerSession> relogin = await LoginAsync(cancellationToken);
        if (!relogin.Succeeded)
        {
            return FeedbackResult<HttpResponseMessage>.Fail(relogin.Failure!);
        }

        _log.Info("the feedback API rejected the access token; re-authenticated once and retried");
        return await SendWithRetryAsync(() => requestFactory(relogin.Value!.AccessToken), cancellationToken);
    }

    private async Task<(string? AccessToken, FeedbackFailure? Failure)> EnsureAccessTokenAsync(CancellationToken cancellationToken)
    {
        CachedAccessToken? cached = await _tokenStore.LoadAsync(cancellationToken);
        if (cached is not null && cached.ExpiresAtUtc - TokenExpirySkew > _timeProvider.GetUtcNow())
        {
            return (cached.AccessToken, null);
        }

        FeedbackResult<PlayerSession> login = await LoginAsync(cancellationToken);
        return login.Succeeded
            ? (login.Value!.AccessToken, null)
            : (null, login.Failure!);
    }

    private static HttpRequestMessage JsonRequest(HttpMethod method, string path, object? body, string? accessToken = null)
    {
        HttpRequestMessage request = new(method, path);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        return request;
    }

    private async Task<FeedbackResult<HttpResponseMessage>> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                HttpResponseMessage response = await _client!.SendAsync(
                    requestFactory(),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                return FeedbackResult<HttpResponseMessage>.Ok(response);
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                if (attempt >= 1)
                {
                    // 只记录异常类型：请求里可能带令牌与玩家文本。
                    _log.Warning($"the feedback API could not be reached ({ex.GetType().Name})");
                    return FeedbackResult<HttpResponseMessage>.Fail(new FeedbackFailure(
                        FeedbackErrorCode.TransportFailed,
                        "the feedback API could not be reached",
                        0,
                        true));
                }
                await Task.Delay(TransportRetryDelay, cancellationToken);
            }
        }
    }

    private async Task<FeedbackResult<T>> MapAsync<T>(
        FeedbackResult<HttpResponseMessage> sent,
        HttpStatusCode expected,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!sent.Succeeded)
        {
            return FeedbackResult<T>.Fail(sent.Failure!);
        }

        using HttpResponseMessage response = sent.Value!;
        if (response.StatusCode != expected)
        {
            FeedbackFailure failure = await DescribeFailureAsync(response, cancellationToken);
            _log.Warning($"the feedback API returned {failure.StatusCode} ({failure.Code})");
            return FeedbackResult<T>.Fail(failure);
        }

        return await ReadAsync<T>(response, cancellationToken);
    }

    private static async Task<FeedbackResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            T? value = await JsonSerializer.DeserializeAsync<T>(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                JsonOptions,
                cancellationToken);
            return value is null
                ? FeedbackResult<T>.Fail(InvalidResponse("the response body was empty"))
                : FeedbackResult<T>.Ok(value);
        }
        catch (JsonException)
        {
            return FeedbackResult<T>.Fail(InvalidResponse("the response body was not the expected JSON shape"));
        }
    }

    private static async Task<FeedbackFailure> DescribeFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        int statusCode = (int)response.StatusCode;
        ProblemDetails? problem = null;
        try
        {
            problem = await JsonSerializer.DeserializeAsync<ProblemDetails>(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            // 400/401/404 未必带 ProblemDetails；没有正文时按状态码兜底。
        }

        string? detail = string.IsNullOrWhiteSpace(problem?.Detail) ? problem?.Title : problem?.Detail;
        string? serverCode = problem?.Code;

        // 服务端在 ProblemDetails 的扩展成员 code 里声明原因（见 AuthEndpoints）；
        // steam_unavailable 表示"Steam 没给出结论"（网络/超时/凭据没配），与票据是否有效无关。
        if (response.StatusCode == HttpStatusCode.Unauthorized
            && string.Equals(serverCode, SteamUnavailableCode, StringComparison.Ordinal))
        {
            return new FeedbackFailure(
                FeedbackErrorCode.SteamVerificationUnavailable,
                WithDetail("Steam could not verify the ticket right now", detail),
                statusCode,
                true,
                detail);
        }

        return response.StatusCode switch
        {
            HttpStatusCode.BadRequest => string.IsNullOrWhiteSpace(detail)
                ? new FeedbackFailure(FeedbackErrorCode.ServerRejected, "the server rejected the request", statusCode, false)
                : new FeedbackFailure(FeedbackErrorCode.ValidationFailed, "the server rejected the request as invalid", statusCode, false, detail),
            HttpStatusCode.Unauthorized => new FeedbackFailure(FeedbackErrorCode.Unauthorized, "the access token was rejected", statusCode, false, detail),
            HttpStatusCode.NotFound => new FeedbackFailure(FeedbackErrorCode.NotFound, "the feedback does not exist or does not belong to this player", statusCode, false, detail),
            HttpStatusCode.TooManyRequests => new FeedbackFailure(FeedbackErrorCode.RateLimited, "the player hit a rate limit; retry later", statusCode, true, detail),
            >= HttpStatusCode.InternalServerError => new FeedbackFailure(FeedbackErrorCode.ServerError, "the feedback server failed", statusCode, true, detail),
            _ => new FeedbackFailure(FeedbackErrorCode.ServerRejected, "the server rejected the request", statusCode, false, detail),
        };
    }

    /// <summary>把服务端给出的原因并进人类可读文案，宿主的日志与 UI 就能直接看到为什么失败。</summary>
    private static string WithDetail(string message, string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? message : $"{message}: {detail}";

    private static string DetailForLog(FeedbackFailure failure) =>
        string.IsNullOrWhiteSpace(failure.Detail) ? string.Empty : $" — {failure.Detail}";

    private static FeedbackFailure ValidationFailure(string detail) =>
        new(FeedbackErrorCode.ValidationFailed, "the request was rejected before it was sent", 0, false, detail);

    private static FeedbackFailure InvalidResponse(string detail) =>
        new(FeedbackErrorCode.InvalidResponse, "the feedback server returned an unexpected response", 0, false, detail);

    // ---- 线上形状（与服务端 Contracts/Requests、Contracts/Responses 一一对应）----

    internal sealed record LoginRequest(string? Ticket, string? DebugSteamId);

    internal sealed record CreateFeedbackRequest(
        string Type,
        string? Title,
        string? Content,
        string? GameVersion,
        string? BuildNumber,
        string? OperatingSystem,
        string? Gpu,
        string? Cpu,
        int? MemoryTotalMb,
        string? Locale,
        string? Map,
        string? Character);

    internal sealed record CreateCommentRequest(string? Content);

    internal sealed record ProblemDetails(string? Type, string? Title, int? Status, string? Detail, string? Code);
}
