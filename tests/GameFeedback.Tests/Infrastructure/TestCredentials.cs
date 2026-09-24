using GameFeedback.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GameFeedback.Tests.Infrastructure;

/// <summary>
/// 通过管理端服务（<see cref="SteamCredentialService"/>）种一份凭据。
/// 走的是真实加密路径（Data Protection + <see cref="ApiKeyProtector"/>），
/// 而不是测试自造的假密文。
/// </summary>
public static class TestCredentials
{
    /// <summary>建一份凭据并返回它的管理端视图（<b>刻意不含密钥</b>；明文由调用方自己留着当断言用的 needle）。</summary>
    public static async Task<CredentialAdminView> CreateAsync(
        IntegrationTestFixture fixture,
        string? name = null,
        string? apiKey = null,
        GameFeedbackApplicationFactory? factory = null)
    {
        var target = factory ?? fixture.DefaultFactory;
        _ = target.CreateClient();
        var service = target.Services.CreateScope().ServiceProvider.GetRequiredService<SteamCredentialService>();

        var result = await service.CreateAsync(
            name ?? $"cred-{Guid.NewGuid():N}"[..12],
            apiKey ?? TestGames.DefaultApiKey,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        return result.Credential!;
    }
}
