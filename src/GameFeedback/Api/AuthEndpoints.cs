using GameFeedback.Contracts.Requests;
using GameFeedback.Contracts.Responses;
using GameFeedback.Data;
using GameFeedback.Services;
using Microsoft.AspNetCore.Mvc;
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

    /// <summary>验票失败的两类稳定代码，放在 ProblemDetails 的扩展成员 <c>code</c> 里，客户端按它分支。</summary>
    private const string SteamTicketRejectedCode = "steam_ticket_rejected";

    private const string SteamUnavailableCode = "steam_unavailable";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // 玩家 API 使用 JWT（无 Cookie），对 CSRF 免疫；防伪校验保留给 Blazor 管理端表单。
        var group = app.MapGroup("/api/auth")
            .WithTags("Auth")
            .RequireRateLimiting("auth-ip")
            .DisableAntiforgery();

        group.MapPost("/steam", async (
            SteamLoginRequest? request,
            SteamAuthService steamAuth,
            PlayerService players,
            TokenService tokens,
            IOptions<SteamOptions> steamOptions,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            // 调试模式（启动校验保证仅 Development 可启用）：跳过 Steam 验票，
            // 采用请求声明的 SteamID64（仍做格式校验），便于无法出票时联调。
            string? steamId;
            if (steamOptions.Value.DebugSkipTicketValidation
                && !string.IsNullOrWhiteSpace(request?.DebugSteamId))
            {
                if (!SteamAuthService.IsValidSteamId64(request.DebugSteamId, out steamId))
                {
                    return Results.Problem(statusCode: 400, title: "SteamID64 格式无效");
                }
                logger.LogWarning("调试模式跳过 Steam 验票 SteamId={SteamId}", steamId);
            }
            else
            {
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
                SteamTicketVerification verification = await steamAuth.AuthenticateTicketAsync(ticket, cancellationToken);
                if (verification.SteamId is null)
                {
                    return verification.Failure == SteamTicketFailure.SteamUnavailable
                        ? Results.Problem(
                            statusCode: StatusCodes.Status401Unauthorized,
                            title: "Steam 验票服务不可用",
                            detail: "Steam 没有给出结论（网络、超时，或本服务的 Steam:ApiKey / Steam:AppId 配置问题），稍后可以重试。",
                            extensions: new Dictionary<string, object?> { ["code"] = SteamUnavailableCode })
                        : Results.Problem(
                            statusCode: StatusCodes.Status401Unauthorized,
                            title: "Steam 验票未通过",
                            detail: "票据无效、已过期，或与服务端的 appid/identity 不匹配。",
                            extensions: new Dictionary<string, object?> { ["code"] = SteamTicketRejectedCode });
                }
                steamId = verification.SteamId;
            }

            var (steamName, avatarUrl) = await steamAuth.GetPlayerSummaryAsync(steamId, cancellationToken);
            var player = await players.UpsertFromSteamLoginAsync(steamId, steamName, avatarUrl, cancellationToken);

            var (accessToken, expiresAt) = tokens.CreateToken(steamId);
            return Results.Ok(new SteamLoginResponse(
                accessToken,
                expiresAt,
                new PlayerDto(player.SteamId, player.SteamName, player.AvatarUrl)));
        })
        .WithSummary("Steam 票据登录，换取玩家访问令牌；Development 调试开关开启时可改用 debugSteamId 直接登录")
        .Produces<SteamLoginResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }
}
