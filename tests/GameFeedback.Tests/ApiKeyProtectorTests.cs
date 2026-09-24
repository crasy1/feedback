using GameFeedback.Services;
using GameFeedback.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace GameFeedback.Tests;

/// <summary>
/// Steam 发行商密钥的静态加密。key ring 丢失/application name 变化会让已存凭据<b>永久</b>解不开，
/// 这条路径必须能被区分出来（否则与"Steam 挂了"长得一模一样）。
/// </summary>
[Collection("Integration")]
public sealed class ApiKeyProtectorTests(IntegrationTestFixture fixture)
{
    private static ApiKeyProtector ResolveProtector(GameFeedbackApplicationFactory factory)
    {
        _ = factory.CreateClient();
        return factory.Services.CreateScope().ServiceProvider.GetRequiredService<ApiKeyProtector>();
    }

    [Fact]
    public void Protect_and_TryUnprotect_round_trip_the_key()
    {
        var protector = ResolveProtector(fixture.DefaultFactory);
        const string apiKey = "0123456789ABCDEF0123456789ABCDEF";

        var ciphertext = protector.Protect(apiKey);

        Assert.NotEqual(apiKey, ciphertext);
        Assert.DoesNotContain(apiKey, ciphertext);
        Assert.Equal(apiKey, protector.TryUnprotect(ciphertext, credentialId: 1));
    }

    [Fact]
    public void Protect_is_non_deterministic()
    {
        // 同一份密钥两次加密必须得到不同密文（否则密文本身就能被用来比对密钥）。
        var protector = ResolveProtector(fixture.DefaultFactory);

        Assert.NotEqual(protector.Protect("same-key"), protector.Protect("same-key"));
    }

    [Fact]
    public void TryUnprotect_returns_null_for_ciphertext_from_another_purpose()
    {
        using var scope = fixture.DefaultFactory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        var protector = scope.ServiceProvider.GetRequiredService<ApiKeyProtector>();
        var foreign = provider.CreateProtector("SomeOther.Purpose.v1").Protect("secret-key");

        Assert.Null(protector.TryUnprotect(foreign, credentialId: 7));
    }

    /// <summary>
    /// 同一个 purpose、不同的 Data Protection application name：这是线上最容易踩的坑
    /// （默认 application name 取自 content root，镜像一换就变），必须解不开而不是返回垃圾。
    /// </summary>
    [Fact]
    public void TryUnprotect_returns_null_for_ciphertext_from_another_application_name()
    {
        var protector = ResolveProtector(fixture.DefaultFactory);
        var foreignDirectory = Path.Combine(
            Path.GetTempPath(), "gamefeedback-tests", $"foreign-{Guid.NewGuid():N}");
        Directory.CreateDirectory(foreignDirectory);
        try
        {
            var foreignProvider = DataProtectionProvider.Create(
                new DirectoryInfo(foreignDirectory),
                builder => builder.SetApplicationName("SomeOtherApplication"));
            var foreign = foreignProvider
                .CreateProtector("GameFeedback.SteamApiKey.v1")
                .Protect("publisher-key");

            Assert.Null(protector.TryUnprotect(foreign, credentialId: 8));
        }
        finally
        {
            Directory.Delete(foreignDirectory, recursive: true);
        }
    }

    [Fact]
    public void TryUnprotect_returns_null_instead_of_throwing_for_garbage()
    {
        var protector = ResolveProtector(fixture.DefaultFactory);

        Assert.Null(protector.TryUnprotect("not-a-protected-payload", credentialId: 9));
        Assert.Null(protector.TryUnprotect(string.Empty, credentialId: 10));
    }
}
