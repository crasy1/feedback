using Godot;
using GdFeedback;

/// <summary>
/// GD Feedback 的宿主测试台。它把插件当成真实宿主来接：注入 <see cref="ITicketProvider"/>、
/// 用 <see cref="FeedbackConfig"/> 配置、订阅全部信号，并把每一步都打到日志区。
///
/// 两种用法：
///   * 交互模式（默认）：用 Godot 运行 Main.tscn，改 BaseUrl / 登录方式后点按钮联调。
///   * 自检模式：<c>-- --lab-selfcheck</c>，完全离线（不联网、不需要 Steam）验证信号链路与
///     System.Net.Http 在引擎内确实可用，最后打印 GD_FEEDBACK_LAB PASS/FAIL 并设置退出码。
/// </summary>
public partial class FeedbackLab : Control
{
    private const string SelfCheckArgument = "--lab-selfcheck";
    private const ulong SelfCheckBudgetMilliseconds = 20_000;

    /// <summary>
    /// 从宿主工程根部读出来的配置（<c>res://feedback.tres</c>，短名与 <c>res://feedback_config.tres</c> 等价）：
    /// 服务地址与 Steam AppID 都在里面。界面只覆盖本机联调用的那几个字段
    /// （见 <see cref="ApplyConfigFromUi"/>），AppID 不从界面改。
    /// </summary>
    private FeedbackConfig _config = null!;

    private FeedbackClient _client = null!;
    private bool _selfCheck;
    private bool _selfCheckFinished;
    private int _selfCheckPhase;
    private ulong _selfCheckStartedAt;

    private LineEdit _baseUrl = null!;
    private LineEdit _steamId = null!;
    private LineEdit _ticket = null!;
    private CheckBox _useSteamTicket = null!;
    private CheckBox _debugLogin = null!;
    private OptionButton _type = null!;
    private LineEdit _title = null!;
    private TextEdit _content = null!;
    private LineEdit _map = null!;
    private LineEdit _character = null!;
    private LineEdit _feedbackId = null!;
    private LineEdit _comment = null!;
    private RichTextLabel _log = null!;

    public override void _Ready()
    {
        _selfCheck = Array.IndexOf(OS.GetCmdlineUserArgs(), SelfCheckArgument) >= 0;
        // 服务地址与 Steam AppID 都来自这个文件：改了 .tres 就改了接入对象，不必碰代码。
        _config = FeedbackConfig.Load();

        _client = new FeedbackClient { Name = "Feedback" };
        AddChild(_client);
        // 先给一个安全的默认实现；界面搭好后每次操作都会按界面状态重新快照（见 ApplyConfigFromUi）。
        _client.TicketProvider = new LabTicketProvider(useSteam: false, manualTicket: string.Empty, log: LogFromBackground);
        WireSignals();

        if (_selfCheck)
        {
            _selfCheckStartedAt = Time.GetTicksMsec();
            StartSelfCheck();
            return;
        }

        BuildUi();
        Say("宿主测试台已就绪：先确认 BaseUrl，再选登录方式（调试登录 / Steam 出票 / 手输票据），然后点「登录」。");
        Say($"配置来源：{DescribeConfigSource()}（BaseUrl={_config.BaseUrl}，SteamAppId={DescribeAppId()}）");
        Say(LabTicketProvider.Status());
    }

    public override void _Process(double delta)
    {
        if (!_selfCheck || _selfCheckFinished)
        {
            return;
        }
        if (Time.GetTicksMsec() - _selfCheckStartedAt > SelfCheckBudgetMilliseconds)
        {
            Finish(1, $"GD_FEEDBACK_LAB FAIL timed out in phase {_selfCheckPhase}");
        }
    }

    // ------------------------------------------------------------------ 自检

    private void StartSelfCheck()
    {
        // 阶段 0：宿主配置必须真的被读到，并且带着一个合法 AppID。
        // 这是"AppID 由配置提供"这条接线唯一的直接证据——它决定请求会不会走在 /g/{appId}/ 下。
        if (!IsValidAppId(_config.SteamAppId))
        {
            Finish(1, $"GD_FEEDBACK_LAB FAIL phase 0 {DescribeConfigSource()} must carry a numeric SteamAppId, got '{_config.SteamAppId}'");
            return;
        }

        _selfCheckPhase = 1;
        _client.Configure(OfflineConfig());
        _ = _client.SubmitAsync(new PlayerFeedbackDraft(PlayerFeedbackType.Bug, string.Empty, "self check"));
    }

