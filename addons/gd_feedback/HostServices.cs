using System.Text.Json;
using Godot;

namespace GdFeedback;

/// <summary>把插件日志接到 Godot 输出面板。只输出行为与错误码，绝不输出票据或访问令牌。</summary>
internal sealed class GodotFeedbackLog : IFeedbackLog
{
    private const string Prefix = "[gd_feedback] ";

    private readonly bool _verbose;

    public GodotFeedbackLog(bool verbose)
    {
        _verbose = verbose;
    }

    public void Info(string message)
    {
        if (_verbose)
        {
            GD.Print(Prefix + message);
        }
    }

    public void Warning(string message) => GD.PushWarning(Prefix + message);

    public void Error(string message) => GD.PushError(Prefix + message);
}

/// <summary>
/// 把访问令牌缓存到 <c>user://gd_feedback/token.json</c>（只在配置开启时使用）。
/// 缓存失败一律降级为"没有缓存"：令牌是优化，不是游戏运行的前提。
/// </summary>
internal sealed class UserTokenStore : ITokenStore
{
    private const string TokenPath = "user://gd_feedback/token.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CachedAccessToken?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(TokenPath);
            if (!File.Exists(path))
            {
                return null;
            }

            await using FileStream stream = File.OpenRead(path);
            StoredToken? stored = await JsonSerializer.DeserializeAsync<StoredToken>(stream, JsonOptions, cancellationToken);
            return stored is null || string.IsNullOrWhiteSpace(stored.AccessToken)
                ? null
                : new CachedAccessToken(stored.SteamId ?? string.Empty, stored.AccessToken, stored.ExpiresAtUtc);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 不记录内容，只记录失败类型。
            GD.PushWarning($"[gd_feedback] cached token could not be read ({ex.GetType().Name})");
            return null;
        }
    }

    public async Task SaveAsync(CachedAccessToken token, CancellationToken cancellationToken)
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(TokenPath);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 先写临时文件再覆盖，避免半截文件被当成有效令牌读回。
            string temporaryPath = path + ".tmp";
            await using (FileStream stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new StoredToken(token.SteamId, token.AccessToken, token.ExpiresAtUtc),
                    JsonOptions,
                    cancellationToken);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"[gd_feedback] cached token could not be written ({ex.GetType().Name})");
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(TokenPath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"[gd_feedback] cached token could not be removed ({ex.GetType().Name})");
        }
        return Task.CompletedTask;
    }

    private sealed record StoredToken(string? SteamId, string? AccessToken, DateTimeOffset ExpiresAtUtc);
}
