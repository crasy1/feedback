using GameFeedback.Contracts.Requests;
using GameFeedback.Contracts.Responses;
using GameFeedback.Services;
using Microsoft.Extensions.Options;

namespace GameFeedback.Api;

public static class AuthEndpoints
{
    /// <summary>
    /// 票据长度上限（十六进制字符）。Steam 的 Web API 票据最大 2560 字节，十六进制编码后是 5120 字符，
    /// 所以 4096 会把真实玩家的票据直接挡掉（真机实测：5120）。这里给到 8192（= 4096 字节）留出余量，
    /// 同时仍然是一个有界值，因为票据会被转发到 Steam 的查询串里。
    /// </summary>
    private const int MaxTicketLength = 8192;

    /// <summary>
    /// 挂在 /g/{appId} 之下，因此实际路径是 <c>/g/{appId}/api/auth/steam</c>。
    /// 游戏归属来自路径、由 Steam 验票证明（票据与 appid + identity 绑定），
    /// 绝不来自请求体——请求体里没有任何游戏字段。
    /// </summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // 玩家 API 使用 JWT（无 Cookie），对 CSRF 免疫；防伪校验保留给 Blazor 管理端表单。
        var group = app.MapGroup("/api/auth")
            .WithTags("Auth")
            .RequireRateLimiting("auth-ip")
            .DisableAntiforgery();

        group.MapPost("/steam", async (
            SteamLoginRequest? request,
            HttpContext http,
            SteamAuthService steamAuth,
            PlayerService players,
            TokenService tokens,
            IOptions<SteamOptions> steamOptions,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            var game = http.GetResolvedGame()!;

            // 调试模式（启动校验保证仅 Development 可启用）：跳过 Steam 验票，
            // 采用请求声明的 SteamID64（仍做格式校验），便于无法出票时联调。
            // 它不需要该游戏配好凭据——验票本来就被跳过了。
            string? steamId;
            if (steamOptions.Value.DebugSkipTicketValidation
                && !string.IsNullOrWhiteSpace(request?.DebugSteamId))
            {
                if (!SteamAuthService.IsValidSteamId64(request.DebugSteamId, out steamId))
                {
                    return Results.Problem(statusCode: 400, title: "SteamID64 格式无效");
                }
                logger.LogWarning("调试模式跳过 Steam 验票 GameId={GameId} SteamId={SteamId}", game.Id, steamId);
            }
            else
            {
                // 该游戏没有可用的 Steam 凭据：不去问 Steam，fail closed。
                // （AppID 缺失的游戏根本解析不到这一步——它在 /g/{appId} 上就 404 了。）
                if (!game.IsFullyConfigured)
                {
                    return game.CredentialUnreadable
                        ? ApiProblems.CredentialUnreadable()
                        : ApiProblems.SteamUnavailable(
                            "这个游戏在管理端还没有可用的 Steam 凭据（Web API Key 缺失或未选择），稍后可以重试。");
                }

                var ticket = request?.Ticket;
                if (string.IsNullOrWhiteSpace(ticket))
                {
                    return Results.Problem(statusCode: 400, title: "缺少票据");
                }
                if (ticket.Length > MaxTicketLength)
                {
                    return Results.Problem(statusCode: 400, title: "票据过长");
                }

                // SteamID 只能来自服务端验证结果，fail closed。失败也分两类上报：
                // "票被否"（重试无用）与"没验成"（网络/配置问题，可重试），各自带稳定的 code。
                SteamTicketVerification verification = await steamAuth.AuthenticateTicketAsync(
                    ticket, game.ApiKey!, game.AppId!, game.Identity, cancellationToken);
                if (verification.SteamId is null)
                {
                    return verification.Failure == SteamTicketFailure.SteamUnavailable
                        ? ApiProblems.SteamUnavailable(
                            "Steam 没有给出结论（网络、超时，或该游戏的 Steam 凭据有问题），稍后可以重试。")
                        : ApiProblems.SteamTicketRejected();
                }
                steamId = verification.SteamId;
            }

            var (steamName, avatarUrl) = game.IsFullyConfigured
                ? await steamAuth.GetPlayerSummaryAsync(steamId, game.ApiKey!, cancellationToken)
                : (null, null);

            var player = await players.UpsertFromSteamLoginAsync(
                game.Id, steamId, steamName, avatarUrl, cancellationToken);

            var (accessToken, expiresAt) = tokens.CreateToken(steamId, game.Id);
            return Results.Ok(new SteamLoginResponse(
                accessToken,
                expiresAt,
                new PlayerDto(player.SteamId, player.SteamName, player.AvatarUrl)));
        })
        .WithSummary("Steam 票据登录，换取玩家访问令牌；Development 调试开关开启时可改用 debugSteamId 直接登录")
        .Produces<SteamLoginResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
