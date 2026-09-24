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

    /// <summary>
    /// 当前游戏运行的 Steam AppID 来源。宿主实现了它，插件就自动把玩家 API 的路径补成
    /// <c>/g/{appId}/api/...</c>，接入方不必手抄标识；不实现则沿用"BaseUrl 里自带路径"的老行为。
    /// </summary>
    public IGameAppIdProvider GameAppIdProvider { get; set; } = UnavailableGameAppIdProvider.Instance;

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

    /// <summary>替换配置/票据来源/AppID 来源；下次调用操作时重建内部运行时。</summary>
    public void Configure(
        FeedbackConfig? config = null,
        ITicketProvider? ticketProvider = null,
        IGameAppIdProvider? gameAppIdProvider = null)
    {
        if (config is not null)
        {
            _config = config;
        }
        if (ticketProvider is not null)
        {
            TicketProvider = ticketProvider;
        }
        if (gameAppIdProvider is not null)
        {
            GameAppIdProvider = gameAppIdProvider;
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

    /// <summary>提交反馈。成功后发 <c>feedback_submitted</c>。draft 里没给的环境信息会被自动补上。</summary>
    public async Task<FeedbackResult<PlayerFeedback>> SubmitAsync(PlayerFeedbackDraft draft)
    {
        FeedbackResult<PlayerFeedback> result = await GetRuntime().SubmitAsync(FillEnvironmentInfo(draft), Token());
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
    /// gpu、cpu、memory_total_mb、locale、map、character）。纯 C# 的 record 无法从 GDScript 构造，
    /// 所以这里另开一个入口。环境信息（operating_system / gpu / cpu / memory_total_mb）不传也行，
    /// 本插件会自动采集；显式传了的值优先。
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
            Metadata(metadata, "cpu"),
            MetadataInt(metadata, "memory_total_mb"),
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
            new GodotFeedbackLog(_config.VerboseLogging),
            messageHandler: null,
            timeProvider: null,
            gameAppIdProvider: GameAppIdProvider);
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
        { "cpu", item.Cpu ?? string.Empty },
        { "memory_total_mb", item.MemoryTotalMb ?? 0 },
        { "playtime_minutes", item.PlaytimeMinutes ?? 0 },
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

    /// <summary>
    /// 读一个数值型键。GDScript 只有 float 一个数值类型，所以 int 与 float 都要接受；
    /// 非数值、越界都当"没给"——这个字段越界应当被丢弃，而不是让提交失败。
    /// </summary>
    private static int? MetadataInt(Godot.Collections.Dictionary? metadata, string key)
    {
        if (metadata is null || !metadata.ContainsKey(key))
        {
            return null;
        }

        Variant value = metadata[key];
        if (value.VariantType is not (Variant.Type.Int or Variant.Type.Float))
        {
            return null;
        }

        return PlayerFeedbackValidation.NormalizeMemoryTotalMb((int)value.AsDouble());
    }

    // ---- 环境信息采集（只在这里碰 Godot：核心三件套必须保持引擎无关）----

    /// <summary>
    /// 用本机采集到的环境信息补上 draft 里没给的字段——<b>宿主显式传的值永远优先</b>。
    /// 任何一项取不到都只是留空，绝不影响提交。
    /// </summary>
    private static PlayerFeedbackDraft FillEnvironmentInfo(PlayerFeedbackDraft draft) => draft with
    {
        OperatingSystem = FirstNonEmpty(draft.OperatingSystem, DescribeOperatingSystem()),
        Gpu = FirstNonEmpty(draft.Gpu, DescribeVideoAdapter()),
        Cpu = FirstNonEmpty(draft.Cpu, DescribeProcessor()),
        MemoryTotalMb = draft.MemoryTotalMb ?? ReadTotalMemoryMb(),
    };

    private static string? FirstNonEmpty(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred : (string.IsNullOrWhiteSpace(fallback) ? null : fallback);

    /// <summary>例如 "Windows 11 (build 22631)"；Linux 上再拼发行版名。取不到返回 null。</summary>
    private static string? DescribeOperatingSystem()
    {
        string name = OS.GetName();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string description = name;
        string alias = OS.GetVersionAlias();
        if (!string.IsNullOrWhiteSpace(alias))
        {
            description += " " + alias;
        }

        if (string.Equals(name, "Linux", StringComparison.OrdinalIgnoreCase))
        {
            string distribution = OS.GetDistributionName();
            if (!string.IsNullOrWhiteSpace(distribution))
            {
                description += " (" + distribution + ")";
            }
        }

        return description;
    }

    /// <summary>CPU 型号。Godot 只在 Windows / macOS / Linux / iOS 实现它，Android 与 Web 返回空串——此时留空。</summary>
    private static string? DescribeProcessor()
    {
        string name = OS.GetProcessorName();
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>
    /// 显卡型号。必须用 RenderingServer：Godot 4.x 的 OS 上没有"显卡名"这个 API
    /// （OS.get_video_adapter_driver_info() 返回的是驱动名+版本，且文档警告首次调用可能耗时数秒）。
    /// headless / 服务端构建返回空串，此时留空。
    /// </summary>
    private static string? DescribeVideoAdapter()
    {
        string name = RenderingServer.GetVideoAdapterName();
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>物理内存总量（MB）。Godot 给的 "physical" 是<b>字节</b>；取不到或平台不支持就留空。</summary>
    private static int? ReadTotalMemoryMb()
    {
        try
        {
            Godot.Collections.Dictionary info = OS.GetMemoryInfo();
            if (!info.ContainsKey("physical"))
            {
                return null;
            }

            ulong bytes = info["physical"].AsUInt64();
            if (bytes == 0)
            {
                return null;
            }

            ulong megabytes = bytes / (1024UL * 1024UL);
            return megabytes > int.MaxValue ? null : (int)megabytes;
        }
        catch (Exception)
        {
            // 平台不支持或返回形状变化：留空即可，绝不因为环境信息影响提交。
            return null;
        }
    }
}
