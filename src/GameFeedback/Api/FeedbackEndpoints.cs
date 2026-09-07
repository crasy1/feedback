using System.Security.Claims;
using GameFeedback.Contracts.Requests;
using GameFeedback.Contracts.Responses;
using GameFeedback.Services;
using Microsoft.AspNetCore.Mvc;

namespace GameFeedback.Api;

public static class FeedbackEndpoints
{
    public static IEndpointRouteBuilder MapFeedbackEndpoints(this IEndpointRouteBuilder app)
    {
        // 玩家 API 走 JWT（无 Cookie），对 CSRF 免疫；防伪校验保留给 Blazor 管理端表单。
        var group = app.MapGroup("/api/feedback")
            .RequireAuthorization("Player")
            .DisableAntiforgery();

        group.MapPost("/", async (
            CreateFeedbackRequest? request,
            ClaimsPrincipal user,
            FeedbackService feedbacks,
            CancellationToken cancellationToken) =>
        {
            var steamId = user.FindFirst("sub")?.Value;
            if (steamId is null)
            {
                return Results.Unauthorized();
            }

            if (request is null)
            {
                return Results.Problem(statusCode: 400, title: "缺少请求体");
            }
            var validationError = FeedbackService.Validate(request);
            if (validationError is not null)
            {
                return Results.Problem(statusCode: 400, title: "请求无效", detail: validationError);
            }

            var playerId = await feedbacks.ResolvePlayerIdAsync(steamId, cancellationToken);
            if (playerId is null)
            {
                return Results.Unauthorized();
            }

            // SteamID 来自认证主体，与请求体无关。
            var feedback = await feedbacks.CreateAsync(playerId.Value, request, cancellationToken);
            return Results.Created($"/api/feedback/{feedback.Id}", FeedbackService.ToDto(feedback));
        }).RequireRateLimiting("player-write");

        group.MapGet("/mine", async (
            ClaimsPrincipal user,
            FeedbackService feedbacks,
            CancellationToken cancellationToken) =>
        {
            var steamId = user.FindFirst("sub")?.Value;
            if (steamId is null)
            {
                return Results.Unauthorized();
            }

            var playerId = await feedbacks.ResolvePlayerIdAsync(steamId, cancellationToken);
            if (playerId is null)
            {
                return Results.Unauthorized();
            }

            var items = await feedbacks.ListOwnAsync(playerId.Value, cancellationToken);
            return Results.Ok(items.Select(FeedbackService.ToDto));
        });

        return app;
    }
}
