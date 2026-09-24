using System.Net;
using GameFeedback.Tests.Infrastructure;

namespace GameFeedback.Tests;

/// <summary>
/// 管理端静态资源的接线：共享样式表 / 小脚本必须真的被服务出来，登录页必须真的引用它。
/// <para>
/// 背景：管理端页面是 InteractiveServer + <c>prerender: false</c>，HTTP 响应里没有渲染后的标记，
/// 所以"页面长什么样"没法用 HTTP 断言；但"样式表有没有被引上、有没有被服务出来"可以，
/// 而这正是把 4 处内联 &lt;style&gt; 收敛成一份 admin.css、并新增 admin.js 之后最容易悄悄坏掉的地方。
/// </para>
/// </summary>
[Collection("Integration")]
public sealed class AdminAssetTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Login_page_links_and_serves_the_shared_admin_assets()
    {
        var client = fixture.DefaultFactory.CreateClient();

        // 登录页是普通 Razor Page（Layout = null），会输出完整 HTML，因此可以直接断言。
        var login = await client.GetAsync("/admin/login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var html = await login.Content.ReadAsStringAsync();
        Assert.Contains("/admin.css", html);
        // 登录页专属样式靠这个 body 类限定作用域，别丢。
        Assert.Contains("admin-login", html);

        // 样式表确实是那一份（含状态徽章规则），而不是空文件或 404 页面。
        var css = await client.GetAsync("/admin.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Contains("badge-open", await css.Content.ReadAsStringAsync());

        var js = await client.GetAsync("/admin.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Contains("copyText", await js.Content.ReadAsStringAsync());
    }
}
