# Steam Game Feedback System

轻量级、自托管的 Steam 游戏反馈系统：玩家通过 Steam 认证提交反馈，管理员在 Blazor 后台审阅、回复并跟踪状态。

架构与设计文档：[docs/overview.md](docs/overview.md) · 安全设计：[docs/security.md](docs/security.md) · 领域模型：[docs/domain-model.md](docs/domain-model.md) · 决策记录：[docs/adr/](docs/adr/)

## 技术栈

- .NET 10 · ASP.NET Core Minimal APIs · Blazor Web App（Interactive Server 管理端）
- EF Core + Npgsql / PostgreSQL 17
- 玩家：Steam 票据验证 → 本地短期 JWT；管理员：ASP.NET Core Identity + Cookie
- Docker Compose 部署（app + postgres）

## 开发流程

```bash
# 1. 启动 PostgreSQL（仅暴露到本机回环）
docker compose up -d postgres

# 2. 本地应用配置
cp appsettings.example.json src/GameFeedback/appsettings.Development.json  # 按需修改

# 3. 运行应用（启动时自动应用数据库迁移）
dotnet run --project src/GameFeedback
```

- 健康检查：`curl http://localhost:5087/health`（端口见 launchSettings）
- 管理后台：`/admin`（首次启动自动创建初始管理员，账号来自 `Admin` 配置段）

## 集成测试

```bash
dotnet test
```

集成测试通过 Testcontainers 启动真实 PostgreSQL 容器，**运行前需要 Docker Desktop 已启动**；Steam 响应在 HTTP 传输层打桩，无需真实 Steam 凭据。

## 全栈部署

```bash
cp .env.example .env   # 填写 POSTGRES_PASSWORD、JWT_SIGNING_KEY（≥32 字符）、ADMIN_SEED_PASSWORD、Steam 密钥
docker compose up -d --build
curl http://127.0.0.1:8080/health
```

- 数据库在 Docker 网络内部，应用容器仅向本机回环暴露 `8080`；公网入口由 Cloudflare Tunnel / Nginx / Caddy 承担，应用已支持 `X-Forwarded-For` / `X-Forwarded-Proto`。
- 迁移在应用启动时自动执行（`Database__AutoMigrate` 可关闭）。

## 配置项

| 键 | 说明 |
|---|---|
| `ConnectionStrings__DefaultConnection` | PostgreSQL 连接字符串 |
| `Steam__ApiKey` / `Steam__AppId` | Steam Publisher Web API Key / 游戏 AppID |
| `Steam__Identity` | 票据 identity，固定 `feedback-api` |
| `Jwt__Issuer` / `Jwt__Audience` / `Jwt__SigningKey` | 访问令牌签发配置（HS256，24 小时有效） |
| `Admin__SeedEmail` / `Admin__SeedPassword` | 首个管理员（仅在数据库无管理员时创建） |
| `Database__AutoMigrate` | 启动时自动迁移，默认 true |
| `RateLimit__AuthPerMinute` 等 | 限流配置，见 `docs/specs/player-api.md` |

**严禁**把真实密钥提交进仓库；生产配置一律通过环境变量 / `.env` 提供。

## 玩家 API（v1）

```text
POST /api/auth/steam            { ticket }        → 访问令牌 + 玩家资料
POST /api/feedback              创建反馈（Bearer 令牌）
GET  /api/feedback/mine         自己的最近 100 条
GET  /api/feedback/{id}         反馈详情（含评论；非本人返回 404）
POST /api/feedback/{id}/comments 追加评论（仅限反馈属主）
```

详细契约与限额：[docs/specs/player-api.md](docs/specs/player-api.md)。Godot 客户端通过 `GetAuthTicketForWebApi("feedback-api")` 获取票据后调用登录端点。
