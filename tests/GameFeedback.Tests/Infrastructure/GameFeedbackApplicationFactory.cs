using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GameFeedback.Tests.Infrastructure;

/// <summary>
/// 以测试专有配置托管完整应用：真实管道（路由、认证、限流），
/// 仅连接字符串指向测试容器；Steam 传输层在各用例中按需打桩。
/// </summary>
public sealed class GameFeedbackApplicationFactory(string connectionString)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
    }
}
