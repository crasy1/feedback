using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace GameFeedback.Tests.Infrastructure;

/// <summary>
/// 以测试专有配置托管完整应用：真实管道（路由、认证、限流），
/// 数据库指向测试容器；Steam 传输层按需打桩。
/// </summary>
public sealed class GameFeedbackApplicationFactory(string connectionString, HttpMessageHandler? steamHandler = null)
    : WebApplicationFactory<Program>
{
    public const string TestIssuer = "GameFeedback.Test";
    public const string TestAudience = "GameFeedbackClient.Test";
    public const string TestSigningKey = "test-signing-key-0123456789abcdef0123456789abcdef";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        builder.UseSetting("Steam:ApiKey", "test-api-key");
        builder.UseSetting("Steam:AppId", "480");
        builder.UseSetting("Jwt:Issuer", TestIssuer);
        builder.UseSetting("Jwt:Audience", TestAudience);
        builder.UseSetting("Jwt:SigningKey", TestSigningKey);

        if (steamHandler is not null)
        {
            builder.ConfigureServices(services =>
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(handlerBuilder =>
                        handlerBuilder.PrimaryHandler = steamHandler)));
        }
    }
}
