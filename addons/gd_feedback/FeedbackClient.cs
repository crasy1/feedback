using System.Globalization;
using Godot;

namespace GdFeedback;

/// <summary>
/// 玩家反馈客户端（Godot 适配器）。职责刻意很薄：解析配置、把工作交给引擎无关的
/// <see cref="FeedbackRuntime"/>，并在结果回来后用 CallDeferred 回到主线程再发信号。
/// Godot 只保证 CallDeferred/SetDeferred 是从后台线程回到主线程的手段，所以这里不做任何
/// "await 之后直接碰节点/发信号"的事（见 ADR-0004）。
/// </summary>
public partial class FeedbackClient : Node
{
    [Signal]
    public delegate void FeedbackLoggedInEventHandler(string steamId, string steamName);

    [Signal]
    public delegate void FeedbackSubmittedEventHandler(int feedbackId);

    [Signal]
    public delegate void FeedbackCommentAddedEventHandler(int feedbackId, int commentId);

    [Signal]
    public delegate void FeedbackMineLoadedEventHandler(Godot.Collections.Array items);

    [Signal]
    public delegate void FeedbackDetailLoadedEventHandler(Godot.Collections.Dictionary detail);

    [Signal]
    public delegate void FeedbackFailedEventHandler(int statusCode, string errorCode, string message, bool retryable);

    private FeedbackRuntime? _runtime;
    private FeedbackConfig? _config;
    private CancellationTokenSource? _lifetime;

    /// <summary>
    /// 宿主注入的票据来源。未注入时登录会得到 <c>ticket_unavailable</c>，
    /// 不会退回任何"看起来成功"的路径（fail closed）。
    /// </summary>
    public ITicketProvider TicketProvider { get; set; } = UnavailableTicketProvider.Instance;

    /// <summary>当前使用的配置；未显式设置时在 <c>_Ready</c> 里按约定路径加载。</summary>
    public FeedbackConfig? Config => _config;

    public override void _Ready()
    {
        _lifetime = new CancellationTokenSource();
        _config ??= FeedbackConfig.Load();
    }

