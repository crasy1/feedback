using System.Net;
using System.Text;
using GameFeedback.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace GameFeedback.Tests;

[Collection("Integration")]
public sealed class AuthRateLimitAndProxyTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Login_limits_are_isolated_by_ip_and_share_ipv4_mapped_addresses()
    {
        var factory = CreateFactory();

        Assert.Equal(401, (await LoginAsync(factory, "192.0.2.1")).Response.StatusCode);
        Assert.Equal(401, (await LoginAsync(factory, "192.0.2.1")).Response.StatusCode);
        Assert.Equal(429, (await LoginAsync(factory, "192.0.2.1")).Response.StatusCode);
        Assert.Equal(429, (await LoginAsync(factory, "::ffff:192.0.2.1")).Response.StatusCode);
        Assert.Equal(401, (await LoginAsync(factory, "192.0.2.2")).Response.StatusCode);
    }

    [Fact]
    public async Task Trusted_proxy_preserves_separate_client_limits()
    {
        var factory = CreateFactory();

        Assert.Equal(401, (await LoginAsync(factory, "192.0.2.10", "203.0.113.1")).Response.StatusCode);
        Assert.Equal(401, (await LoginAsync(factory, "192.0.2.10", "203.0.113.1")).Response.StatusCode);
        Assert.Equal(429, (await LoginAsync(factory, "192.0.2.10", "203.0.113.1")).Response.StatusCode);
        Assert.Equal(401, (await LoginAsync(factory, "192.0.2.10", "203.0.113.2")).Response.StatusCode);
    }

    [Fact]
    public async Task Untrusted_sender_cannot_reset_limit_with_forwarded_for()
    {
        var factory = CreateFactory();

        Assert.Equal(401, (await LoginAsync(factory, "192.0.2.11", "203.0.113.1")).Response.StatusCode);
        Assert.Equal(401, (await LoginAsync(factory, "192.0.2.11", "203.0.113.2")).Response.StatusCode);
        Assert.Equal(429, (await LoginAsync(factory, "192.0.2.11", "203.0.113.3")).Response.StatusCode);
    }

    [Theory]
    [InlineData("192.0.2.10", "203.0.113.1", "https")]
    [InlineData("127.0.0.1", "203.0.113.1", "https")]
    [InlineData("192.0.2.11", "192.0.2.11", "http")]
    public async Task Forwarded_ip_and_scheme_require_a_trusted_proxy(string remoteIp, string expectedIp, string expectedScheme)
    {
        var factory = CreateFactory();
        var result = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            context.Request.Scheme = "http";
            context.Request.Method = "GET";
            context.Request.Path = "/health";
            context.Request.Headers["X-Forwarded-For"] = "203.0.113.1";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
        });

        Assert.Equal(200, result.Response.StatusCode);
        Assert.Equal(IPAddress.Parse(expectedIp), result.Connection.RemoteIpAddress);
        Assert.Equal(expectedScheme, result.Request.Scheme);
    }

    private GameFeedbackApplicationFactory CreateFactory()
    {
        var steam = new FakeSteamHandler();
        steam.EnqueueTicketResponses(FakeSteamHandler.TicketRejected(), 4);
        return fixture.CreateFactory(steam, settings =>
        {
            settings["RateLimit:AuthPerMinute"] = "2";
            settings["ReverseProxy:KnownProxies:0"] = "192.0.2.10";
        });
    }

    private static async Task<HttpContext> LoginAsync(GameFeedbackApplicationFactory factory, string remoteIp, string? forwardedIp = null)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("{\"ticket\":\"test-ticket\"}"));
        return await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            context.Request.Scheme = "https";
            context.Request.Method = "POST";
            context.Request.Path = "/api/auth/steam";
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = body.Length;
            context.Request.Body = body;
            // SendAsync 直接构造上下文，需显式声明请求体可读。
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());
            if (forwardedIp is not null)
            {
                context.Request.Headers["X-Forwarded-For"] = forwardedIp;
                context.Request.Headers["X-Forwarded-Proto"] = "https";
            }
        });
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
