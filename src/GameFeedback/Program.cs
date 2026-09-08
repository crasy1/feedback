using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using GameFeedback.Api;
using GameFeedback.Components;
using GameFeedback.Data;
using GameFeedback.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddOptions<SteamOptions>()
    .Bind(builder.Configuration.GetSection("Steam"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection("Jwt"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<RateLimitOptions>()
    .Bind(builder.Configuration.GetSection("RateLimit"));

builder.Services.AddOptions<AdminOptions>()
    .Bind(builder.Configuration.GetSection("Admin"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpClient("Steam", client =>
{
    client.BaseAddress = new Uri("https://api.steampowered.com");
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddScoped<SteamAuthService>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<PlayerService>();
builder.Services.AddScoped<FeedbackService>();
builder.Services.AddScoped<AdminFeedbackService>();
builder.Services.AddScoped<AdminSeeder>();

builder.Services.AddCascadingAuthenticationState();

// 管理员：ASP.NET Core Identity + Cookie（与玩家 JWT 是两套独立身份体系）。
builder.Services.AddIdentityCore<IdentityUser>(options =>
{
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireNonAlphanumeric = false;
})
    .AddSignInManager()
    .AddEntityFrameworkStores<AppDbContext>();

builder.Services
    .AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddJwtBearer(options =>
    {
        var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()
            ?? throw new InvalidOperationException("缺少 Jwt 配置");
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            NameClaimType = "sub",
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    })
    .AddIdentityCookies();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/admin/login";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

builder.Services.AddRazorPages();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Player", policy => policy.RequireAuthenticatedUser().AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme));

var rateLimit = builder.Configuration.GetSection("RateLimit").Get<RateLimitOptions>() ?? new RateLimitOptions();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth-ip", context => RateLimitPartition.GetFixedWindowLimiter(
        // IPv4 与 IPv4-mapped IPv6 表示同一个客户端，必须共享额度。
        context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimit.AuthPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    options.AddPolicy("player-write", context => RateLimitPartition.GetFixedWindowLimiter(
        GetPlayerPartitionKey(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimit.FeedbackPer10Minutes,
            Window = TimeSpan.FromMinutes(10),
            QueueLimit = 0,
        }));
    options.AddPolicy("player-comments", context => RateLimitPartition.GetFixedWindowLimiter(
        GetPlayerPartitionKey(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimit.CommentsPer10Minutes,
            Window = TimeSpan.FromMinutes(10),
            QueueLimit = 0,
        }));
});

static string GetPlayerPartitionKey(HttpContext context) =>
    context.User.FindFirst("sub")?.Value
    ?? context.Connection.RemoteIpAddress?.ToString()
    ?? "unknown";

// 保留框架的回环信任默认值，其他反向代理必须显式配置。
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
    {
        options.KnownProxies.Add(IPAddress.Parse(proxy));
    }
});

var app = builder.Build();

// 必须最先执行，限流按 IP 分区才能取到真实客户端地址。
app.UseForwardedHeaders();

// 启动时自动应用迁移（Database__AutoMigrate 可关闭）。
if (app.Configuration.GetValue("Database:AutoMigrate", defaultValue: true))
{
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }
}

// 种子首个管理员（仅在没有任何管理员时执行）。
using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<AdminSeeder>();
    await seeder.SeedAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
// 状态码页重执行只作用于浏览器端 Blazor 页面：API 的 4xx 响应
// 必须原样返回，否则 POST /not-found 会把响应体替换成防伪错误。
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
// 限流在认证之后：按玩家（sub claim）分区需要已填充的 User。
app.UseRateLimiter();

// /admin 门禁：两套身份体系相互隔离——玩家 JWT 不满足 Identity Cookie，
// 未认证一律重定向到登录页（HTTP 层强制，与组件层防护互为纵深）。
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/admin")
        && !path.StartsWithSegments("/admin/login")
        && !path.StartsWithSegments("/admin/logout")
        && context.User.Identity?.IsAuthenticated != true)
    {
        await context.ChallengeAsync(IdentityConstants.ApplicationScheme);
        return;
    }
    await next(context);
});

// 防伪校验只作用于浏览器端 Blazor 页面；/api 走 JWT（无 Cookie），对 CSRF 免疫。
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseAntiforgery());

app.MapStaticAssets();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapAuthEndpoints();
app.MapFeedbackEndpoints();
app.MapRazorPages();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
