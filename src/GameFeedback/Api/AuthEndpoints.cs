using GameFeedback.Contracts.Requests;
using GameFeedback.Contracts.Responses;
using GameFeedback.Data;
using GameFeedback.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GameFeedback.Api;

public static class AuthEndpoints
{
    private const int MaxTicketLength = 4096;

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

                // SteamID 只能来自服务端验证结果，fail closed。
                steamId = await steamAuth.AuthenticateTicketAsync(ticket, cancellationToken);
                if (steamId is null)
                {
                    return Results.Unauthorized();
                }
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
