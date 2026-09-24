using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameFeedback.Services;
using GameFeedback.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GameFeedback.Tests;

/// <summary>
/// 密钥与认证材料绝不进日志。日志会被采集、转发、长期保留，所以"写进日志"就等于"泄漏"。
/// <para>
/// 这条断言盯着<b>真实日志管道</b>（含 EF Core 的 SQL 行与 <c>Microsoft.Extensions.Http</c> 的请求行），
/// 而不是只盯着我们自己的几个 LogInformation 调用点——Steam 的 URL 里带着 key 与 ticket，
/// 那正是最容易漏掉的一条路径。
/// </para>
/// </summary>
[Collection("Integration")]
public sealed class SecretLoggingTests(IntegrationTestFixture fixture)
{
    private const string PlaintextApiKey = "SECRET-API-KEY-0123456789ABCDEF";
    private const string TicketValue = "SECRET-TICKET-0123456789ABCDEF";

    [Fact]
    public async Task Steam_key_and_ticket_never_reach_the_log()
    {
        var game = await fixture.SeedGameAsync(apiKey: PlaintextApiKey);
        var capture = new CapturingLoggerProvider();
        var steam = new FakeSteamHandler();
        var factory = fixture.CreateFactory(steam, logCapture: capture);
        var client = factory.CreateClient();
        var steamId = PlayerClient.UniqueSteamId();

        // 登录：AuthenticateUserTicket 与 GetPlayerSummaries 都把 key / ticket 放在查询串里。
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketOk(steamId));
        steam.SetProfileResponse(FakeSteamHandler.Profile("LogProbe", null));
        var login = await client.PostAsJsonAsync(game.AuthPath, new { ticket = TicketValue });
        Assert.Equal(System.Net.HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("accessToken").GetString()!;

        // 提交反馈会再打一次 GetSingleGamePlaytime（同样带 key）。
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var created = await client.PostAsJsonAsync(game.FeedbackPath,
            new Dictionary<string, object?> { ["type"] = "Bug", ["title"] = "日志探针", ["content"] = "内容" });
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);

        // 凭据探测：又一条把密钥放进 URL 的路径。
        var credentials = factory.Services.CreateScope()
            .ServiceProvider.GetRequiredService<SteamCredentialService>();
        await credentials.VerifyAsync(game.CredentialId!.Value, CancellationToken.None);

        var lines = capture.Lines.ToList();
        Assert.NotEmpty(lines);

        AssertNoLeak(lines, PlaintextApiKey, "Steam API Key");
        AssertNoLeak(lines, TicketValue, "Steam 票据");
        AssertNoLeak(lines, token, "玩家 JWT");

        // 自检：EF Core 的 DbCommand 日志在 EnableSensitiveDataLogging 打开时会带出参数值，
        // 而凭据密文/玩家标识都会经过它——所以这条路径必须真的在采集范围内，
        // 否则上面三条断言就是空的、给不出任何保证。
        Assert.True(
            lines.Any(line => line.Contains("Microsoft.EntityFrameworkCore.Database.Command", StringComparison.Ordinal)),
            $"日志捕获里没有 EF Core 的 SQL 行，断言等于没生效。捕获到的行：\n{string.Join("\n", lines)}");

        // 更强的自检：会打印请求 URI 的那一类日志（Steam 的 key 与 ticket 就在查询串里）
        // 必须被我们的配置整个压掉，一行都不该出现。
        // 运行时确实会把查询串收敛成 "?*"，但"绝不记录票据与密钥"这条硬不变量不该依赖别人的默认行为——
        // 保证来自 appsettings*.json 里的 "System.Net.Http.HttpClient": "Warning"。
        // 这里断言那道闸真的在生效，而不是断言它恰好替我们兜住了。
        Assert.True(
            lines.All(line => !line.Contains("System.Net.Http.HttpClient", StringComparison.Ordinal)),
            $"System.Net.Http.HttpClient 的请求行没有被压掉，Steam 的 key/ticket 会转而依赖运行时脱敏。捕获到的行：\n{string.Join("\n", lines)}");
    }

    private static void AssertNoLeak(IReadOnlyList<string> lines, string secret, string what)
    {
        var leaked = lines.Where(line => line.Contains(secret, StringComparison.Ordinal)).ToList();
        Assert.True(
            leaked.Count == 0,
            $"{what} 出现在 {leaked.Count} 行日志里：\n{string.Join("\n", leaked)}\n\n全部日志行：\n{string.Join("\n", lines)}");
    }
}
