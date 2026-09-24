using System.Net;
using System.Text.RegularExpressions;
using GameFeedback.Data;
using GameFeedback.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace GameFeedback.Tests;

[Collection("Integration")]
public sealed class AdminIdentityTests(IntegrationTestFixture fixture)
{
    private const string TestEmail = "admin@test.local";
    private const string TestPassword = "admin-password-123";

    private static HttpClient NewClient(GameFeedbackApplicationFactory factory) =>
        factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

    private static async Task<string> GetAntiForgeryTokenAsync(HttpClient client, string path)
    {
        var page = await client.GetAsync(path);
        page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "登录页缺少防伪令牌");
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task Seeded_admin_can_log_in_and_access_admin()
    {
        var factory = fixture.CreateFactory();
        var client = NewClient(factory);

        var token = await GetAntiForgeryTokenAsync(client, "/admin/login");
        var response = await client.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = TestEmail,
            ["Password"] = TestPassword,
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(response.Headers.Location?.ToString().EndsWith("/admin/feedback"),
            $"登录后应重定向到 /admin/feedback，实际 {response.Headers.Location}");

        var admin = await client.GetAsync("/admin/feedback");
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    /// <summary>
    /// 首次运行：库里一个游戏都没有时，登录成功后必须被送到"添加游戏"页，
    /// 因为在那之前后台除了建游戏什么都做不了（没有任何游戏可供配置或浏览）。
    /// <para>
    /// 这里覆盖的是登录后的重定向（Razor Page，静态 SSR，HTTP 层可断）。
    /// 布局里那道守卫（<c>AdminLayout.razor</c> 的 OnAfterRenderAsync → HasAnyAsync）跑在
    /// InteractiveServer 电路里，而 App.razor 用的是 <c>prerender: false</c>，
    /// HTTP 响应里根本没有渲染后的标记，所以那道守卫只能用服务层行为来近似覆盖
    /// （见 <c>GameAdminServiceTests.HasAny_is_false_on_an_empty_database_and_true_after_the_first_game</c>）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task First_run_admin_login_redirects_to_the_add_game_page()
    {
        var factory = await fixture.CreateFactoryOnEmptyDatabaseAsync();
        var client = NewClient(factory);

        var token = await GetAntiForgeryTokenAsync(client, "/admin/login");
        var response = await client.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = TestEmail,
            ["Password"] = TestPassword,
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(response.Headers.Location?.ToString().EndsWith("/admin/games/new"),
            $"空库登录后应重定向到 /admin/games/new，实际 {response.Headers.Location}");
    }

    /// <summary>守卫会重定向的那些页面本身必须仍然可达，否则空库状态会把自己锁死（重定向死循环）。</summary>
    [Fact]
    public async Task First_run_exempt_pages_stay_reachable_on_an_empty_database()
    {
        var factory = await fixture.CreateFactoryOnEmptyDatabaseAsync();
        var client = NewClient(factory);

        var token = await GetAntiForgeryTokenAsync(client, "/admin/login");
        var login = await client.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = TestEmail,
            ["Password"] = TestPassword,
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        // 添加游戏页是空库状态下唯一的出口，必须放行（否则守卫会把它自己也拦掉 → 死循环）。
        // 页面本身是 InteractiveServer + prerender:false，HTTP 层只能验"路由与授权放行"。
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/games/new")).StatusCode);
        // 登录/登出页与静态资源同样不该被守卫波及。
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/login")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin.css")).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_admin_request_redirects_to_login()
    {
        var factory = fixture.CreateFactory();
        var client = NewClient(factory);

        var response = await client.GetAsync("/admin/feedback");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/admin/login", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Login_page_renders_simplified_chinese()
    {
        var factory = fixture.CreateFactory();
        var client = NewClient(factory);

        var page = await client.GetAsync("/admin/login");
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("管理员登录", html);
    }

    [Fact]
    public async Task Wrong_password_shows_error_without_session()
    {
        var factory = fixture.CreateFactory();
        var client = NewClient(factory);

        var token = await GetAntiForgeryTokenAsync(client, "/admin/login");
        var response = await client.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = TestEmail,
            ["Password"] = "wrong-password",
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("邮箱或密码不正确", html);

        var admin = await client.GetAsync("/admin/feedback");
        Assert.Equal(HttpStatusCode.Redirect, admin.StatusCode);
    }

    [Fact]
    public async Task Player_jwt_cannot_access_admin()
    {
        var (_, playerClient, _) = await PlayerClient.CreateAsync(fixture, PlayerClient.UniqueSteamId());
        var factory = fixture.CreateFactory();
        var adminClient = NewClient(factory);
        adminClient.DefaultRequestHeaders.Authorization = playerClient.DefaultRequestHeaders.Authorization;

        var response = await adminClient.GetAsync("/admin/feedback");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/admin/login", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Admin_can_log_out()
    {
        var factory = fixture.CreateFactory();
        var client = NewClient(factory);

        var token = await GetAntiForgeryTokenAsync(client, "/admin/login");
        await client.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = TestEmail,
            ["Password"] = TestPassword,
            ["__RequestVerificationToken"] = token,
        }));

        var logoutToken = await GetAntiForgeryTokenAsync(client, "/admin/logout");
        var logout = await client.PostAsync("/admin/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = logoutToken,
        }));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        var after = await client.GetAsync("/admin/feedback");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
    }

    [Fact]
    public async Task Seeding_never_touches_existing_installs()
    {
        // 同一数据库上先后启动两个工厂：管理员数量保持 1，不重复创建。
        var first = fixture.CreateFactory();
        _ = first.CreateClient();

        var scope = first.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var countAfterFirst = db.Users.Count();

        var second = fixture.CreateFactory();
        _ = second.CreateClient();

        db = second.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, countAfterFirst);
        Assert.Equal(1, db.Users.Count());
    }
}
