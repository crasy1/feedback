using GameFeedback.Services;

namespace GameFeedback.Api;

/// <summary>
/// 把路径里的 <c>{appId}</c> 解析成 Game。未知 AppID → 404 <c>game_not_found</c>，
/// 停用 → 403 <c>game_disabled</c>。
/// <para>
/// 用 AppID 而不是自造的 slug 寻址：客户端本来就运行在某个 AppID 之下，宿主把它交给插件即可，
/// 接入时不用手抄任何标识——「slug 抄错」这类事故从模型上消失。
/// </para>
/// <para>
/// 凭据缺失或无法解密<b>不</b>在这里拦：那两种情况要由登录端点以 401 报出，
/// 而且调试登录（Development 专用）本来就允许在没有凭据的游戏上工作。
/// </para>
/// <para>
/// 服务从 <c>RequestServices</c> 取而不是构造注入：端点过滤器由框架在构建管线时创建一次，
/// 构造注入会把 scoped 的 <see cref="GameResolver"/> 捕获成单例。
/// </para>
/// </summary>
public sealed class ResolveGameFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var appId = http.Request.RouteValues["appId"] as string;
        var resolver = http.RequestServices.GetRequiredService<GameResolver>();
        var game = await resolver.ResolveAsync(appId, http.RequestAborted);

        if (game is null)
        {
            return ApiProblems.GameNotFound(appId);
        }
        if (!game.IsActive)
        {
            return ApiProblems.GameDisabled();
        }

        http.SetResolvedGame(game);
        return await next(context);
    }
}

/// <summary>
/// 核对「路径里的游戏」与「令牌里的游戏」一致。用一个游戏换来的令牌拿到另一个游戏的路径下使用，
/// 一律 401——这是「访问令牌绑定游戏」这条不变量的执行点。
/// </summary>
public sealed class GameTokenFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var game = http.GetResolvedGame();
        if (game is null)
        {
            return ApiProblems.GameRequired();
        }
        if (http.GetTokenGameId() != game.Id)
        {
            return ApiProblems.GameMismatch();
        }

        return await next(context);
    }
}
