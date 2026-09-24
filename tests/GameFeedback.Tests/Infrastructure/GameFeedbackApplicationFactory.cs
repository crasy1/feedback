using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace GameFeedback.Tests.Infrastructure;

/// <summary>
/// 以测试专有配置托管完整应用：真实管道（路由、认证、限流），
/// 数据库指向测试容器；Steam 传输层按需打桩。
/// </summary>
public sealed class GameFeedbackApplicationFactory(
    string connectionString,
    HttpMessageHandler? steamHandler = null,
    IReadOnlyDictionary<string, string>? extraSettings = null,
    CapturingLoggerProvider? logCapture = null)
    : WebApplicationFactory<Program>
{
    public const string TestIssuer = "GameFeedback.Test";
    public const string TestAudience = "GameFeedbackClient.Test";
    public const string TestSigningKey = "test-signing-key-0123456789abcdef0123456789abcdef";

    /// <summary>
    /// 本次测试运行共享的 Data Protection key ring 目录。
    /// <para>
    /// 必须<b>全进程共享一份</b>：游戏的 Steam 凭据是用这个 key ring 加密的，
    /// 每个工厂各用一份 key ring 就等于线上"key ring 丢了"——夹具里种下的游戏
    /// 在别的工厂里会解不开凭据，登录直接 401 credential_unreadable。
    /// </para>
    /// <para>
    /// 指向临时目录（每次测试运行换一个），既不写进仓库，也不会与另一次运行相撞，
    /// 更不会去用应用默认的 <c>&lt;contentRoot&gt;/keys</c> 而把仓库搞脏。
    /// </para>
    /// </summary>
    public static string DataProtectionKeysPath { get; } =
        Path.Combine(Path.GetTempPath(), "gamefeedback-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        // Steam:ApiKey / Steam:AppId / Steam:Identity 已不存在：AppID、票据 identity 与
        // 发行商密钥都按 Game 存在数据库里，测试要在库里种 Game + 凭据（见 TestGames）。
        builder.UseSetting("DataProtection:KeysPath", DataProtectionKeysPath);
        builder.UseSetting("Jwt:Issuer", TestIssuer);
        builder.UseSetting("Jwt:Audience", TestAudience);
        builder.UseSetting("Jwt:SigningKey", TestSigningKey);
        builder.UseSetting("Admin:SeedEmail", "admin@test.local");
        builder.UseSetting("Admin:SeedPassword", "admin-password-123");

        if (extraSettings is not null)
        {
            foreach (var (key, value) in extraSettings)
            {
                builder.UseSetting(key, value);
            }
        }

        if (steamHandler is not null)
        {
            builder.ConfigureServices(services =>
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(handlerBuilder =>
                        handlerBuilder.PrimaryHandler = steamHandler)));
        }

        if (logCapture is not null)
        {
            // 走真实日志管道（含 Microsoft.Extensions.Http 对请求行的 Information 日志），
            // 而不是只盯着我们自己的 LogInformation 调用点。
            builder.ConfigureLogging(logging => logging.AddProvider(logCapture));
        }
    }
}
