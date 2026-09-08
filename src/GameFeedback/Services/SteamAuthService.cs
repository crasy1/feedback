using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace GameFeedback.Services;

/// <summary>
/// Steam 票据验证与资料获取。验证失败一律返回 null（fail closed），
/// 绝不信任调用方提供的 SteamID，也不签发令牌、不写 HTTP 响应。
/// </summary>
public class SteamAuthService(IHttpClientFactory httpClientFactory, IOptions<SteamOptions> steamOptions, ILogger<SteamAuthService> logger)
{
    /// <summary>SteamID64 的最小值（所有 SteamID64 由此开始）。</summary>
    private const ulong MinSteamId64 = 76561197960265728;

    private static readonly string AuthenticateUserTicketPath = "/ISteamUserAuth/AuthenticateUserTicket/v1/";
    private static readonly string GetPlayerSummariesPath = "/ISteamUser/GetPlayerSummaries/v2/";

    private HttpClient Client => httpClientFactory.CreateClient("Steam");

    /// <summary>验证票据；成功返回受信任的 SteamID64，任何失败返回 null。</summary>
    public async Task<string?> AuthenticateTicketAsync(string ticket, CancellationToken cancellationToken)
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
            logger.LogWarning(ex, "Steam AuthenticateUserTicket 请求失败");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Steam AuthenticateUserTicket 返回 {StatusCode}", (int)response.StatusCode);
            return null;
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            logger.LogWarning("Steam AuthenticateUserTicket 响应不是有效 JSON");
            return null;
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
                logger.LogInformation("Steam 票据验证未通过");
                return null;
            }

            if (!parameters.TryGetProperty("steamid", out var steamIdElement) ||
                steamIdElement.ValueKind != JsonValueKind.String ||
                !IsValidSteamId64(steamIdElement.GetString(), out var steamId))
            {
                logger.LogInformation("Steam 票据验证响应缺少有效 SteamID64");
                return null;
            }

            return steamId;
        }
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
