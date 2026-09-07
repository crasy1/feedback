using GameFeedback.Contracts.Requests;
using GameFeedback.Contracts.Responses;
using GameFeedback.Data;
using GameFeedback.Services;
using Microsoft.AspNetCore.Mvc;

namespace GameFeedback.Api;

public static class AuthEndpoints
{
    private const int MaxTicketLength = 4096;

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // 玩家 API 使用 JWT（无 Cookie），对 CSRF 免疫；防伪校验保留给 Blazor 管理端表单。
        var group = app.MapGroup("/api/auth").RequireRateLimiting("auth-ip").DisableAntiforgery();

        group.MapPost("/steam", async (
            SteamLoginRequest? request,
            SteamAuthService steamAuth,
            PlayerService players,
            TokenService tokens,
            CancellationToken cancellationToken) =>
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
            var steamId = await steamAuth.AuthenticateTicketAsync(ticket, cancellationToken);
            if (steamId is null)
            {
                return Results.Unauthorized();
            }

            var (steamName, avatarUrl) = await steamAuth.GetPlayerSummaryAsync(steamId, cancellationToken);
            var player = await players.UpsertFromSteamLoginAsync(steamId, steamName, avatarUrl, cancellationToken);

            var (accessToken, expiresAt) = tokens.CreateToken(steamId);
            return Results.Ok(new SteamLoginResponse(
                accessToken,
                expiresAt,
                new PlayerDto(player.SteamId, player.SteamName, player.AvatarUrl)));
        });

        return app;
    }
}