    /// <summary>阶段 2 用调试登录打真实 HTTP：地址必然连不上，用来证明请求确实发得出去。</summary>
    private void StartSelfCheckPhaseTwo()
    {
        _selfCheckPhase = 2;
        FeedbackConfig config = OfflineConfig();
        config.AllowDebugLogin = true;
        config.DebugSteamId = "76561197960265729";
        _client.Configure(config);
        _ = _client.LoginAsync();
    }

    /// <summary>
    /// 在宿主配置（<c>res://feedback.tres</c>）的取值上只把地址换成保留端口 1（本机必然拒连），
    /// 超时压到 1 秒。<c>SteamAppId</c> 刻意原样保留：阶段 2 要证明的正是它被真的用上了
    /// （非法 AppID 会立刻以 <c>invalid_configuration</c> 失败，而不是走到网络）。
    /// </summary>
    private FeedbackConfig OfflineConfig()
    {
        _config.BaseUrl = "http://127.0.0.1:1";
        _config.RequestTimeoutSeconds = 1.0;
        _config.AllowDebugLogin = false;
        return _config;
    }

    /// <summary>与服务端 GameValidation 同口径：纯数字、最多 10 位、不能全 0。</summary>
    private static bool IsValidAppId(string? candidate)
    {
        string trimmed = candidate?.Trim() ?? string.Empty;
        return trimmed.Length is > 0 and <= 10
            && trimmed.All(char.IsAsciiDigit)
            && !trimmed.All(character => character == '0');
    }

    /// <summary>AppID 是公开的地址信息，不是秘密；但"没配"与"配错了"要一眼看得出来。</summary>
    private string DescribeAppId() =>
        IsValidAppId(_config.SteamAppId) ? _config.SteamAppId : $"(未配置或非法：'{_config.SteamAppId}')";

    /// <summary>配置是哪个文件生效的：项目根可能有短名与长名两份，日志里必须说清楚读了哪一份。</summary>
    private string DescribeConfigSource() =>
        string.IsNullOrEmpty(_config.LoadedFromPath) ? "(没有配置文件，用内置默认值)" : _config.LoadedFromPath;

    private void Finish(int exitCode, string message)
    {
        _selfCheckFinished = true;
        if (exitCode == 0)
        {
            GD.Print(message);
        }
        else
        {
            GD.PushError(message);
        }
        GetTree().Quit(exitCode);
    }

    // ------------------------------------------------------------------ 信号

    private void WireSignals()
    {
        _client.FeedbackLoggedIn += OnLoggedIn;
        _client.FeedbackSubmitted += OnSubmitted;
        _client.FeedbackCommentAdded += OnCommentAdded;
        _client.FeedbackMineLoaded += OnMineLoaded;
        _client.FeedbackDetailLoaded += OnDetailLoaded;
        _client.FeedbackFailed += OnFailed;
    }

    private void OnLoggedIn(string steamId, string steamName) => Say($"feedback_logged_in: {steamId} ({steamName})");

    private void OnSubmitted(int feedbackId)
    {
        Say($"feedback_submitted: #{feedbackId}");
        if (!_selfCheck)
        {
            _feedbackId.Text = feedbackId.ToString();
        }
    }

    private void OnCommentAdded(int feedbackId, int commentId) => Say($"feedback_comment_added: feedback #{feedbackId} → comment #{commentId}");

    private void OnMineLoaded(Godot.Collections.Array items)
    {
        Say($"feedback_mine_loaded: {items.Count} 条");
        foreach (Variant item in items)
        {
            Godot.Collections.Dictionary row = item.AsGodotDictionary();
            Say($"    #{row["id"]} [{row["type"]}/{row["status"]}] {row["title"]}");
        }
    }

    private void OnDetailLoaded(Godot.Collections.Dictionary detail)
    {
        Godot.Collections.Array comments = detail.ContainsKey("comments") ? detail["comments"].AsGodotArray() : new Godot.Collections.Array();
        Say($"feedback_detail_loaded: #{detail["id"]} [{detail["type"]}/{detail["status"]}] {detail["title"]}（{comments.Count} 条评论）");
        foreach (Variant item in comments)
        {
            Godot.Collections.Dictionary comment = item.AsGodotDictionary();
            Say($"    - [{comment["author_type"]}] {comment["content"]}");
        }
    }

