using System.Text.Json;
using GameFeedback.Data;
using GameFeedback.Domain;
using Microsoft.EntityFrameworkCore;

namespace GameFeedback.Services;

/// <summary>管理端列表用的凭据视图——<b>刻意不含密钥</b>，连密文都不带出服务层。</summary>
public sealed record CredentialAdminView(
    int Id,
    string Name,
    int GameCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CredentialSaveResult(CredentialAdminView? Credential, string? Error)
{
    public bool Succeeded => Error is null;
}

public enum CredentialDeleteOutcome
{
    Deleted,
    NotFound,

    /// <summary>仍被某个游戏引用——拒绝删除（外键也是 Restrict，这里是给用户看的说法）。</summary>
    InUse,
}

public enum CredentialProbeStatus
{
    Ok,
    Invalid,
    Unreachable,
    NotFound,
}

public sealed record CredentialProbeResult(CredentialProbeStatus Status, string Message);

/// <summary>
/// Steam 凭据（Publisher Web API Key）的增删改与探测。
/// <para>
/// 安全约束：明文只在这一层与 <see cref="ApiKeyProtector"/>、Steam 调用处之间流动，
/// 绝不进入任何返回值、DTO、页面或日志。后台里这个字段是只写的。
/// </para>
/// </summary>
public class SteamCredentialService(
    IDbContextFactory<AppDbContext> dbFactory,
    ApiKeyProtector protector,
    IHttpClientFactory httpClientFactory,
    ILogger<SteamCredentialService> logger)
{
    private const int ApiKeyMaxLength = 128;

    /// <summary>探测用的 SteamID64：格式合法但不会对应真实账号，所以「200 + 空 players」就是密钥有效的证据。</summary>
    private const string ProbeSteamId = "76561197960265728";

    private static readonly string GetPlayerSummariesPath = "/ISteamUser/GetPlayerSummaries/v2/";

    public async Task<IReadOnlyList<CredentialAdminView>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // 排序必须作用在实体查询上：投影成 DTO 之后再排序，EF Core 翻译不了（投影里含相关子查询）。
        return await Project(db, db.SteamCredentials.AsNoTracking().OrderBy(c => c.Name).ThenBy(c => c.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<CredentialAdminView?> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Project(db, db.SteamCredentials.AsNoTracking().Where(c => c.Id == id))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static IQueryable<CredentialAdminView> Project(AppDbContext db, IQueryable<SteamCredential> credentials) =>
        credentials.Select(c => new CredentialAdminView(
            c.Id,
            c.Name,
            db.Games.Count(g => g.CredentialId == c.Id),
            c.CreatedAt,
            c.UpdatedAt));

    public async Task<CredentialSaveResult> CreateAsync(string? name, string? apiKey, CancellationToken cancellationToken)
    {
        var nameError = ValidateName(name);
        if (nameError is not null)
        {
            return new CredentialSaveResult(null, nameError);
        }
        var keyError = ValidateApiKey(apiKey, required: true);
        if (keyError is not null)
        {
            return new CredentialSaveResult(null, keyError);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var credential = new SteamCredential
        {
            Name = name!.Trim(),
            EncryptedApiKey = protector.Protect(apiKey!.Trim()),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.SteamCredentials.Add(credential);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("管理员创建了 Steam 凭据 CredentialId={CredentialId} Name={Name}", credential.Id, credential.Name);
        return new CredentialSaveResult(await GetAsync(credential.Id, cancellationToken), null);
    }

    /// <summary><paramref name="apiKey"/> 留空表示保留原密钥（编辑名字时不必重新粘贴）。</summary>
    public async Task<CredentialSaveResult> UpdateAsync(int id, string? name, string? apiKey, CancellationToken cancellationToken)
    {
        var nameError = ValidateName(name);
        if (nameError is not null)
        {
            return new CredentialSaveResult(null, nameError);
        }

        var replacingKey = !string.IsNullOrWhiteSpace(apiKey);
        if (replacingKey)
        {
            var keyError = ValidateApiKey(apiKey, required: true);
            if (keyError is not null)
            {
                return new CredentialSaveResult(null, keyError);
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var credential = await db.SteamCredentials.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (credential is null)
        {
            return new CredentialSaveResult(null, "凭据不存在");
        }

        credential.Name = name!.Trim();
        if (replacingKey)
        {
            credential.EncryptedApiKey = protector.Protect(apiKey!.Trim());
        }
        credential.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        // 审计：只记「换了/没换」，任何情况下都不记密钥内容。
        logger.LogInformation(
            "管理员修改了 Steam 凭据 CredentialId={CredentialId} Name={Name} 密钥已更新={KeyReplaced}",
            credential.Id,
            credential.Name,
            replacingKey);

        return new CredentialSaveResult(await GetAsync(id, cancellationToken), null);
    }

    public async Task<CredentialDeleteOutcome> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var credential = await db.SteamCredentials.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (credential is null)
        {
            return CredentialDeleteOutcome.NotFound;
        }
        if (await db.Games.AnyAsync(g => g.CredentialId == id, cancellationToken))
        {
            return CredentialDeleteOutcome.InUse;
        }

        db.SteamCredentials.Remove(credential);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("管理员删除了 Steam 凭据 CredentialId={CredentialId}", id);
        return CredentialDeleteOutcome.Deleted;
    }

    /// <summary>
    /// 只读探测：拿这份凭据问一次 Steam。密钥是手工粘进去的，粘错的表现和「Steam 挂了」
    /// 完全一样（都是 401 steam_unavailable），没有这个动作管理员根本发现不了。
    /// </summary>
    public async Task<CredentialProbeResult> VerifyAsync(int id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var credential = await db.SteamCredentials.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (credential is null)
        {
            return new CredentialProbeResult(CredentialProbeStatus.NotFound, "凭据不存在");
        }

        var apiKey = protector.TryUnprotect(credential.EncryptedApiKey, credential.Id);
        if (apiKey is null)
        {
            return new CredentialProbeResult(
                CredentialProbeStatus.Unreachable,
                "凭据无法解密：Data Protection key ring 可能已丢失，恢复 key ring 后重试。");
        }

        var url = $"{GetPlayerSummariesPath}?key={Uri.EscapeDataString(apiKey)}&steamids={ProbeSteamId}";

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient("Steam").GetAsync(url, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Steam 凭据探测失败（网络）CredentialId={CredentialId}", id);
            return new CredentialProbeResult(CredentialProbeStatus.Unreachable, "无法连接 Steam，稍后重试。");
        }

        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        {
            logger.LogInformation("Steam 凭据探测：密钥无效 CredentialId={CredentialId}", id);
            return new CredentialProbeResult(CredentialProbeStatus.Invalid, "Steam 拒绝了这份密钥（无效或已撤销）。");
        }
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Steam 凭据探测返回 {StatusCode} CredentialId={CredentialId}", (int)response.StatusCode, id);
            return new CredentialProbeResult(CredentialProbeStatus.Unreachable, "Steam 没有给出结论，稍后重试。");
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("response", out var responseObject)
                && responseObject.ValueKind == JsonValueKind.Object
                && responseObject.TryGetProperty("players", out var players)
                && players.ValueKind == JsonValueKind.Array)
            {
                logger.LogInformation("Steam 凭据探测：密钥可用 CredentialId={CredentialId}", id);
                return new CredentialProbeResult(CredentialProbeStatus.Ok, "密钥可用。");
            }
        }
        catch (JsonException)
        {
            // 落到下面的统一分支。
        }

        logger.LogWarning("Steam 凭据探测响应无法解析 CredentialId={CredentialId}", id);
        return new CredentialProbeResult(CredentialProbeStatus.Unreachable, "Steam 的响应无法解析，稍后重试。");
    }

    private static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "名称不能为空";
        }
        return name.Trim().Length > 100 ? "名称长度不能超过 100" : null;
    }

    private static string? ValidateApiKey(string? apiKey, bool required)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return required ? "密钥不能为空" : null;
        }

        var trimmed = apiKey.Trim();
        if (trimmed.Length > ApiKeyMaxLength)
        {
            return $"密钥长度不能超过 {ApiKeyMaxLength}";
        }
        return trimmed.Any(char.IsWhiteSpace) ? "密钥里不能含空白字符（粘贴时可能带进了换行）" : null;
    }
}
