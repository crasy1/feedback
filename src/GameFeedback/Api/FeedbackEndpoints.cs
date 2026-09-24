using System.Security.Claims;
using GameFeedback.Contracts.Requests;
using GameFeedback.Contracts.Responses;
using GameFeedback.Services;
using Microsoft.AspNetCore.Mvc;

namespace GameFeedback.Api;

public static class FeedbackEndpoints
{
    /// <summary>
    /// 挂在 /g/{appId} 之下，因此实际路径是 <c>/g/{appId}/api/feedback/...</c>。
    /// <see cref="GameTokenFilter"/> 保证令牌必须是为这个游戏签发的。
    /// </summary>
    public static IEndpointRouteBuilder MapFeedbackEndpoints(this IEndpointRouteBuilder app)
    {
        // 玩家 API 走 JWT（无 Cookie），对 CSRF 免疫；防伪校验保留给 Blazor 管理端表单。
        var group = app.MapGroup("/api/feedback")
            .WithTags("Feedback")
            .RequireAuthorization("Player")
            .AddEndpointFilter<GameTokenFilter>()
            .DisableAntiforgery();

        group.MapPost("/", async (
            CreateFeedbackRequest? request,
            HttpContext http,
            ClaimsPrincipal user,
            FeedbackService feedbacks,
            SteamPlaytimeService playtime,
            CancellationToken cancellationToken) =>
        {
            var game = http.GetResolvedGame()!;
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

            var playerId = await feedbacks.ResolvePlayerIdAsync(game.Id, steamId, cancellationToken);
            if (playerId is null)
            {
                return Results.Unauthorized();
            }

            // 游玩时长由服务端按<b>本游戏</b>的 AppID 与凭据向 Steam 查询后快照到这条反馈上；取不到就是 null，
            // 绝不因此拒绝提交（见 issue 03 与 ADR-0006）。SteamID 来自认证主体，与请求体无关。
            // 凭据可能在令牌签发之后被管理员摘掉，所以这里再确认一次配置仍然完整。
            var playtimeMinutes = game.IsFullyConfigured
                ? await playtime.GetPlaytimeMinutesAsync(steamId, game.ApiKey!, game.AppId!, cancellationToken)
                : (int?)null;

            var feedback = await feedbacks.CreateAsync(game.Id, playerId.Value, request, playtimeMinutes, cancellationToken);
            return Results.Created($"/g/{game.AppId}/api/feedback/{feedback.Id}", FeedbackService.ToDto(feedback));
        })
        .WithSummary("创建反馈")
        .Produces<FeedbackDto>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireRateLimiting("player-write");

        group.MapGet("/mine", async (
            HttpContext http,
            ClaimsPrincipal user,
            FeedbackService feedbacks,
            CancellationToken cancellationToken) =>
        {
            var game = http.GetResolvedGame()!;
            var steamId = user.FindFirst("sub")?.Value;
            if (steamId is null)
            {
                return Results.Unauthorized();
            }

            var playerId = await feedbacks.ResolvePlayerIdAsync(game.Id, steamId, cancellationToken);
            if (playerId is null)
            {
                return Results.Unauthorized();
            }

            var items = await feedbacks.ListOwnAsync(game.Id, playerId.Value, cancellationToken);
            return Results.Ok(items.Select(FeedbackService.ToDto));
        })
        .WithSummary("自己的最近 100 条反馈")
        .Produces<IEnumerable<FeedbackDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("/{id:int}", async (
            int id,
            HttpContext http,
            ClaimsPrincipal user,
            FeedbackService feedbacks,
            CancellationToken cancellationToken) =>
        {
            var game = http.GetResolvedGame()!;
            var steamId = user.FindFirst("sub")?.Value;
            if (steamId is null)
            {
                return Results.Unauthorized();
            }

            var playerId = await feedbacks.ResolvePlayerIdAsync(game.Id, steamId, cancellationToken);
            if (playerId is null)
            {
                return Results.Unauthorized();
            }

            // 所有权在查询层强制：他人、别的游戏、或缺失一律 404。
            var feedback = await feedbacks.GetOwnAsync(game.Id, playerId.Value, id, cancellationToken);
            if (feedback is null)
            {
                return Results.NotFound();
            }

            var comments = feedback.Comments
                .Select(c => new CommentDto(c.Id, c.AuthorType.ToString(), c.Content, c.CreatedAt))
                .ToList();
            return Results.Ok(FeedbackService.ToDetailDto(feedback, comments));
        })
        .WithSummary("反馈详情（仅限属主；非本人或缺失一律 404）")
        .Produces<FeedbackDetailDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/{id:int}/comments", async (
            int id,
            CreateCommentRequest? request,
            HttpContext http,
            ClaimsPrincipal user,
            FeedbackService feedbacks,
            CancellationToken cancellationToken) =>
        {
            var game = http.GetResolvedGame()!;
            var steamId = user.FindFirst("sub")?.Value;
            if (steamId is null)
            {
                return Results.Unauthorized();
            }

            var content = request?.Content;
            var validationError = FeedbackService.ValidateComment(content);
            if (validationError is not null)
            {
                return Results.Problem(statusCode: 400, title: "请求无效", detail: validationError);
            }

            var playerId = await feedbacks.ResolvePlayerIdAsync(game.Id, steamId, cancellationToken);
            if (playerId is null)
            {
                return Results.Unauthorized();
            }

            // 只有属主能评论：他人、别的游戏、或缺失一律 404。
            var feedback = await feedbacks.GetOwnAsync(game.Id, playerId.Value, id, cancellationToken);
            if (feedback is null)
            {
                return Results.NotFound();
            }

            var comment = await feedbacks.AddPlayerCommentAsync(playerId.Value, id, content!, cancellationToken);
            return Results.Created(
                $"/g/{game.AppId}/api/feedback/{id}",
                new CommentDto(comment.Id, comment.AuthorType.ToString(), comment.Content, comment.CreatedAt));
        })
        .WithSummary("追加玩家评论（仅限属主）")
        .Produces<CommentDto>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .RequireRateLimiting("player-comments");

        return app;
    }
}