    public override void _ExitTree()
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        _runtime?.Dispose();
        _runtime = null;
    }

    /// <summary>替换配置/票据来源；下次调用操作时重建内部运行时。</summary>
    public void Configure(FeedbackConfig? config = null, ITicketProvider? ticketProvider = null)
    {
        if (config is not null)
        {
            _config = config;
        }
        if (ticketProvider is not null)
        {
            TicketProvider = ticketProvider;
        }
        _runtime?.Dispose();
        _runtime = null;
    }

    /// <summary>登录（票据换访问令牌）。成功后发 <c>feedback_logged_in</c>。</summary>
    public async Task<FeedbackResult<PlayerSession>> LoginAsync()
    {
        FeedbackResult<PlayerSession> result = await GetRuntime().LoginAsync(Token());
        PlayerSession? session = result.Value;
        if (session is not null)
        {
            EmitDeferred(() => EmitSignal(
                SignalName.FeedbackLoggedIn,
                session.Player.SteamId,
                session.Player.SteamName ?? string.Empty));
        }
        EmitFailureIfAny(result.Failure);
        return result;
    }

    /// <summary>提交反馈。成功后发 <c>feedback_submitted</c>。</summary>
    public async Task<FeedbackResult<PlayerFeedback>> SubmitAsync(PlayerFeedbackDraft draft)
    {
        FeedbackResult<PlayerFeedback> result = await GetRuntime().SubmitAsync(draft, Token());
        PlayerFeedback? feedback = result.Value;
        if (feedback is not null)
        {
            EmitDeferred(() => EmitSignal(SignalName.FeedbackSubmitted, feedback.Id));
        }
        EmitFailureIfAny(result.Failure);
        return result;
    }

    /// <summary>
    /// GDScript 友好的提交重载：<paramref name="type"/> 取 "Bug" / "Suggestion" / "Other"，
    /// <paramref name="metadata"/> 用 snake_case 键（game_version、build_number、operating_system、
    /// gpu、locale、map、character）。纯 C# 的 record 无法从 GDScript 构造，所以这里另开一个入口。
    /// </summary>
    public Task<FeedbackResult<PlayerFeedback>> SubmitAsync(
        string? type,
        string? title,
        string? content,
        Godot.Collections.Dictionary? metadata = null)
    {
        if (!TryParseType(type, out PlayerFeedbackType parsed))
        {
            FeedbackResult<PlayerFeedback> rejected = FeedbackResult<PlayerFeedback>.Fail(new FeedbackFailure(
                FeedbackErrorCode.ValidationFailed,
                "the request was rejected before it was sent",
                0,
                false,
                "type must be Bug, Suggestion or Other"));
            EmitFailureIfAny(rejected.Failure);
            return Task.FromResult(rejected);
        }

        return SubmitAsync(new PlayerFeedbackDraft(
            parsed,
            title,
            content,
            Metadata(metadata, "game_version"),
            Metadata(metadata, "build_number"),
            Metadata(metadata, "operating_system"),
            Metadata(metadata, "gpu"),
            Metadata(metadata, "locale"),
            Metadata(metadata, "map"),
            Metadata(metadata, "character")));
    }

    /// <summary>读取自己的反馈列表。成功后发 <c>feedback_mine_loaded</c>。</summary>
    public async Task<FeedbackResult<IReadOnlyList<PlayerFeedback>>> ListMineAsync()
    {
        FeedbackResult<IReadOnlyList<PlayerFeedback>> result = await GetRuntime().ListMineAsync(Token());
        if (result.Value is { } items)
        {
            Godot.Collections.Array payload = ToGodotArray(items);
            EmitDeferred(() => EmitSignal(SignalName.FeedbackMineLoaded, payload));
        }
        EmitFailureIfAny(result.Failure);
        return result;
    }

    /// <summary>读取反馈详情（含评论）。成功后发 <c>feedback_detail_loaded</c>。</summary>
    public async Task<FeedbackResult<PlayerFeedbackDetail>> GetDetailAsync(int feedbackId)
    {
        FeedbackResult<PlayerFeedbackDetail> result = await GetRuntime().GetDetailAsync(feedbackId, Token());
        if (result.Value is { } detail)
        {
            Godot.Collections.Dictionary payload = ToGodotDictionary(detail);
            EmitDeferred(() => EmitSignal(SignalName.FeedbackDetailLoaded, payload));
        }
        EmitFailureIfAny(result.Failure);
        return result;
    }

    /// <summary>追加玩家评论。成功后发 <c>feedback_comment_added</c>。</summary>
    public async Task<FeedbackResult<PlayerFeedbackComment>> AddCommentAsync(int feedbackId, string? content)
    {
        FeedbackResult<PlayerFeedbackComment> result = await GetRuntime().AddCommentAsync(feedbackId, content, Token());
        PlayerFeedbackComment? comment = result.Value;
        if (comment is not null)
        {
            EmitDeferred(() => EmitSignal(SignalName.FeedbackCommentAdded, feedbackId, comment.Id));
        }
        EmitFailureIfAny(result.Failure);
        return result;
    }

    /// <summary>丢弃访问令牌（登出或切换账号）。</summary>
    public Task ClearSessionAsync() => GetRuntime().ClearSessionAsync(Token());

    private CancellationToken Token() => _lifetime?.Token ?? CancellationToken.None;

    private FeedbackRuntime GetRuntime()
    {
        if (_runtime is not null)
        {
            return _runtime;
        }

        _config ??= FeedbackConfig.Load();
        _runtime = new FeedbackRuntime(
            _config.ToOptions(),
            TicketProvider,
            _config.CacheAccessToken ? new UserTokenStore() : new InMemoryTokenStore(),
            new GodotFeedbackLog(_config.VerboseLogging));
        return _runtime;
    }

    /// <summary>把动作排到主线程的空闲时机执行；这就是本插件唯一的回主线程手段。</summary>
    private void EmitDeferred(Action emission)
    {
        if (!IsInstanceValid(this))
        {
            return;
        }
        Callable.From(emission).CallDeferred();
    }

    private void EmitFailureIfAny(FeedbackFailure? failure)
    {
        if (failure is null)
        {
            return;
        }
        FeedbackFailure reported = failure;
        EmitDeferred(() => EmitSignal(
            SignalName.FeedbackFailed,
            reported.StatusCode,
            reported.Code,
            reported.Message,
            reported.Retryable));
    }

    private static Godot.Collections.Array ToGodotArray(IEnumerable<PlayerFeedback> items)
    {
        Godot.Collections.Array array = new();
        foreach (PlayerFeedback item in items)
        {
            array.Add(ToGodotDictionary(item));
        }
        return array;
    }

    private static Godot.Collections.Dictionary ToGodotDictionary(IPlayerFeedbackView item) => new()
    {
        { "id", item.Id },
        { "type", item.Type },
        { "title", item.Title },
        { "content", item.Content },
        { "status", item.Status },
        { "game_version", item.GameVersion ?? string.Empty },
        { "build_number", item.BuildNumber ?? string.Empty },
        { "operating_system", item.OperatingSystem ?? string.Empty },
        { "gpu", item.Gpu ?? string.Empty },
        { "locale", item.Locale ?? string.Empty },
        { "map", item.Map ?? string.Empty },
        { "character", item.Character ?? string.Empty },
        { "created_at", Format(item.CreatedAt) },
    };

    private static Godot.Collections.Dictionary ToGodotDictionary(PlayerFeedbackDetail detail)
    {
        Godot.Collections.Dictionary payload = ToGodotDictionary((IPlayerFeedbackView)detail);
        Godot.Collections.Array comments = new();
        foreach (PlayerFeedbackComment comment in detail.Comments)
        {
            comments.Add(new Godot.Collections.Dictionary
            {
                { "id", comment.Id },
                { "author_type", comment.AuthorType },
                { "content", comment.Content },
                { "created_at", Format(comment.CreatedAt) },
            });
        }
        payload["comments"] = comments;
        return payload;
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>只接受类型名称（与服务端一致：拒绝数字与逗号组合）。</summary>
    private static bool TryParseType(string? value, out PlayerFeedbackType type)
    {
        type = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (PlayerFeedbackType candidate in Enum.GetValues<PlayerFeedbackType>())
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                type = candidate;
                return true;
            }
        }
        return false;
    }

    private static string? Metadata(Godot.Collections.Dictionary? metadata, string key)
    {
        if (metadata is null || !metadata.ContainsKey(key))
        {
            return null;
        }
        string text = metadata[key].AsString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
