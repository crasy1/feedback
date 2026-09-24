using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using GameFeedback.Data;
using GameFeedback.Domain;
using GameFeedback.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace GameFeedback.Tests.Infrastructure;

/// <summary>
/// 测试里种下的一个 Game：玩家 API 的路径前缀（<b>Steam AppID</b>，不再是自造的 slug）与它可用的 Steam 配置。
/// 多游戏是本系统的常态，所以测试数据也必须按游戏隔离。
/// <para>
/// <see cref="AppId"/> 为 null 只可能是升级迁移为存量数据建的那行占位游戏（见 <see cref="Game.SteamAppId"/>）：
/// 那种游戏<b>根本不可寻址</b>，所以路径辅助属性遇到它会直接抛异常——
/// 免得测试悄悄拼出 <c>/g//api</c> 这种谁也匹配不上的路径，还以为在测别的东西。
/// </para>
/// </summary>
public sealed record TestGame(int Id, string? AppId, int? CredentialId, string? ApiKey)
{
    /// <summary>该游戏玩家 API 的前缀，例如 <c>/g/480/api</c>。</summary>
    public string ApiPrefix => AppId is null
        ? throw new InvalidOperationException("这个 Game 没有 SteamAppId（升级迁移的占位行），不可寻址")
        : $"/g/{AppId}/api";

    public string AuthPath => $"{ApiPrefix}/auth/steam";

    public string FeedbackPath => $"{ApiPrefix}/feedback";

    public string MinePath => $"{FeedbackPath}/mine";

    /// <summary>该游戏下的其它路径（<c>/api/...</c> 之后的部分原样拼上）。</summary>
    public string Api(string relativePath) => $"{ApiPrefix}/{relativePath.TrimStart('/')}";

    /// <summary>任意 AppID 下的玩家 API 路径。用于覆盖"这个 AppID 解析不到游戏"的分支。</summary>
    public static string PathForAppId(string appId, string relativePath) =>
        $"/g/{appId}/api/{relativePath.TrimStart('/')}";
}

/// <summary>
/// 测试数据的种入工具。走应用自己的 DI（<see cref="ApiKeyProtector"/>）加密凭据，
/// 因此测的就是真实的加密/解密路径，而不是测试自造的假密文。
/// </summary>
public static class TestGames
{
    /// <summary>种进测试库的明文密钥；断言"密钥不泄漏"时拿它做 needle。</summary>
    public const string DefaultApiKey = "test-publisher-api-key";

    public const string DefaultIdentity = "feedback-api";

    private static int _appIdSeed = Random.Shared.Next(1_000_000, 8_999_999);

    /// <summary>唯一且合法的 Steam AppID（9 位数字，绝不全为 0）——它同时是玩家 API 的路径段。</summary>
    public static string UniqueAppId() => $"9{Interlocked.Increment(ref _appIdSeed):D8}";

    /// <summary>
    /// 在库里种一个 Game（默认带一份可用的凭据与 AppID），返回它的寻址 AppID 与配置。
    /// <paramref name="withCredential"/> / <paramref name="withAppId"/> 置 false 可造出
    /// "配置未完成"的游戏：前者登录 fail closed（401 steam_unavailable），后者不可寻址
    /// ——两者是**不同**的状态，测试必须分开覆盖。
    /// </summary>
    public static async Task<TestGame> SeedAsync(
        GameFeedbackApplicationFactory factory,
        string? name = null,
        string? steamAppId = null,
        bool withAppId = true,
        bool withCredential = true,
        bool isActive = true,
        string identity = DefaultIdentity,
        string apiKey = DefaultApiKey,
        string? credentialName = null,
        CancellationToken cancellationToken = default)
    {
        // 访问 Services 会启动宿主，自动迁移在这里执行。
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ApiKeyProtector>();

        var now = DateTime.UtcNow;

        int? credentialId = null;
        if (withCredential)
        {
            var credential = new SteamCredential
            {
                Name = credentialName ?? $"cred-{Guid.NewGuid():N}"[..16],
                EncryptedApiKey = protector.Protect(apiKey),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.SteamCredentials.Add(credential);
            await db.SaveChangesAsync(cancellationToken);
            credentialId = credential.Id;
        }

        var game = new Game
        {
            Name = name ?? $"测试游戏-{Guid.NewGuid():N}"[..18],
            SteamAppId = withAppId ? steamAppId ?? UniqueAppId() : null,
            Identity = identity,
            IsActive = isActive,
            CredentialId = credentialId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(cancellationToken);

        return new TestGame(game.Id, game.SteamAppId, game.CredentialId, withCredential ? apiKey : null);
    }

    /// <summary>
    /// 直接用测试的 JWT 参数签发令牌。用于构造客户端自己造不出来的形状——
    /// 典型是"升级前签发的、没有 game 声明的旧令牌"。
    /// </summary>
    public static string MintToken(string steamId, int? gameId)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, steamId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (gameId is not null)
        {
            claims.Add(new Claim(
                TokenService.GameClaimType, gameId.Value.ToString(CultureInfo.InvariantCulture)));
        }

        var token = new JwtSecurityToken(
            issuer: GameFeedbackApplicationFactory.TestIssuer,
            audience: GameFeedbackApplicationFactory.TestAudience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(GameFeedbackApplicationFactory.TestSigningKey)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>读出响应体里的 ProblemDetails 扩展成员 <c>code</c>（稳定错误码）。</summary>
    public static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
