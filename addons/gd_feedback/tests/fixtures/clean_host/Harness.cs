// GD Feedback 插件的干净宿主 harness（纯 .NET，不需要 Godot 引擎）。
//
// 由 tests/verify.ps1 展开到临时目录后编译运行。它只用假票据提供者与假 HttpMessageHandler
// 驱动 FeedbackRuntime，验证：本地校验必须先于网络、Steam 验证失败必须 fail closed、
// 令牌缓存与 401 重登、错误码映射、以及"日志绝不出现票据与访问令牌"这条安全不变量。
//
// 引擎无关是本 harness 的前提：它只编译 FeedbackContracts.cs / FeedbackAbstractions.cs /
// FeedbackRuntime.cs —— 这三个文件一旦出现 using Godot 就编译不过。
//
// 输出标记：HARNESS PASS <name> / HARNESS FAIL <name>: <reason>，末尾 HARNESS SUMMARY。
// 退出码：0 = 全部通过，1 = 有失败。
using System.Net;
using System.Text;
using System.Text.Json;
using GdFeedback;

internal static class Program
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly List<string> Failures = [];
    private static int _checks;

    private static async Task<int> Main()
    {
        Console.WriteLine("HARNESS START gd_feedback runtime");

        await Run("validation rejects oversized title before any request", ValidationRejectsOversizedTitleAsync);
        await Run("validation rejects oversized metadata before any request", ValidationRejectsOversizedMetadataAsync);
        await Run("validation rejects oversized comment before any request", ValidationRejectsOversizedCommentAsync);
        await Run("missing ticket fails closed without a request", MissingTicketFailsClosedAsync);
        await Run("oversized ticket is rejected locally", OversizedTicketRejectedAsync);
        await Run("a real 5120-character Steam ticket is accepted", RealWorldTicketLengthIsAcceptedAsync);
        await Run("login sends the ticket and the configured identity", LoginSendsTicketAsync);
        await Run("login 401 maps to steam_verification_rejected", LoginRejectedAsync);
        await Run("login 401 with steam_unavailable is reported as retryable", LoginUnavailableAsync);
        await Run("login 401 with steam_ticket_rejected carries the server reason", LoginRejectedWithReasonAsync);
        await Run("debug login stays off unless enabled", DebugLoginOffByDefaultAsync);
        await Run("debug login sends debugSteamId when enabled", DebugLoginOnSendsSteamIdAsync);
        await Run("debug login rejects a malformed SteamID64", DebugLoginRejectsMalformedSteamIdAsync);
        await Run("submit sends the bearer token and camelCase type", SubmitSendsBearerAndTypeAsync);
        await Run("token is reused across calls", TokenReusedAcrossCallsAsync);
        await Run("expired cached token triggers a new login", ExpiredTokenTriggersLoginAsync);
        await Run("fresh cached token skips login", FreshTokenSkipsLoginAsync);
        await Run("401 triggers exactly one re-login and retry", UnauthorizedTriggersSingleReloginAsync);
        await Run("404 maps to not_found", NotFoundMapsToNotFoundAsync);
        await Run("429 maps to rate_limited without burning another attempt", RateLimitedIsNotRetriedAsync);
        await Run("transport failure is retried once", TransportFailureRetriedOnceAsync);
        await Run("persistent transport failure maps to transport_failed", TransportFailureTwiceAsync);
        await Run("500 maps to server_error", ServerErrorMapsAsync);
        await Run("non-JSON body maps to invalid_response", InvalidJsonMapsAsync);
        await Run("empty body maps to invalid_response", EmptyBodyMapsAsync);
        await Run("server validation detail is preserved", ServerValidationDetailPreservedAsync);
        await Run("token storage is the host's concern and stays in memory by default", TokenStorageIsInjectedAsync);
        await Run("logs never contain the ticket or the access token", LogsNeverContainSecretsAsync);
        await Run("invalid BaseUrl fails fast", InvalidBaseUrlFailsFastAsync);
        await Run("empty identity is rejected", EmptyIdentityRejectedAsync);
        await Run("mine deserializes the list shape", MineDeserializesAsync);
        await Run("detail deserializes comments", DetailDeserializesAsync);
        await Run("comment posts content to the comment route", CommentPostsAsync);
        await Run("base url normalization keeps the api prefix", BaseUrlNormalizationAsync);
        await Run("cancellation surfaces instead of turning into a transport failure", CancellationSurfacesAsync);

        Console.WriteLine($"HARNESS SUMMARY checks={_checks} failed={Failures.Count}");
        return Failures.Count == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- tests

    private static Task ValidationRejectsOversizedTitleAsync()
    {
        StubHandler handler = new();
        using FeedbackRuntime runtime = Runtime(handler);
        FeedbackResult<PlayerFeedback> result = runtime
            .SubmitAsync(new PlayerFeedbackDraft(PlayerFeedbackType.Bug, new string('x', 201), "content"))
            .GetAwaiter().GetResult();
        Check(FailureCode(result) == FeedbackErrorCode.ValidationFailed, "expected validation_failed");
        Check(handler.Requests.Count == 0, "no HTTP request may be sent for an invalid draft");
        return Task.CompletedTask;
    }

    private static Task ValidationRejectsOversizedMetadataAsync()
    {
        StubHandler handler = new();
        using FeedbackRuntime runtime = Runtime(handler);
        FeedbackResult<PlayerFeedback> result = runtime
            .SubmitAsync(new PlayerFeedbackDraft(
                PlayerFeedbackType.Other,
                "title",
                "content",
                GameVersion: new string('v', 65)))
            .GetAwaiter().GetResult();
        Check(FailureCode(result) == FeedbackErrorCode.ValidationFailed, "expected validation_failed");
        Check(handler.Requests.Count == 0, "no HTTP request may be sent for invalid metadata");
        return Task.CompletedTask;
    }

    private static Task ValidationRejectsOversizedCommentAsync()
    {
        StubHandler handler = new();
        using FeedbackRuntime runtime = Runtime(handler);
        FeedbackResult<PlayerFeedbackComment> result = runtime
            .AddCommentAsync(1, new string('c', 5001))
            .GetAwaiter().GetResult();
        Check(FailureCode(result) == FeedbackErrorCode.ValidationFailed, "expected validation_failed");
        Check(handler.Requests.Count == 0, "no HTTP request may be sent for an invalid comment");
        return Task.CompletedTask;
    }

    private static Task MissingTicketFailsClosedAsync()
    {
        StubHandler handler = new();
        using FeedbackRuntime runtime = Runtime(handler, new StubTicketProvider(null));
        FeedbackResult<PlayerSession> result = runtime.LoginAsync().GetAwaiter().GetResult();
        Check(FailureCode(result) == FeedbackErrorCode.TicketUnavailable, "expected ticket_unavailable");
        Check(handler.Requests.Count == 0, "login must not be attempted without a ticket");
        return Task.CompletedTask;
    }

    private static Task OversizedTicketRejectedAsync()
    {
        StubHandler handler = new();
        using FeedbackRuntime runtime = Runtime(handler, new StubTicketProvider(new string('a', (16 * 1024) + 1)));
        FeedbackResult<PlayerSession> result = runtime.LoginAsync().GetAwaiter().GetResult();
        Check(FailureCode(result) == FeedbackErrorCode.TicketInvalid, "expected ticket_invalid");
        Check(handler.Requests.Count == 0, "an oversized ticket must not be sent");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 真机回归：Steam 的 Web API 票据上限是 2560 字节，十六进制后是 5120 字符。
    /// 客户端与 harness 曾跟着服务端写成 4096，把真实票据挡在了本地（真机实测就是这个长度）。
    /// </summary>
    private static Task RealWorldTicketLengthIsAcceptedAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-real"));
        string ticket = new string('a', 5120);
        using FeedbackRuntime runtime = Runtime(handler, new StubTicketProvider(ticket));

        FeedbackResult<PlayerSession> result = runtime.LoginAsync().GetAwaiter().GetResult();

        Check(result.Succeeded, $"a 5120-character ticket must be accepted, got {result.Failure?.Code ?? "none"}");
        Check(handler.Requests.Count == 1, "the login request must be sent");
        Check(handler.Requests[0].Body.Contains(ticket, StringComparison.Ordinal), "the ticket must be forwarded verbatim");
        return Task.CompletedTask;
    }

    private static Task LoginSendsTicketAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-1"));
        StubTicketProvider tickets = new("sentinel-ticket");
        using FeedbackRuntime runtime = Runtime(handler, tickets);

        FeedbackResult<PlayerSession> result = runtime.LoginAsync().GetAwaiter().GetResult();
        PlayerSession session = Value(result);

        Check(handler.Requests.Count == 1, "expected exactly one login request");
        Check(handler.Requests[0].Path == "/api/auth/steam", $"unexpected path {handler.Requests[0].Path}");
        Check(handler.Requests[0].Bearer is null, "the login request must not carry a bearer token");
        Check(handler.Requests[0].Body.Contains("\"ticket\":\"sentinel-ticket\"", StringComparison.Ordinal), "ticket missing from the body");
        Check(tickets.LastIdentity == "feedback-api", $"ticket identity must match the server ({tickets.LastIdentity})");
        Check(session.AccessToken == "token-1", "access token not parsed");
        Check(session.Player.SteamId == SteamId, "player steamId not parsed");
        return Task.CompletedTask;
    }

    private static Task LoginRejectedAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.Unauthorized, "{}");
        using FeedbackRuntime runtime = Runtime(handler);

        FeedbackResult<PlayerSession> result = runtime.LoginAsync().GetAwaiter().GetResult();
        Check(FailureCode(result) == FeedbackErrorCode.SteamVerificationRejected, "expected steam_verification_rejected");
        Check(result.Failure!.StatusCode == 401, "expected status 401");
        Check(!result.Failure.Retryable, "a rejected ticket is not retryable");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 服务端说"Steam 没验成"（网络/超时/凭据没配）时，必须是可重试的独立错误码——
    /// 别让玩家以为自己的票据有问题。真机上这条曾经只剩一个笼统的 401。
    /// </summary>
    private static Task LoginUnavailableAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.Unauthorized, Json(new
        {
            title = "Steam 验票服务不可用",
            status = 401,
            detail = "Steam 没有给出结论（网络、超时，或本服务的 Steam:ApiKey / Steam:AppId 配置问题），稍后可以重试。",
            code = "steam_unavailable",
        }));
        using FeedbackRuntime runtime = Runtime(handler);

        FeedbackResult<PlayerSession> result = runtime.LoginAsync().GetAwaiter().GetResult();

        Check(
            FailureCode(result) == FeedbackErrorCode.SteamVerificationUnavailable,
            $"expected steam_verification_unavailable, got {FailureCode(result)}");
        Check(result.Failure!.Retryable, "Steam being unavailable must be retryable");
        Check(result.Failure.Message.Contains("Steam:ApiKey", StringComparison.Ordinal), "the server reason must reach the host log");
        return Task.CompletedTask;
    }

    /// <summary>服务端带 code 的否决必须仍是不可重试，并把服务端给出的原因带到宿主日志里。</summary>
    private static Task LoginRejectedWithReasonAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.Unauthorized, Json(new
        {
            title = "Steam 验票未通过",
            status = 401,
            detail = "票据无效、已过期，或与服务端的 appid/identity 不匹配。",
            code = "steam_ticket_rejected",
        }));
        using FeedbackRuntime runtime = Runtime(handler);

        FeedbackResult<PlayerSession> result = runtime.LoginAsync().GetAwaiter().GetResult();

        Check(
            FailureCode(result) == FeedbackErrorCode.SteamVerificationRejected,
            $"expected steam_verification_rejected, got {FailureCode(result)}");
        Check(!result.Failure!.Retryable, "a rejected ticket is not retryable");
        Check(result.Failure.Message.Contains("appid/identity", StringComparison.Ordinal), "the server reason must reach the host log");
        return Task.CompletedTask;
    }

    private static Task DebugLoginOffByDefaultAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-2"));
        using FeedbackRuntime runtime = Runtime(handler, debugSteamId: SteamId);
        runtime.LoginAsync().GetAwaiter().GetResult();

        Check(handler.Requests.Count == 1, "expected one login request");
        Check(!handler.Requests[0].Body.Contains("debugSteamId", StringComparison.Ordinal), "debugSteamId must not be sent while the switch is off");
        Check(handler.Requests[0].Body.Contains("\"ticket\"", StringComparison.Ordinal), "the ticket path must be used");
        return Task.CompletedTask;
    }

    private static Task DebugLoginOnSendsSteamIdAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-3"));
        StubTicketProvider tickets = new("unused-ticket");
        using FeedbackRuntime runtime = Runtime(handler, tickets, allowDebugLogin: true, debugSteamId: SteamId);
        runtime.LoginAsync().GetAwaiter().GetResult();

        Check(handler.Requests.Count == 1, "expected one login request");
        Check(handler.Requests[0].Body.Contains($"\"debugSteamId\":\"{SteamId}\"", StringComparison.Ordinal), "debugSteamId missing from the body");
        Check(tickets.Calls == 0, "the ticket provider must not be consulted during debug login");
        return Task.CompletedTask;
    }

    private static Task DebugLoginRejectsMalformedSteamIdAsync()
    {
        StubHandler handler = new();
        using FeedbackRuntime runtime = Runtime(handler, allowDebugLogin: true, debugSteamId: "123");
        FeedbackResult<PlayerSession> result = runtime.LoginAsync().GetAwaiter().GetResult();

        Check(FailureCode(result) == FeedbackErrorCode.InvalidSteamId, "expected invalid_steam_id");
        Check(handler.Requests.Count == 0, "a malformed debug SteamID must not be sent");
        return Task.CompletedTask;
    }

    private static Task SubmitSendsBearerAndTypeAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-4"));
        handler.RespondJson(HttpStatusCode.Created, FeedbackJson(42));
        using FeedbackRuntime runtime = Runtime(handler);

        FeedbackResult<PlayerFeedback> result = runtime
            .SubmitAsync(new PlayerFeedbackDraft(
                PlayerFeedbackType.Bug,
                "title",
                "content",
                GameVersion: "1.2.3",
                Map: "arena_01"))
            .GetAwaiter().GetResult();

        PlayerFeedback feedback = Value(result);
        Check(feedback.Id == 42, "feedback id not parsed");
        Check(handler.Requests.Count == 2, "expected login + submit");
        RecordedRequest submit = handler.Requests[1];
        Check(submit.Path == "/api/feedback", $"unexpected path {submit.Path}");
        Check(submit.Bearer == "token-4", "the submit request must carry the access token");
        Check(submit.Body.Contains("\"type\":\"Bug\"", StringComparison.Ordinal), "type must be the enum name, not a number");
        Check(submit.Body.Contains("\"gameVersion\":\"1.2.3\"", StringComparison.Ordinal), "metadata must serialize as camelCase");
        Check(submit.Body.Contains("\"map\":\"arena_01\"", StringComparison.Ordinal), "map metadata missing");
        return Task.CompletedTask;
    }

    private static Task TokenReusedAcrossCallsAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-5"));
        handler.RespondJson(HttpStatusCode.Created, FeedbackJson(1));
        handler.RespondJson(HttpStatusCode.OK, "[]");
        using FeedbackRuntime runtime = Runtime(handler);

        runtime.SubmitAsync(new PlayerFeedbackDraft(PlayerFeedbackType.Bug, "t", "c")).GetAwaiter().GetResult();
        runtime.ListMineAsync().GetAwaiter().GetResult();

        int logins = handler.Requests.Count(r => r.Path == "/api/auth/steam");
        Check(logins == 1, $"expected a single login, saw {logins}");
        Check(handler.Requests[2].Bearer == "token-5", "the cached token must be reused");
        return Task.CompletedTask;
    }

    private static Task ExpiredTokenTriggersLoginAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("fresh-token"));
        handler.RespondJson(HttpStatusCode.Created, FeedbackJson(7));
        SpyTokenStore store = new(new CachedAccessToken(SteamId, "stale-token", Now.AddMinutes(-1)));
        using FeedbackRuntime runtime = Runtime(handler, store: store, clock: new FixedClock());

        runtime.SubmitAsync(new PlayerFeedbackDraft(PlayerFeedbackType.Bug, "t", "c")).GetAwaiter().GetResult();

        Check(handler.Requests[0].Path == "/api/auth/steam", "an expired token must force a login first");
        Check(handler.Requests[1].Bearer == "fresh-token", "the fresh token must be used");
        Check(store.Token?.AccessToken == "fresh-token", "the fresh token must be stored");
        return Task.CompletedTask;
    }

    private static Task FreshTokenSkipsLoginAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, "[]");
        SpyTokenStore store = new(new CachedAccessToken(SteamId, "valid-token", Now.AddHours(5)));
        using FeedbackRuntime runtime = Runtime(handler, store: store, clock: new FixedClock());

        runtime.ListMineAsync().GetAwaiter().GetResult();

        Check(handler.Requests.Count == 1, "a valid cached token must not trigger a login");
        Check(handler.Requests[0].Bearer == "valid-token", "the cached token must be used");
        return Task.CompletedTask;
    }

    private static Task UnauthorizedTriggersSingleReloginAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("first-token"));
        handler.RespondJson(HttpStatusCode.Unauthorized, "{}");
        handler.RespondJson(HttpStatusCode.OK, LoginJson("second-token"));
        handler.RespondJson(HttpStatusCode.Created, FeedbackJson(9));
        using FeedbackRuntime runtime = Runtime(handler);

        FeedbackResult<PlayerFeedback> result = runtime
            .SubmitAsync(new PlayerFeedbackDraft(PlayerFeedbackType.Bug, "t", "c"))
            .GetAwaiter().GetResult();

        Check(result.Succeeded, $"expected success after re-login, got {result.Failure?.Code ?? "none"}");
        int logins = handler.Requests.Count(r => r.Path == "/api/auth/steam");
        int submits = handler.Requests.Count(r => r.Path == "/api/feedback");
        Check(logins == 2, $"expected exactly one re-login, saw {logins}");
        Check(submits == 2, $"expected exactly one retry, saw {submits}");
        Check(handler.Requests[3].Bearer == "second-token", "the retry must use the new token");
        return Task.CompletedTask;
    }

    private static Task NotFoundMapsToNotFoundAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-6"));
        handler.RespondJson(HttpStatusCode.NotFound, string.Empty);
        using FeedbackRuntime runtime = Runtime(handler);

        FeedbackResult<PlayerFeedbackDetail> result = runtime.GetDetailAsync(123).GetAwaiter().GetResult();
        Check(FailureCode(result) == FeedbackErrorCode.NotFound, "expected not_found");
        Check(handler.Requests.Count(r => r.Path == "/api/feedback/123") == 1, "404 must not be retried");
        return Task.CompletedTask;
    }

    private static Task RateLimitedIsNotRetriedAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-7"));
        handler.RespondJson(HttpStatusCode.TooManyRequests, string.Empty);
        using FeedbackRuntime runtime = Runtime(handler);

        FeedbackResult<PlayerFeedback> result = runtime
            .SubmitAsync(new PlayerFeedbackDraft(PlayerFeedbackType.Bug, "t", "c"))
            .GetAwaiter().GetResult();

        Check(FailureCode(result) == FeedbackErrorCode.RateLimited, "expected rate_limited");
        Check(result.Failure!.Retryable, "429 must be reported as retryable");
        Check(handler.Requests.Count(r => r.Path == "/api/feedback") == 1, "429 must not silently re-send inside the same window");
        return Task.CompletedTask;
    }

    private static Task TransportFailureRetriedOnceAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-8"));
        handler.FailTransport();
        handler.RespondJson(HttpStatusCode.Created, FeedbackJson(11));
        using FeedbackRuntime runtime = Runtime(handler);

        FeedbackResult<PlayerFeedback> result = runtime
            .SubmitAsync(new PlayerFeedbackDraft(PlayerFeedbackType.Bug, "t", "c"))
            .GetAwaiter().GetResult();

        Check(result.Succeeded, $"expected success after one retry, got {result.Failure?.Code ?? "none"}");
        Check(handler.Requests.Count(r => r.Path == "/api/feedback") == 2, "expected exactly one transport retry");
        return Task.CompletedTask;
    }

    private static Task TransportFailureTwiceAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-9"));
        handler.FailTransport();
        handler.FailTransport();
        using FeedbackRuntime runtime = Runtime(handler);

        FeedbackResult<PlayerFeedback> result = runtime
            .SubmitAsync(new PlayerFeedbackDraft(PlayerFeedbackType.Bug, "t", "c"))
            .GetAwaiter().GetResult();

        Check(FailureCode(result) == FeedbackErrorCode.TransportFailed, "expected transport_failed");
        Check(result.Failure!.StatusCode == 0, "a transport failure has no HTTP status");
        Check(result.Failure.Retryable, "transport failures are retryable");
        return Task.CompletedTask;
    }

    private static Task ServerErrorMapsAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-10"));
        handler.RespondJson(HttpStatusCode.InternalServerError, "{}");
        using FeedbackRuntime runtime = Runtime(handler);

        FeedbackResult<PlayerFeedback> result = runtime
            .SubmitAsync(new PlayerFeedbackDraft(PlayerFeedbackType.Bug, "t", "c"))
            .GetAwaiter().GetResult();

        Check(FailureCode(result) == FeedbackErrorCode.ServerError, "expected server_error");
        Check(result.Failure!.Retryable, "5xx is retryable");
        return Task.CompletedTask;
    }

    private static Task InvalidJsonMapsAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-11"));
        handler.RespondJson(HttpStatusCode.Created, "not-json-at-all");
        using FeedbackRuntime runtime = Runtime(handler);

        FeedbackResult<PlayerFeedback> result = runtime
            .SubmitAsync(new PlayerFeedbackDraft(PlayerFeedbackType.Bug, "t", "c"))
            .GetAwaiter().GetResult();

        Check(FailureCode(result) == FeedbackErrorCode.InvalidResponse, "expected invalid_response");
        return Task.CompletedTask;
    }

    private static Task EmptyBodyMapsAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-12"));
        handler.RespondJson(HttpStatusCode.Created, string.Empty);
        using FeedbackRuntime runtime = Runtime(handler);

        FeedbackResult<PlayerFeedback> result = runtime
            .SubmitAsync(new PlayerFeedbackDraft(PlayerFeedbackType.Bug, "t", "c"))
            .GetAwaiter().GetResult();

        Check(FailureCode(result) == FeedbackErrorCode.InvalidResponse, "expected invalid_response for an empty body");
        return Task.CompletedTask;
    }

    private static Task ServerValidationDetailPreservedAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-13"));
        handler.RespondJson(
            HttpStatusCode.BadRequest,
            Json(new { type = "about:blank", title = "请求无效", status = 400, detail = "title 长度必须在 1–200 之间" }));
        using FeedbackRuntime runtime = Runtime(handler);

        FeedbackResult<PlayerFeedback> result = runtime
            .SubmitAsync(new PlayerFeedbackDraft(PlayerFeedbackType.Bug, "t", "c"))
            .GetAwaiter().GetResult();

        Check(FailureCode(result) == FeedbackErrorCode.ValidationFailed, "expected validation_failed");
        Check(result.Failure!.Detail == "title 长度必须在 1–200 之间", "the server detail must be preserved for the UI");
        return Task.CompletedTask;
    }

    private static Task TokenStorageIsInjectedAsync()
    {
        // 默认路径：每个 Runtime 自带一份内存存储，新实例不会继承旧会话（等于"不落盘"）。
        StubHandler first = new();
        first.RespondJson(HttpStatusCode.OK, LoginJson("token-a"));
        first.RespondJson(HttpStatusCode.OK, "[]");
        using (FeedbackRuntime runtime = Runtime(first))
        {
            runtime.ListMineAsync().GetAwaiter().GetResult();
        }

        StubHandler second = new();
        second.RespondJson(HttpStatusCode.OK, LoginJson("token-b"));
        second.RespondJson(HttpStatusCode.OK, "[]");
        using (FeedbackRuntime runtime = Runtime(second))
        {
            runtime.ListMineAsync().GetAwaiter().GetResult();
        }
        Check(second.Requests[0].Path == "/api/auth/steam", "a new in-memory session must log in again");

        // 注入宿主的持久存储时，令牌被保存并可在下一次调用复用。
        StubHandler third = new();
        third.RespondJson(HttpStatusCode.OK, LoginJson("token-c"));
        third.RespondJson(HttpStatusCode.OK, "[]");
        SpyTokenStore store = new();
        using (FeedbackRuntime runtime = Runtime(third, store: store))
        {
            runtime.ListMineAsync().GetAwaiter().GetResult();
        }
        Check(store.Saves == 1, "the injected store must receive the token once");
        Check(store.Token?.AccessToken == "token-c", "the injected store must receive the issued token");
        return Task.CompletedTask;
    }

    private static Task LogsNeverContainSecretsAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("SENTINEL-ACCESS-TOKEN"));
        handler.RespondJson(HttpStatusCode.Unauthorized, "{}");
        handler.RespondJson(HttpStatusCode.OK, LoginJson("SENTINEL-ACCESS-TOKEN"));
        handler.RespondJson(HttpStatusCode.TooManyRequests, string.Empty);
        CapturingLog log = new();
        using FeedbackRuntime runtime = Runtime(handler, new StubTicketProvider("SENTINEL-TICKET"), log: log);

        runtime.SubmitAsync(new PlayerFeedbackDraft(PlayerFeedbackType.Bug, "t", "c")).GetAwaiter().GetResult();

        Check(log.Lines.Count > 0, "the log should record that something happened");
        foreach (string line in log.Lines)
        {
            Check(!line.Contains("SENTINEL-ACCESS-TOKEN", StringComparison.Ordinal), $"a log line leaked the access token: {line}");
            Check(!line.Contains("SENTINEL-TICKET", StringComparison.Ordinal), $"a log line leaked the ticket: {line}");
        }
        return Task.CompletedTask;
    }

    private static Task InvalidBaseUrlFailsFastAsync()
    {
        StubHandler handler = new();
        using FeedbackRuntime runtime = Runtime(handler, baseUrl: "not-a-url");
        FeedbackResult<PlayerFeedback> result = runtime
            .SubmitAsync(new PlayerFeedbackDraft(PlayerFeedbackType.Bug, "t", "c"))
            .GetAwaiter().GetResult();

        Check(FailureCode(result) == FeedbackErrorCode.InvalidBaseUrl, "expected invalid_base_url");
        Check(handler.Requests.Count == 0, "a broken configuration must not produce requests");
        return Task.CompletedTask;
    }

    private static Task EmptyIdentityRejectedAsync()
    {
        StubHandler handler = new();
        using FeedbackRuntime runtime = Runtime(handler, identity: string.Empty);
        FeedbackResult<PlayerSession> result = runtime.LoginAsync().GetAwaiter().GetResult();
        Check(FailureCode(result) == FeedbackErrorCode.InvalidConfiguration, "expected invalid_configuration");
        return Task.CompletedTask;
    }

    private static Task MineDeserializesAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-15"));
        handler.RespondJson(HttpStatusCode.OK, $"[{FeedbackBody(1, "first")},{FeedbackBody(2, "second")}]");
        using FeedbackRuntime runtime = Runtime(handler);

        FeedbackResult<IReadOnlyList<PlayerFeedback>> result = runtime.ListMineAsync().GetAwaiter().GetResult();
        IReadOnlyList<PlayerFeedback> items = Value(result);

        Check(items.Count == 2, $"expected two items, saw {items.Count}");
        Check(items[0].Title == "first", "the first item must keep server order");
        Check(items[0].CreatedAt.Offset == TimeSpan.Zero, "createdAt must parse as UTC");
        return Task.CompletedTask;
    }

    private static Task DetailDeserializesAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-16"));
        handler.RespondJson(
            HttpStatusCode.OK,
            Json(new
            {
                id = 5,
                type = "Bug",
                title = "detail",
                content = "body",
                status = "Open",
                gameVersion = "1.0",
                buildNumber = "1",
                operatingSystem = "Windows 11",
                gpu = "RTX",
                locale = "zh-CN",
                map = "arena_01",
                character = "mage",
                createdAt = Now,
                comments = new[]
                {
                    new { id = 1, authorType = "Player", content = "still broken", createdAt = Now },
                },
            }));
        using FeedbackRuntime runtime = Runtime(handler);

        FeedbackResult<PlayerFeedbackDetail> result = runtime.GetDetailAsync(5).GetAwaiter().GetResult();
        PlayerFeedbackDetail detail = Value(result);

        Check(detail.Id == 5, "detail id not parsed");
        Check(detail.Comments.Count == 1, "comments not parsed");
        Check(detail.Comments[0].AuthorType == "Player", "authorType not parsed");
        return Task.CompletedTask;
    }

    private static Task CommentPostsAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-17"));
        handler.RespondJson(
            HttpStatusCode.Created,
            Json(new { id = 3, authorType = "Player", content = "补充说明", createdAt = Now }));
        using FeedbackRuntime runtime = Runtime(handler);

        FeedbackResult<PlayerFeedbackComment> result = runtime.AddCommentAsync(7, "补充说明").GetAwaiter().GetResult();
        PlayerFeedbackComment comment = Value(result);

        Check(comment.Id == 3, "comment id not parsed");
        RecordedRequest request = handler.Requests[1];
        Check(request.Path == "/api/feedback/7/comments", $"unexpected path {request.Path}");

        // STJ 默认把非 ASCII 转义（"content":"\u8865..."），所以比对解析后的值而不是原文字面量。
        using JsonDocument body = JsonDocument.Parse(request.Body);
        Check(body.RootElement.GetProperty("content").GetString() == "补充说明", "comment content missing");
        return Task.CompletedTask;
    }

    private static Task BaseUrlNormalizationAsync()
    {
        StubHandler handler = new();
        handler.RespondJson(HttpStatusCode.OK, LoginJson("token-18"));
        handler.RespondJson(HttpStatusCode.OK, "[]");
        using FeedbackRuntime runtime = Runtime(handler, baseUrl: "http://localhost:5087");
        runtime.ListMineAsync().GetAwaiter().GetResult();

        Check(handler.Requests[1].Path == "/api/feedback/mine", $"unexpected path {handler.Requests[1].Path}");
        return Task.CompletedTask;
    }

    private static async Task CancellationSurfacesAsync()
    {
        StubHandler handler = new();
        using FeedbackRuntime runtime = Runtime(handler);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        bool cancelled = false;
        try
        {
            await runtime.ListMineAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Check(cancelled, "cancellation must surface as OperationCanceledException, not as transport_failed");
        Check(handler.Requests.Count == 0, "a cancelled call must not reach the transport");
    }

    // ------------------------------------------------------------ helpers

    private const string SteamId = "76561197960265729";

    private static async Task Run(string name, Func<Task> test)
    {
        try
        {
            await test();
            Console.WriteLine($"HARNESS PASS {name}");
        }
        catch (Exception ex)
        {
            Failures.Add(name);
            Console.WriteLine($"HARNESS FAIL {name}: {ex.Message}");
        }
    }

    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string FailureCode<T>(FeedbackResult<T> result) where T : class =>
        result.Failure?.Code ?? throw new InvalidOperationException("expected a failure but the call succeeded");

    private static T Value<T>(FeedbackResult<T> result) where T : class =>
        result.Value ?? throw new InvalidOperationException($"expected a value but the call failed with {result.Failure?.Code}");

    private static FeedbackRuntime Runtime(
        StubHandler handler,
        ITicketProvider? tickets = null,
        ITokenStore? store = null,
        IFeedbackLog? log = null,
        TimeProvider? clock = null,
        string baseUrl = "http://localhost:5087",
        string identity = "feedback-api",
        bool allowDebugLogin = false,
        string? debugSteamId = null) =>
        new(
            new FeedbackOptions(baseUrl, identity, 15, allowDebugLogin, debugSteamId, null),
            tickets ?? new StubTicketProvider("test-ticket"),
            store,
            log,
            handler,
            clock ?? new FixedClock());

    private static string Json(object value) => JsonSerializer.Serialize(value, Web);

    private static string LoginJson(string accessToken, DateTimeOffset? expiresAt = null) => Json(new
    {
        accessToken,
        expiresAtUtc = expiresAt ?? Now.AddHours(24),
        player = new { steamId = SteamId, steamName = "Tester", avatarUrl = (string?)null },
    });

    private static string FeedbackJson(int id) => Json(new
    {
        id,
        type = "Bug",
        title = "t",
        content = "c",
        status = "Open",
        gameVersion = "1.2.3",
        buildNumber = "456",
        operatingSystem = "Windows 11",
        gpu = "RTX 4070",
        locale = "zh-CN",
        map = "arena_01",
        character = "mage",
        createdAt = Now,
    });

    private static string FeedbackBody(int id, string title) => Json(new
    {
        id,
        type = "Bug",
        title,
        content = "c",
        status = "Open",
        gameVersion = "1.2.3",
        buildNumber = "456",
        operatingSystem = "Windows 11",
        gpu = "RTX 4070",
        locale = "zh-CN",
        map = "arena_01",
        character = "mage",
        createdAt = Now,
    });

    private sealed record RecordedRequest(string Method, string Path, string? Bearer, string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();

        public List<RecordedRequest> Requests { get; } = [];

        public StubHandler Respond(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responders.Enqueue(responder);
            return this;
        }

        public StubHandler RespondJson(HttpStatusCode status, string json) =>
            Respond(_ =>
            {
                HttpResponseMessage response = new(status);
                if (!string.IsNullOrEmpty(json))
                {
                    response.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }
                return response;
            });

        public StubHandler FailTransport() => Respond(_ => throw new HttpRequestException("simulated network failure"));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.Headers.Authorization?.Parameter,
                body));

            if (_responders.Count == 0)
            {
                throw new InvalidOperationException($"no scripted response for {request.Method} {request.RequestUri}");
            }
            return _responders.Dequeue()(request);
        }
    }

    private sealed class StubTicketProvider(string? ticket) : ITicketProvider
    {
        public int Calls { get; private set; }

        public string? LastIdentity { get; private set; }

        public Task<string?> GetWebApiTicketAsync(string identity, CancellationToken cancellationToken)
        {
            Calls++;
            LastIdentity = identity;
            return Task.FromResult(ticket);
        }
    }

    private sealed class SpyTokenStore(CachedAccessToken? initial = null) : ITokenStore
    {
        public CachedAccessToken? Token { get; private set; } = initial;

        public int Saves { get; private set; }

        public int Clears { get; private set; }

        public Task<CachedAccessToken?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Token);

        public Task SaveAsync(CachedAccessToken token, CancellationToken cancellationToken)
        {
            Token = token;
            Saves++;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            Token = null;
            Clears++;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLog : IFeedbackLog
    {
        public List<string> Lines { get; } = [];

        public void Info(string message) => Lines.Add("INFO " + message);

        public void Warning(string message) => Lines.Add("WARN " + message);

        public void Error(string message) => Lines.Add("ERROR " + message);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
