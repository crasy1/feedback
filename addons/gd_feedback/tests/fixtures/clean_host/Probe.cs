using Godot;
using GdFeedback;

/// <summary>
/// 可选的 Godot 引擎内探针（由 tests/verify.py 在给了 -GodotPath 时运行）。
/// 它验证四件只能在引擎里验证的事：
///
///   1. 适配器的回主线程路径：Runtime 的结果经 CallDeferred 回到主线程后发出 feedback_failed 信号。
///      （第一阶段的空标题必然在本地校验就被挡下，所以完全不联网。）
///   2. System.Net.Http 在 Godot 运行时里真的能发请求：第二阶段打开调试登录、把 BaseUrl 指向
///      127.0.0.1:1（必然连不上），断言拿到的是被正确映射的传输/服务端失败，而不是异常或卡死。
///   3. <c>FeedbackConfig.SteamAppId</c> 真的参与路径推导：第三阶段给一个不像 AppID 的值，断言本地
///      就以 invalid_configuration 失败。这个字段没被读到的话，这里会得到 transport_failed ——
///      两种结果可分辨，所以这条断言是真有内容的。
///   4. 配置留空时宿主注入的 <c>IGameAppIdProvider</c> 仍然生效：第四阶段同一个坏值只从 provider 来。
///
/// 第二阶段的错误码刻意宽松：transport_failed（连不上/超时）与 server_error（例如中间有代理返回 5xx）
/// 都算通过 —— 那一阶段要证明的是"请求真的发出去了并且失败被正确分类"，不是某一种失败。
/// </summary>
public partial class Probe : Node
{
    /// <summary>墙钟预算（毫秒）。用真实时间而不是帧数：headless 下帧率不可预期。</summary>
    private const ulong BudgetMilliseconds = 20_000;

    /// <summary>保留端口 1：本机必然拒连，1 秒超时把等待压到最短。</summary>
    private const string UnreachableBaseUrl = "http://127.0.0.1:1";

    /// <summary>不像 AppID 的值（例如误抄成 slug）：必须本地 fail closed，而不是拼出一个必然 404 的路径。</summary>
    private const string NotAnAppId = "neon-drift";

    private FeedbackClient _client = null!;
    private ulong _startedAt;
    private int _phase;
    private bool _finished;

    public override void _Ready()
    {
        _startedAt = Time.GetTicksMsec();

        _client = new FeedbackClient();
        AddChild(_client);
        _client.FeedbackFailed += OnFeedbackFailed;
        _client.FeedbackLoggedIn += (_, _) => Finish(1, "GD_FEEDBACK_PROBE FAIL login unexpectedly succeeded");

        _phase = 1;
        _client.Configure(new FeedbackConfig { BaseUrl = UnreachableBaseUrl, RequestTimeoutSeconds = 1.0 });
        _ = _client.SubmitAsync(new PlayerFeedbackDraft(PlayerFeedbackType.Bug, string.Empty, "probe content"));
    }

    public override void _Process(double delta)
    {
        if (_finished)
        {
            return;
        }
        if (Time.GetTicksMsec() - _startedAt > BudgetMilliseconds)
        {
            Finish(1, $"GD_FEEDBACK_PROBE FAIL timed out in phase {_phase}");
        }
    }

    /// <summary>第二阶段：走真实 HTTP。调试登录跳过票据，端点必然连不上，用来证明请求确实发出去了。</summary>
    private void StartRealHttpPhase()
    {
        _phase = 2;
        _client.Configure(new FeedbackConfig
        {
            BaseUrl = UnreachableBaseUrl,
            RequestTimeoutSeconds = 1.0,
            AllowDebugLogin = true,
            DebugSteamId = "76561197960265729",
        });
        _ = _client.LoginAsync();
    }

    /// <summary>
    /// 第三阶段：AppID 写在配置里（也就是宿主配置资源 <c>feedback_config.tres</c> / <c>feedback.tres</c> 的那个字段）。
    /// 配置里给的是坏值，期望的是<b>本地</b> invalid_configuration，且不发出任何请求。
    /// </summary>
    private void StartConfiguredAppIdPhase()
    {
        _phase = 3;
        _client.Configure(new FeedbackConfig
        {
            BaseUrl = UnreachableBaseUrl,
            RequestTimeoutSeconds = 1.0,
            SteamAppId = NotAnAppId,
        });
        _ = _client.LoginAsync();
    }

    /// <summary>第四阶段：配置留空时退回宿主注入的来源。同一个坏值，同样应当在本地被挡下。</summary>
    private void StartProviderFallbackPhase()
    {
        _phase = 4;
        _client.Configure(
            new FeedbackConfig { BaseUrl = UnreachableBaseUrl, RequestTimeoutSeconds = 1.0 },
            gameAppIdProvider: new StubAppIdProvider(NotAnAppId));
        _ = _client.LoginAsync();
    }

    private void OnFeedbackFailed(int statusCode, string errorCode, string message, bool retryable)
    {
        switch (_phase)
        {
            case 1:
                if (errorCode != FeedbackErrorCode.ValidationFailed)
                {
                    Finish(1, $"GD_FEEDBACK_PROBE FAIL phase 1 expected validation_failed, got {errorCode}");
                    return;
                }

                StartRealHttpPhase();
                return;

            case 2:
                if (errorCode is not (FeedbackErrorCode.TransportFailed or FeedbackErrorCode.ServerError))
                {
                    Finish(1, $"GD_FEEDBACK_PROBE FAIL phase 2 expected a transport failure, got {errorCode} (status {statusCode}, retryable {retryable})");
                    return;
                }

                StartConfiguredAppIdPhase();
                return;

            case 3:
                if (errorCode != FeedbackErrorCode.InvalidConfiguration)
                {
                    Finish(1, $"GD_FEEDBACK_PROBE FAIL phase 3 expected invalid_configuration from FeedbackConfig.SteamAppId, got {errorCode} (status {statusCode})");
                    return;
                }

                StartProviderFallbackPhase();
                return;

            case 4:
            {
                bool ok = errorCode == FeedbackErrorCode.InvalidConfiguration;
                Finish(
                    ok ? 0 : 1,
                    ok
                        ? "GD_FEEDBACK_PROBE PASS"
                        : $"GD_FEEDBACK_PROBE FAIL phase 4 expected invalid_configuration from the injected provider, got {errorCode} (status {statusCode})");
                return;
            }
        }
    }

    private void Finish(int exitCode, string message)
    {
        _finished = true;
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

    /// <summary>只返回一个常量的 AppID 来源，用来验证"配置留空才问宿主"。</summary>
    private sealed class StubAppIdProvider(string? appId) : IGameAppIdProvider
    {
        public string? GetSteamAppId() => appId;
    }
}