    private void OnFailed(int statusCode, string errorCode, string message, bool retryable)
    {
        Say($"feedback_failed: {errorCode}（status={statusCode}, retryable={retryable}）{message}");

        if (!_selfCheck)
        {
            return;
        }

        if (_selfCheckPhase == 1)
        {
            if (errorCode != FeedbackErrorCode.ValidationFailed)
            {
                Finish(1, $"GD_FEEDBACK_LAB FAIL phase 1 expected validation_failed, got {errorCode}");
                return;
            }
            StartSelfCheckPhaseTwo();
            return;
        }

        if (_selfCheckPhase == 2)
        {
            // 连不上（transport_failed）或中间有代理返回 5xx（server_error）都算通过：
            // 这一阶段证明的是"请求真的发出去了并且失败被正确分类"。
            bool ok = errorCode is FeedbackErrorCode.TransportFailed or FeedbackErrorCode.ServerError;
            Finish(ok ? 0 : 1, ok ? "GD_FEEDBACK_LAB PASS" : $"GD_FEEDBACK_LAB FAIL phase 2 got {errorCode}");
        }
    }

    // ------------------------------------------------------------------ 界面

    private void BuildUi()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);

        MarginContainer margin = new();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_top", "margin_right", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, 16);
        }
        AddChild(margin);

        VBoxContainer root = new() { SizeFlagsVertical = SizeFlags.ExpandFill };
        margin.AddChild(root);

        root.AddChild(new Label { Text = "GD Feedback 宿主测试台（Godot 4.7.2 / .NET 10）" });
        root.AddChild(new Label
        {
            Text = "插件：addons/gd_feedback（已安装副本）· 服务端契约：docs/specs/player-api.md",
            Modulate = new Color(1, 1, 1, 0.6f),
        });
        root.AddChild(new HSeparator());

        _baseUrl = new LineEdit { Text = _config.BaseUrl, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _baseUrl.TooltipText = "默认取自 res://feedback.tres；这里改只影响本次运行，不回写文件";
        HBoxContainer baseRow = Row("BaseUrl");
        baseRow.AddChild(_baseUrl);
        root.AddChild(baseRow);

        _steamId = new LineEdit { Text = "76561197960265729", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _debugLogin = new CheckBox { Text = "调试登录" };
        _useSteamTicket = new CheckBox { Text = "用 Steam 出票", ButtonPressed = true, TooltipText = "勾上：宿主用 vendored 的 steamworks 插件出票；取消：用下面手输的票据" };
        _ticket = new LineEdit { PlaceholderText = "手输 Steam 票据（GetAuthTicketForWebApi 的十六进制结果；勾了「用 Steam 出票」就忽略这里）", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        HBoxContainer authRow = Row("SteamID64");
        authRow.AddChild(_steamId);
        authRow.AddChild(_debugLogin);
        authRow.AddChild(_useSteamTicket);
        root.AddChild(authRow);
        HBoxContainer ticketRow = Row("票据");
        ticketRow.AddChild(_ticket);
        root.AddChild(ticketRow);

        _type = new OptionButton();
        foreach (string name in Enum.GetNames<PlayerFeedbackType>())
        {
            _type.AddItem(name);
        }
        _title = new LineEdit { PlaceholderText = "标题（1-200 字符）", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _map = new LineEdit { PlaceholderText = "map", CustomMinimumSize = new Vector2(120, 0) };
        _character = new LineEdit { PlaceholderText = "character", CustomMinimumSize = new Vector2(120, 0) };
        HBoxContainer feedbackRow = Row("反馈");
        feedbackRow.AddChild(_type);
        feedbackRow.AddChild(_title);
        feedbackRow.AddChild(_map);
        feedbackRow.AddChild(_character);
        root.AddChild(feedbackRow);

        _content = new TextEdit { PlaceholderText = "正文（1-10000 字符）", CustomMinimumSize = new Vector2(0, 90) };
        root.AddChild(_content);

        HBoxContainer actions = new();
        actions.AddChild(ActionButton("登录", () => _ = _client.LoginAsync()));
        actions.AddChild(ActionButton("提交反馈", SubmitFeedback));
        actions.AddChild(ActionButton("我的反馈", () => _ = _client.ListMineAsync()));
        _feedbackId = new LineEdit { Text = "1", CustomMinimumSize = new Vector2(80, 0) };
        actions.AddChild(new Label { Text = "id" });
        actions.AddChild(_feedbackId);
        actions.AddChild(ActionButton("读取详情", () => _ = _client.GetDetailAsync(CurrentFeedbackId())));
        _comment = new LineEdit { PlaceholderText = "追加评论", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        actions.AddChild(_comment);
        actions.AddChild(ActionButton("追加评论", () => _ = _client.AddCommentAsync(CurrentFeedbackId(), _comment.Text)));
        root.AddChild(actions);

        HBoxContainer probes = new();
        probes.AddChild(ActionButton("健康检查", () => _ = CheckHealthAsync()));
        probes.AddChild(ActionButton("离线自检（校验先于网络）", () =>
            _ = _client.SubmitAsync(new PlayerFeedbackDraft(PlayerFeedbackType.Bug, string.Empty, "probe"))));
        probes.AddChild(ActionButton("清空日志", () => _log.Text = string.Empty));
        root.AddChild(probes);

        _log = new RichTextLabel { SizeFlagsVertical = SizeFlags.ExpandFill, ScrollFollowing = true };
        root.AddChild(_log);
    }

    private static HBoxContainer Row(string label)
    {
        HBoxContainer row = new();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(90, 0) });
        return row;
    }

    private Button ActionButton(string text, Action onPressed)
    {
        Button button = new() { Text = text };
        button.Pressed += () =>
        {
            ApplyConfigFromUi();
            onPressed();
        };
        return button;
    }

    private void SubmitFeedback()
    {
        // 只填游戏语义：环境信息（OS / GPU / CPU / 内存）由插件自动采集，宿主不再手写。
        Godot.Collections.Dictionary metadata = new()
        {
            { "game_version", "0.1.0-lab" },
            { "build_number", "lab" },
            { "locale", OS.GetLocale() },
            { "map", _map.Text },
            { "character", _character.Text },
        };
        _ = _client.SubmitAsync(_type.GetItemText(_type.Selected), _title.Text, _content.Text, metadata);
    }

    private int CurrentFeedbackId() => int.TryParse(_feedbackId.Text.Trim(), out int id) ? id : 0;

    /// <summary>宿主侧直接查一次 /health：先确认栈起来了，再点登录，省得对着 429/超时猜。</summary>
    private async Task CheckHealthAsync()
    {
        string baseUrl = _baseUrl.Text.Trim().TrimEnd('/');
        string message;
        try
        {
            using System.Net.Http.HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
            message = $"/health {await client.GetStringAsync($"{baseUrl}/health")}";
        }
        catch (Exception ex)
        {
            message = $"/health 不可达（{ex.GetType().Name}）：栈起来了吗？docker compose --env-file .env.local up -d --build";
        }

        // 宿主自己也要守主线程规则：await 之后不直接碰节点，用 CallDeferred 回到主线程。
        Callable.From(() => Say(message)).CallDeferred();
    }

    private void ApplyConfigFromUi()
    {
        // 主线程快照界面状态：provider 之后可能在后台线程被调用，那时绝不能碰节点。
        _client.TicketProvider = new LabTicketProvider(
            _useSteamTicket.ButtonPressed,
            _ticket.Text.Trim(),
            LogFromBackground);

        // 界面只覆盖本机联调的那几个字段；SteamAppId 留在 feedback.tres 里，不从界面改
        // （CacheMode.Ignore 加载，改内存里的实例不会回写磁盘）。
        _config.BaseUrl = _baseUrl.Text.Trim();
        _config.Identity = "feedback-api";
        _config.RequestTimeoutSeconds = 15.0;
        _config.AllowDebugLogin = _debugLogin.ButtonPressed;
        _config.DebugSteamId = _steamId.Text.Trim();
        _config.CacheAccessToken = false;
        _config.VerboseLogging = true;
        _client.Configure(_config);
    }

    /// <summary>provider 可能在后台线程被调用，日志必须经 CallDeferred 回到主线程再写节点。</summary>
    private void LogFromBackground(string message) => Callable.From(() => Say(message)).CallDeferred();

    private void Say(string line)
    {
        GD.Print($"[lab] {line}");
        if (_log is not null)
        {
            _log.Text += line + "\n";
        }
    }
}
