using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace GameFeedback.Services;

/// <summary>验票失败的两类原因：Steam 明确否决，还是我们压根没验成。</summary>
public enum SteamTicketFailure
{
    /// <summary>验票通过。</summary>
    None,

    /// <summary>Steam 明确回答"这张票不行"（无效、过期、appid/identity 不匹配…）。</summary>
    TicketRejected,

    /// <summary>没能完成验票：网络/超时/HTTP 错误/响应不可解析/配置不对。</summary>
    SteamUnavailable,
}

/// <summary>验票结果。成功必带受信任的 SteamID64；失败必带原因类别。</summary>
public sealed record SteamTicketVerification(string? SteamId, SteamTicketFailure Failure)
{
    public static SteamTicketVerification Ok(string steamId) => new(steamId, SteamTicketFailure.None);

    public static SteamTicketVerification Rejected() => new(null, SteamTicketFailure.TicketRejected);

    public static SteamTicketVerification Unavailable() => new(null, SteamTicketFailure.SteamUnavailable);
}

/// <summary>
/// Steam 票据验证与资料获取。验证失败一律 fail closed（不给 SteamID、不签发令牌），
/// 但会把失败分成"票被否"与"没验成"两类，让调用方与运维都能分辨。
/// </summary>
public class SteamAuthService(IHttpClientFactory httpClientFactory, IOptions<SteamOptions> steamOptions, ILogger<SteamAuthService> logger)
{
    /// <summary>SteamID64 的最小值（所有 SteamID64 由此开始）。</summary>
    private const ulong MinSteamId64 = 76561197960265728;

    private static readonly string AuthenticateUserTicketPath = "/ISteamUserAuth/AuthenticateUserTicket/v1/";
    private static readonly string GetPlayerSummariesPath = "/ISteamUser/GetPlayerSummaries/v2/";

    private HttpClient Client => httpClientFactory.CreateClient("Steam");

    /// <summary>
    /// 验证票据。成功给出受信任的 SteamID64；失败区分"Steam 说这张票不行"与"根本没验成"
    /// （网络/超时/HTTP 错误/响应不可解析）。两者对外都是 401，但可诊断性与可重试性完全不同。
    /// </summary>
    public async Task<SteamTicketVerification> AuthenticateTicketAsync(string ticket, CancellationToken cancellationToken)
    {
        var steam = steamOptions.Value;
        var url = $"{AuthenticateUserTicketPath}?key={Uri.EscapeDataString(steam.ApiKey)}" +
                  $"&appid={Uri.EscapeDataString(steam.AppId)}" +
                  $"&ticket={Uri.EscapeDataString(ticket)}" +
                  $"&identity={Uri.EscapeDataString(steam.Identity)}";

        HttpResponseMessage response;
        try
        {
            response = await Client.GetAsync(url, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Steam AuthenticateUserTicket 请求失败（appid={AppId}）", steam.AppId);
            return SteamTicketVerification.Unavailable();
        }

        if (!response.IsSuccessStatusCode)
        {
            // 403/400 最常见的原因就是 Steam:ApiKey / Steam:AppId 是占位值或配错。
            logger.LogWarning(
                "Steam AuthenticateUserTicket 返回 {StatusCode}（appid={AppId} identity={Identity}；" +
                "若为 403/400 请检查 Steam:ApiKey 与 Steam:AppId）",
                (int)response.StatusCode,
                steam.AppId,
                steam.Identity);
            return SteamTicketVerification.Unavailable();
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            logger.LogWarning("Steam AuthenticateUserTicket 响应不是有效 JSON");
            return SteamTicketVerification.Unavailable();
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("response", out var responseObject) ||
                responseObject.ValueKind != JsonValueKind.Object ||
                !responseObject.TryGetProperty("params", out var parameters) ||
                parameters.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.String ||
                !string.Equals(result.GetString(), "OK", StringComparison.OrdinalIgnoreCase))
            {
                // Steam 自己会说明原因；把它连同我们的 appid/identity 一起记下来，
                // "票不对"和"我们配错了 appid/identity"从此一眼可分。
                // 这里从根节点按路径取（而不是用上面 if 里的 out 变量：短路时它们可能未赋值）。
                logger.LogWarning(
                    "Steam 票据验证未通过 result={Result} errorcode={ErrorCode} errordesc={ErrorDesc} appid={AppId} identity={Identity}",
                    ReadStringIgnoreCase(document.RootElement, "response", "params", "result") ?? "(missing)",
                    ReadStringIgnoreCase(document.RootElement, "response", "params", "error", "errorcode") ?? "(none)",
                    ReadStringIgnoreCase(document.RootElement, "response", "params", "error", "errordesc") ?? "(none)",
                    steam.AppId,
                    steam.Identity);
                return SteamTicketVerification.Rejected();
            }

            if (!parameters.TryGetProperty("steamid", out var steamIdElement) ||
                steamIdElement.ValueKind != JsonValueKind.String ||
                !IsValidSteamId64(steamIdElement.GetString(), out var steamId))
            {
                logger.LogWarning(
                    "Steam 票据验证响应缺少有效 SteamID64（appid={AppId} identity={Identity}）",
                    steam.AppId,
                    steam.Identity);
                return SteamTicketVerification.Rejected();
            }

            return SteamTicketVerification.Ok(steamId);
        }
    }

    /// <summary>按属性名（忽略大小写）取一层或两层嵌套属性并转成日志用字符串；缺失返回 null，绝不抛异常。</summary>
    private static string? ReadStringIgnoreCase(JsonElement element, params string[] path)
    {
        JsonElement current = element;
        foreach (string name in path)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            bool found = false;
            foreach (JsonProperty property in current.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    current = property.Value;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            _ => null,
        };
    }

    /// <summary>获取玩家资料；尽力而为，失败返回 (null, null) 且不影响登录。</summary>
    public async Task<(string? SteamName, string? AvatarUrl)> GetPlayerSummaryAsync(string steamId, CancellationToken cancellationToken)
    {
        var steam = steamOptions.Value;
        var url = $"{GetPlayerSummariesPath}?key={Uri.EscapeDataString(steam.ApiKey)}&steamids={steamId}";

        HttpResponseMessage response;
        try
        {
            response = await Client.GetAsync(url, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Steam GetPlayerSummaries 请求失败");
            return (null, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Steam GetPlayerSummaries 返回 {StatusCode}", (int)response.StatusCode);
            return (null, null);
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("response", out var responseObject) ||
                responseObject.ValueKind != JsonValueKind.Object ||
                !responseObject.TryGetProperty("players", out var players) ||
                players.ValueKind != JsonValueKind.Array ||
                players.GetArrayLength() == 0 ||
                players[0].ValueKind != JsonValueKind.Object ||
                !players[0].TryGetProperty("personaname", out var name) ||
                name.ValueKind != JsonValueKind.String)
            {
                return (null, null);
            }

            string? avatar = null;
            if (players[0].TryGetProperty("avatarfull", out var avatarElement))
            {
                if (avatarElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    return (null, null);
                }

                avatar = avatarElement.GetString();
            }

            return (name.GetString(), avatar);
        }
        catch (JsonException)
        {
            logger.LogWarning("Steam GetPlayerSummaries 响应不是有效 JSON");
        }

        return (null, null);
    }

    /// <summary>校验字符串是否为合法 SteamID64（17 位、不低于最小值）。</summary>
    internal static bool IsValidSteamId64(string? value, out string steamId)
    {
        steamId = string.Empty;
        if (value is null || value.Length != 17 || !ulong.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        if (parsed < MinSteamId64)
        {
            return false;
        }

        steamId = value;
        return true;
    }
}
