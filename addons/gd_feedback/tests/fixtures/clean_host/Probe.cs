using Godot;
using GdFeedback;

/// <summary>
/// 可选的 Godot 引擎内探针（由 tests/verify.ps1 在给了 -GodotPath 时运行）。
/// 它验证两件只能在引擎里验证的事：
///
///   1. 适配器的回主线程路径：Runtime 的结果经 CallDeferred 回到主线程后发出 feedback_failed 信号。
///      （第一阶段的空标题必然在本地校验就被挡下，所以完全不联网。）
///   2. System.Net.Http 在 Godot 运行时里真的能发请求：第二阶段打开调试登录、把 BaseUrl 指向
///      127.0.0.1:1（必然连不上），断言拿到的是被正确映射的传输/服务端失败，而不是异常或卡死。
///
/// 第二阶段对错误码刻意宽松：transport_failed（连不上/超时）与 server_error（例如中间有代理返回 5xx）
/// 都算通过 —— 这一阶段要证明的是"请求真的发出去了并且失败被正确分类"，不是某一种失败。
/// </summary>
public partial class Probe : Node
{
    /// <summary>墙钟预算（毫秒）。用真实时间而不是帧数：headless 下帧率不可预期。</summary>
    private const ulong BudgetMilliseconds = 20_000;

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
        _client.Configure(new FeedbackConfig { BaseUrl = "http://127.0.0.1:1", RequestTimeoutSeconds = 1.0 });

        _phase = 1;
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

    private void OnFeedbackFailed(int statusCode, string errorCode, string message, bool retryable)
    {
        if (_phase == 1)
        {
            if (errorCode != FeedbackErrorCode.ValidationFailed)
            {
                Finish(1, $"GD_FEEDBACK_PROBE FAIL phase 1 expected validation_failed, got {errorCode}");
                return;
            }

            // 第二阶段：走真实 HTTP。调试登录跳过票据，端点必然连不上，用来证明请求确实发出去了。
            _phase = 2;
            _client.Configure(new FeedbackConfig
            {
                BaseUrl = "http://127.0.0.1:1",
                RequestTimeoutSeconds = 1.0,
                AllowDebugLogin = true,
                DebugSteamId = "76561197960265729",
            });
            _ = _client.LoginAsync();
            return;
        }

        if (_phase == 2)
        {
            if (errorCode is FeedbackErrorCode.TransportFailed or FeedbackErrorCode.ServerError)
            {
                Finish(0, "GD_FEEDBACK_PROBE PASS");
                return;
            }
            Finish(1, $"GD_FEEDBACK_PROBE FAIL phase 2 expected a transport failure, got {errorCode} (status {statusCode}, retryable {retryable})");
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
}
