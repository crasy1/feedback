using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using GameFeedback.Api;
using GameFeedback.Components;
using GameFeedback.Contracts.Requests;
using GameFeedback.Data;
using GameFeedback.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddOptions<SteamOptions>()
    .Bind(builder.Configuration.GetSection("Steam"))
    // 跳过 Steam 验票的调试开关只允许在 Development 生效：生产配置了它直接拒绝启动，
    // 保证“Steam 验证不可用时不认证”这条安全不变量不被配置绕过。
    .Validate(
        options => !options.DebugSkipTicketValidation || builder.Environment.IsDevelopment(),
        "Steam:DebugSkipTicketValidation 只能在 Development 环境启用")
    .ValidateOnStart();

// Steam 凭据（Publisher Web API Key）加密用的 Data Protection。
// application name 必须显式钉死：默认值取自 content root 路径，镜像一换就可能变，
// 而 key ring 是按 application name 派生隔离的——一旦对不上，已存凭据将永久无法解密。
// key ring 本身必须落在持久卷上并纳入备份（见 docker-compose.yml 的 keys 卷）。
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    dataProtectionKeysPath = Path.Combine(builder.Environment.ContentRootPath, "keys");
}
Directory.CreateDirectory(dataProtectionKeysPath);
builder.Services.AddDataProtection()
    .SetApplicationName("GameFeedback")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));

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
builder.Services.AddScoped<SteamPlaytimeService>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<PlayerService>();
builder.Services.AddScoped<FeedbackService>();
builder.Services.AddScoped<AdminFeedbackService>();
builder.Services.AddScoped<GameResolver>();
builder.Services.AddScoped<GameAdminService>();
builder.Services.AddScoped<SteamCredentialService>();
builder.Services.AddScoped<ApiKeyProtector>();
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

/// <summary>
/// 玩家侧写入的分区键：按 (Game, SteamID) 拆开，避免一个游戏里的提交吃掉另一个游戏的额度；
/// 路由值在限流中间件运行时已经可用（限流中间件在路由之后执行）。
/// </summary>
static string GetPlayerPartitionKey(HttpContext context)
{
    var steamId = context.User.FindFirst("sub")?.Value
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";
    var appId = context.Request.RouteValues["appId"] as string;
    return string.IsNullOrEmpty(appId) ? steamId : $"{appId}:{steamId}";
}

// 保留框架的回环信任默认值，其他反向代理必须显式配置。
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
    {
        options.KnownProxies.Add(IPAddress.Parse(proxy));
    }
});

// OpenAPI 文档 + Swagger UI：开发环境默认启用；生产环境默认关闭，
// 需显式配置 Swagger:Enabled=true（文档会暴露 API 结构，只应在受信网络开启）。
var enableSwaggerDocs = builder.Environment.IsDevelopment()
    || builder.Configuration.GetValue("Swagger:Enabled", defaultValue: false);
// Swagger 请求体示例里的默认调试 SteamID64（仅演示格式，联调时可改）。
const string PlayerClientDebugExampleSteamId = "76561198765432109";

static JsonObject LoginRequestExample() => new()
{
    ["ticket"] = "0b4b1f2a3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f",
    ["debugSteamId"] = PlayerClientDebugExampleSteamId,
};

static JsonObject FeedbackRequestExample() => new()
{
    ["type"] = "Bug",
    ["title"] = "进入竞技场时客户端崩溃",
    ["content"] = "1v1 模式加载 arena_01 必现崩溃，普通对局不受影响。",
    ["gameVersion"] = "1.2.3",
    ["buildNumber"] = "456",
    ["operatingSystem"] = "Windows 11",
    ["gpu"] = "RTX 4070",
    ["cpu"] = "Intel(R) Core(TM) i7-6700K CPU @ 4.00GHz",
    ["memoryTotalMb"] = 16384,
    ["locale"] = "zh-CN",
    ["map"] = "arena_01",
    ["character"] = "mage",
};

static JsonObject CommentRequestExample() => new()
{
    ["content"] = "补充：重启后问题依旧，驱动已是最新。",
};

// 玩家 API 的路径现在都带 /g/{appId} 前缀，所以按后缀匹配而不是全等。
static JsonObject? RequestBodyExampleFor(string relativePath) => relativePath switch
{
    _ when relativePath.EndsWith("/api/auth/steam", StringComparison.Ordinal) => LoginRequestExample(),
    _ when relativePath.EndsWith("/api/feedback", StringComparison.Ordinal) => FeedbackRequestExample(),
    _ when relativePath.EndsWith("/api/feedback/{id}/comments", StringComparison.Ordinal) => CommentRequestExample(),
    _ => null,
};

