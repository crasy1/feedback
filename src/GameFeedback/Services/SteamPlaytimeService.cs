using System.Text.Json;
using Microsoft.Extensions.Options;

namespace GameFeedback.Services;

/// <summary>
/// 查询玩家在本游戏上的累计游玩时长（分钟），走 Steam Web API 的
/// <c>IPlayerService/GetSingleGamePlaytime/v1</c>。
/// <para>
/// 尽力而为：任何失败（超时、非 2xx、JSON 异常、字段缺失）都返回 <c>null</c> 并记 Warning，
/// <b>绝不</b>抛给调用方——玩家不该因为查不到时长就丢掉自己写好的反馈。
/// </para>
/// <para>
/// 不复用 <see cref="SteamAuthService"/>：那个类的职责是"验票 + 资料"。
/// </para>
/// </summary>
public class SteamPlaytimeService(
    IHttpClientFactory httpClientFactory,
    IOptions<SteamOptions> steamOptions,
    ILogger<SteamPlaytimeService> logger)
{
    private static readonly string GetSingleGamePlaytimePath = "/IPlayerService/GetSingleGamePlaytime/v1/";

    /// <summary>
    /// 查询预算。共享的 <c>HttpClient("Steam")</c> 超时是 10 秒，
    /// 让一次反馈提交卡满 10 秒才返回 201 对玩家太糟，所以这里单独设更短的预算。
    /// </summary>
    public static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(3);

    /// <summary>响应正文写进日志前的截断长度，避免异常巨大的响应灌满日志。</summary>
    private const int LoggedPayloadMaxLength = 512;

    private HttpClient Client => httpClientFactory.CreateClient("Steam");

    /// <summary>
    /// 返回该玩家在本 AppId 上的累计游玩时长（分钟）；取不到返回 <c>null</c>。
    /// <paramref name="steamId"/> 必须来自认证主体，绝不要传入请求体里的值。
    /// </summary>
    public async Task<int?> GetPlaytimeMinutesAsync(string steamId, CancellationToken cancellationToken)
    {
        var steam = steamOptions.Value;
        var url = $"{GetSingleGamePlaytimePath}?key={Uri.EscapeDataString(steam.ApiKey)}" +
                  $"&steamid={Uri.EscapeDataString(steamId)}" +
                  $"&appid={Uri.EscapeDataString(steam.AppId)}";

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(LookupBudget);

        HttpResponseMessage response;
        try
        {
            response = await Client.GetAsync(url, budget.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方自己取消了（客户端断开等）：照实往上传，不要伪装成"拿不到时长"。
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Steam GetSingleGamePlaytime 请求失败（SteamId={SteamId} AppId={AppId}），本条反馈的游玩时长记为 null",
                steamId,
                steam.AppId);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Steam GetSingleGamePlaytime 返回 {StatusCode}（SteamId={SteamId} AppId={AppId}），本条反馈的游玩时长记为 null",
                (int)response.StatusCode,
                steamId,
                steam.AppId);
            return null;
        }

        string payload;
        try
        {
            payload = await response.Content.ReadAsStringAsync(budget.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Steam GetSingleGamePlaytime 响应读取失败（SteamId={SteamId}），本条反馈的游玩时长记为 null",
                steamId);
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            // 实测形状（真实 key + 真实账号，见 issue 01）：
            //   {"response":{"playtime_forever":2361,"playtime_2weeks":81,…}}
            // 扁平对象——没有 games[] 包装，也不回显 appid。playtime_forever 单位是分钟，0 合法。
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("response", out var responseObject)
                && responseObject.ValueKind == JsonValueKind.Object
                && responseObject.TryGetProperty("playtime_forever", out var playtime)
                && playtime.ValueKind == JsonValueKind.Number
                && playtime.TryGetInt32(out var minutes)
                && minutes >= 0)
            {
                return minutes;
            }
        }
        catch (JsonException)
        {
            logger.LogWarning(
                "Steam GetSingleGamePlaytime 响应不是有效 JSON（SteamId={SteamId} AppId={AppId}），本条反馈的游玩时长记为 null：{Payload}",
                steamId,
                steam.AppId,
                Truncate(payload));
            return null;
        }

        // 走到这里说明形状与预期不符：私密资料 / 未拥有该游戏，或者 Steam 动了字段名。
        // 响应体不含认证材料，可以安全记录——否则"字段名变了"会表现成与"资料私密"一模一样的静默 null，
        // 而这种错误不会自己暴露（本功能的失败策略本来就是 null）。见 issue 01 的兜底要求。
        logger.LogWarning(
            "Steam GetSingleGamePlaytime 响应里没有可用的 playtime_forever（SteamId={SteamId} AppId={AppId}），本条反馈的游玩时长记为 null：{Payload}",
            steamId,
            steam.AppId,
            Truncate(payload));
        return null;
    }

    private static string Truncate(string value) =>
        value.Length <= LoggedPayloadMaxLength ? value : value[..LoggedPayloadMaxLength] + "…";
}
