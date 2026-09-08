using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameFeedback.Tests.Infrastructure;

namespace GameFeedback.Tests;

/// <summary>
/// OpenAPI 文档 / Swagger UI 的暴露行为：
/// 开发环境默认开启；生产环境默认关闭（文档会暴露 API 结构），须显式配置才可访问。
/// </summary>
[Collection("Integration")]
public sealed class OpenApiEndpointTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task OpenApi_doc_and_swagger_ui_served_in_development()
    {
        // WebApplicationFactory 默认环境是 Production，须显式指定 Development。
        var factory = fixture.CreateFactory(extraSettings: settings =>
            settings["environment"] = "Development");
        var client = factory.CreateClient();

        var doc = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, doc.StatusCode);

        var body = await doc.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("paths").TryGetProperty("/api/auth/steam", out _));
        // 玩家 JWT 以 Bearer 方案呈现在文档中，供 Swagger UI 调试时授权。
        Assert.Equal("bearer", body.GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("Bearer")
            .GetProperty("scheme")
            .GetString());

        // 请求参数带中文说明、请求体带示例值（Swagger UI 调试时预填）。
        var createSchema = body.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("CreateFeedbackRequest");
        Assert.True(createSchema.GetProperty("properties")
            .TryGetProperty("title", out var titleProp) && titleProp.TryGetProperty("description", out _));
        Assert.True(createSchema.TryGetProperty("example", out var example));
        Assert.Equal("Bug", example.GetProperty("type").GetString());
        var loginSchema = body.GetProperty("components").GetProperty("schemas").GetProperty("SteamLoginRequest");
        Assert.True(loginSchema.GetProperty("properties").TryGetProperty("debugSteamId", out var debugProp)
            && debugProp.TryGetProperty("description", out _));

        // Try it out 的请求体预填取自媒体类型级 example（schema 级的 UI 不采用）。
        var feedbackMedia = body.GetProperty("paths")
            .GetProperty("/api/feedback")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json");
        Assert.Equal("Bug", feedbackMedia.GetProperty("example").GetProperty("type").GetString());
        var commentMedia = body.GetProperty("paths")
            .GetProperty("/api/feedback/{id}/comments")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json");
        Assert.True(commentMedia.TryGetProperty("example", out _));

        // 受保护端点必须声明 Bearer 安全要求（不能是空对象 {}，
        // 否则 Swagger UI 视为允许匿名、不附带 Authorization 头）。
        var mineSecurity = body.GetProperty("paths")
            .GetProperty("/api/feedback/mine")
            .GetProperty("get")
            .GetProperty("security");
        Assert.True(mineSecurity[0].TryGetProperty("Bearer", out _));

        // Swagger UI 使用中间件形式：页面由 /swagger 提供并加载 index.js，
        // 文档端点配置（SwaggerEndpoint）注入在 index.js 内。
        var ui = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Contains("html", ui.Content.Headers.ContentType?.ToString());

        var initializer = await client.GetStringAsync("/swagger/index.js");
        Assert.Contains("/openapi/v1.json", initializer);
    }

    [Fact]
    public async Task OpenApi_doc_not_exposed_in_production_by_default()
    {
        var factory = fixture.CreateFactory(extraSettings: settings =>
            settings["environment"] = "Production");
        var client = factory.CreateClient();

        var doc = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.NotFound, doc.StatusCode);

        var ui = await client.GetAsync("/swagger");
        Assert.Equal(HttpStatusCode.NotFound, ui.StatusCode);
    }

    [Fact]
    public async Task OpenApi_doc_exposed_in_production_only_when_explicitly_enabled()
    {
        var factory = fixture.CreateFactory(extraSettings: settings =>
        {
            settings["environment"] = "Production";
            settings["Swagger:Enabled"] = "true";
        });
        var client = factory.CreateClient();

        var doc = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, doc.StatusCode);
    }
}