// ApiDescription.RelativePath 与文档路径键有差异：可能带尾部斜杠、
// 含 ":int" 这类路由约束。统一成文档路径键的形状再匹配。
static string NormalizeOperationPath(string relativePath) =>
    "/" + relativePath.TrimStart('/').TrimEnd('/').Replace(":int", string.Empty);
if (enableSwaggerDocs)
{
    builder.Services.AddOpenApi(options =>
    {
        // 注册 Bearer 安全方案，只附加到需要玩家身份的反馈端点；
        // Steam 登录端点是换取令牌的入口，本身不要求授权。
        options.AddDocumentTransformer((document, context, cancellationToken) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "先调用 POST /api/auth/steam 获取 accessToken，再粘贴到此处（不含 Bearer 前缀）",
            };
            foreach (var (pathKey, pathItem) in document.Paths)
            {
                if (!pathKey.Contains("/api/feedback", StringComparison.Ordinal))
                {
                    continue;
                }
                if (pathItem.Operations is null)
                {
                    continue;
                }
                foreach (var operation in pathItem.Operations.Values)
                {
                    // v2 的引用序列化需要宿主文档，缺省时会把安全要求写成空对象 {}，
                    // Swagger UI 会把 {} 理解为"允许匿名"而不附带 Authorization 头。
                    operation.Security =
                    [
                        new OpenApiSecurityRequirement
                        {
                            [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
                        },
                    ];
                }
            }
            return Task.CompletedTask;
        });

        // 为请求 DTO 填充示例值：模型（Schema）文档里展示。
        options.AddSchemaTransformer((schema, context, cancellationToken) =>
        {
            if (context.JsonTypeInfo.Type == typeof(SteamLoginRequest))
            {
                schema.Example = LoginRequestExample();
            }
            else if (context.JsonTypeInfo.Type == typeof(CreateFeedbackRequest))
            {
                schema.Example = FeedbackRequestExample();
            }
            else if (context.JsonTypeInfo.Type == typeof(CreateCommentRequest))
            {
                schema.Example = CommentRequestExample();
            }
            return Task.CompletedTask;
        });

        // Swagger UI"Try it out"的请求体预填取自媒体类型级 example（schema 级的
        // example 只在模型文档展示，且我们的请求体 schema 是 oneOf 包裹），
        // 因此必须在 operation 上再写一份。
        options.AddOperationTransformer((operation, context, cancellationToken) =>
        {
            if (operation.RequestBody?.Content?.TryGetValue("application/json", out var media) == true
                && media is not null)
            {
                media.Example = RequestBodyExampleFor(NormalizeOperationPath(context.Description.RelativePath ?? string.Empty));
            }
            return Task.CompletedTask;
        });
    });
}

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
/// <summary>
/// 玩家 API 的路径前缀。玩家路由现在都在 <c>/g/{appId}/api/...</c> 下，
/// 所以"这是不是玩家 API 请求"必须同时看两个前缀——只看 <c>/api</c> 会把玩家请求当成浏览器页面处理。
/// </summary>
static bool IsPlayerApiRequest(HttpContext context) =>
    context.Request.Path.StartsWithSegments("/g")
    || context.Request.Path.StartsWithSegments("/api");

// 状态码页重执行只作用于浏览器端 Blazor 页面：API 的 4xx 响应
// 必须原样返回，否则 POST /not-found 会把响应体替换成防伪错误。
app.UseWhen(
    context => !IsPlayerApiRequest(context),
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

// 防伪校验只作用于浏览器端 Blazor 页面；玩家 API 走 JWT（无 Cookie），对 CSRF 免疫。
app.UseWhen(
    context => !IsPlayerApiRequest(context),
    branch => branch.UseAntiforgery());

app.MapStaticAssets();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// 玩家 API 一律挂在 /g/{appId} 之下：客户端本来就运行在某个 Steam AppID 下，由宿主把它交给
// 插件自动补全路径，接入时不必手抄任何标识字符串。
var playerApi = app.MapGroup("/g/{appId}")
    .AddEndpointFilter<ResolveGameFilter>();
playerApi.MapAuthEndpoints();
playerApi.MapFeedbackEndpoints();

// 根路径上的玩家 API 已永久移除。这里给出稳定的 game_required（而不是一个空白 404），
// 因为这次变更之后最常见的线上故障就是「某个游戏的构建还把 BaseUrl 指向根路径」。
foreach (var method in new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" })
{
    app.MapMethods("/api", [method], () => ApiProblems.GameRequired()).ExcludeFromDescription();
    app.MapMethods("/api/{**rest}", [method], () => ApiProblems.GameRequired()).ExcludeFromDescription();
}

if (enableSwaggerDocs)
{
    app.MapOpenApi();
    // 不用 MapSwaggerUI：其端点路由实现存在 catch-all 路由缺陷（UI 页面 404），
    // 中间件形式由内嵌资源直接服务页面与资产，行为稳定。
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Player API v1"));
}

app.MapRazorPages();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
