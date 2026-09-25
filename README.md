# Steam Game Feedback System

轻量级、自托管的 Steam 游戏反馈系统：玩家通过 Steam 认证提交反馈，管理员在 Blazor 后台审阅、回复并跟踪状态。
一个部署可以同时服务**多个 Steam 游戏**（多 AppID）：每个游戏有独立的 Steam AppID 与凭据，玩家与反馈按游戏隔离；客户端用自己的 AppID 寻址，不需要手抄任何标识。

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
- Swagger UI：`http://localhost:5087/swagger`（开发环境默认开启，可直接调试玩家 API）
- 管理后台：`/admin`（首次启动自动创建初始管理员，账号来自 `Admin` 配置段）

### 本地测试环境变量（`.env.local`）

仓库根有一份本地生成的 `.env.local`（**不在版本库里**，已被 `.gitignore` 忽略），内容是 **dev-only** 的本地测试配置：Development、跳过 Steam 验票、开 Swagger、本地 JWT 密钥与管理员账号，全部是假值。

应用本身不读 `.env` 文件（ASP.NET Core 只读 appsettings 与环境变量），docker compose 也只在显式 `--env-file` 时读它——所以用下面这条命令或脚本消费它：

```bash
# 本地测试主路径：compose 起最新构建（应用在 http://127.0.0.1:3000）
docker compose --env-file .env.local up -d --build
python scripts/run_local.py --docker    # 同一条命令，成功后多打印一次 ps 与测试台提示

# 不起容器、直接跑应用（http://localhost:5087）
python scripts/run_local.py
python scripts/run_local.py --dry-run    # 只打印解析后的配置（密钥打码），不启动任何东西
```

起来后手动测试用 [tests/godot-feedback-host/](tests/godot-feedback-host/README.md)：那个 Godot 测试台的 BaseUrl 默认就是 `http://127.0.0.1:3000`，勾「调试登录」即可，先点「健康检查」确认 `/health` 通了。

脚本按 `docker-compose.yml` 的**同一套映射与默认值**把 compose 风格变量翻成 ASP.NET Core 配置键，并提前复现应用的启动校验（例如 `STEAM_DEBUG_SKIP=true` 必须配 `ASPNETCORE_ENVIRONMENT=Development`）。

> 注意：`POSTGRES_PASSWORD` 只在数据卷**首次初始化**时生效。若你已有的 `postgres-data` 卷是用 `.env` 里的密码建的，请把同一个密码填进 `.env.local`，或执行 `docker compose --env-file .env.local down -v` 重建（会删除本地测试数据）。

## 集成测试

```bash
dotnet test
```

集成测试通过 Testcontainers 启动真实 PostgreSQL 容器，**运行前需要 Docker Desktop 已启动**；Steam 响应在 HTTP 传输层打桩，无需真实 Steam 凭据。

对运行中的实例做端到端手工验证（登录 → 建反馈 → 所有权/校验/限流负例）：

```bash
python scripts/smoke_player_api.py     # 需要应用已在 http://localhost:5087 运行
```

更多测试方式（Swagger UI、真实票据路径）见 [docs/testing.md](docs/testing.md)。

## 全栈部署

```bash
cp .env.example .env   # 填写 POSTGRES_PASSWORD、JWT_SIGNING_KEY（≥32 字符）、ADMIN_SEED_PASSWORD
docker compose up -d --build
curl http://127.0.0.1:3000/health
```

### 首次配置（部署后必做）

Steam 的凭据、AppID 与票据 identity **不在环境变量里**，而是按游戏存放在数据库中，由管理员在后台维护。
所以一个全新部署在管理员配置完之前，**玩家登录会 fail closed**（`401 steam_unavailable`）：

1. 打开 `/admin/login` 登录（账号来自 `ADMIN_SEED_EMAIL` / `ADMIN_SEED_PASSWORD`）。
2. 数据库里还没有游戏时，后台会把你强制送到 **添加游戏** 页。
3. 先在 **Steam 凭据** 页添加 Publisher Web API Key（密文入库、界面不回显；保存后点「验证」确认可用），
   再回到 **游戏** 页填写名称、**Steam AppID**、票据 identity 并选中这份凭据（AppID 就是玩家 API 的路径段）。
4. 把该游戏客户端构建的 `FeedbackConfig.BaseUrl` 指向 `https://<域名>`（**不要**带 `/g/...`），
   并在配置文件（`feedback_config.tres`，或项目根下的短名 `feedback.tres`）里填上该游戏的
   **Steam AppID**——路径前缀由插件自动补全。
   （AppID 只在运行时才确定时，改成在宿主里实现 `IGameAppIdProvider`，并把配置里的值留空。）

备份时**必须一并备份 Data Protection key ring 卷**（compose 里的 `data-protection-keys`）：
丢了它，数据库里已存的 Steam 凭据将永久无法解密。

- 数据库在 Docker 网络内部，应用容器的 `8080` 映射至本机回环的 `3000`；公网入口由 Cloudflare Tunnel / Nginx / Caddy 承担。
- 转发头仅接受回环或显式配置的代理。使用 Docker 时，将 `.env` 的 `REVERSE_PROXY_IP` 设置为应用实际看到的代理来源 IP（可能是网桥网关）；留空不会信任外部来源。可通过 `docker network inspect <网络名>` 核对网关与代理地址。代理须正确设置客户端 IP 和协议，否则 HTTPS 识别及按 IP 登录限流无法反映真实客户端。
- 迁移在应用启动时自动执行（`Database__AutoMigrate` 可关闭）。
- Swagger UI 在生产环境默认关闭；将 `.env` 的 `SWAGGER_ENABLED` 设为 `true` 可开启（文档会暴露 API 结构，仅限内网/受信网络调试，不要经公网入口暴露）。

## 配置项

| 键 | 说明 |
|---|---|
| `ConnectionStrings__DefaultConnection` | PostgreSQL 连接字符串 |
| `Steam__DebugSkipTicketValidation` | 调试开关：跳过 Steam 验票（登录请求带 `debugSteamId`）。仅 Development 可启用，生产配置会拒绝启动 |
| `DataProtection__KeysPath` | Data Protection key ring 目录，默认 `<内容根>/keys`；compose 固定为 `/app/keys` 并挂持久卷 |
| `Jwt__Issuer` / `Jwt__Audience` / `Jwt__SigningKey` | 访问令牌签发配置（HS256，24 小时有效） |
| `Admin__SeedEmail` / `Admin__SeedPassword` | 首个管理员（仅在数据库无管理员时创建） |
| `Database__AutoMigrate` | 启动时自动迁移，默认 true |
| `Swagger__Enabled` | Swagger UI / OpenAPI 文档；开发环境默认开启，其他环境默认关闭 |
| `RateLimit__AuthPerMinute` 等 | 限流配置，见 `docs/specs/player-api.md` |
| `ReverseProxy__KnownProxies__0` 等 | 额外可信代理来源 IP；Compose 使用 `REVERSE_PROXY_IP`，默认仅回环 |

**Steam 相关配置不是环境变量**：Publisher Web API Key（密文）、AppID 与票据 identity 都按游戏存在数据库里，
在后台的「Steam 凭据」与「游戏」页面维护，改完立即生效、无需重启。

**严禁**把真实密钥提交进仓库；`.env` / Data Protection key ring 都已在 `.gitignore` 中。

## 玩家 API（v1）

玩家 API 一律挂在游戏的 **Steam AppID** 之下：

```text
POST /g/{appId}/api/auth/steam            { ticket }        → 访问令牌 + 玩家资料
POST /g/{appId}/api/feedback              创建反馈（Bearer 令牌）
GET  /g/{appId}/api/feedback/mine         自己的最近 100 条
GET  /g/{appId}/api/feedback/{id}         反馈详情（含评论；非本人返回 404）
POST /g/{appId}/api/feedback/{id}/comments 追加评论（仅限反馈属主）
```

根路径上的 `/api/...` 已**永久移除**，返回 `404` 与 `code = game_required`。
访问令牌里带 `game` 声明：一个游戏里换来的令牌拿到另一个游戏的路径下使用会被拒（`401 game_mismatch`）。

详细契约与错误码：[docs/specs/player-api.md](docs/specs/player-api.md)。Godot 客户端把 `FeedbackConfig.BaseUrl`
指向反馈服务地址（**不带** `/g/...`），AppID 由配置资源（`feedback_config.tres` / `feedback.tres`）的 `SteamAppId` 提供
（AppID 只在运行时才确定时改用 `IGameAppIdProvider` 注入），
插件自动补全路径；再用 `GetAuthTicketForWebApi(identity)` 取票据（默认 identity 为 `feedback-api`）。

## Godot 客户端插件

玩家侧对接已随本仓库提供：[addons/gd_feedback/](addons/gd_feedback/README.md)（Godot 4.7 / .NET 10 插件，零宿主依赖、核心引擎无关）。
安装到游戏项目：把 `addons/gd_feedback/` 复制到目标项目的 `addons/` 下，构建 C# 项目后在 Plugins 里启用。设计与取舍见 [docs/adr/0004](docs/adr/0004-godot-feedback-client-addon.md)。

```bash
python addons/gd_feedback/tests/verify.py     # 离线验证（身份/白名单、引擎无关核心、干净宿主 fixture、引擎内探针）
python addons/gd_feedback/tools/package.py    # 按 manifest 白名单打包到 artifacts/
```

已经装了插件的**完整 Godot 4.7.2 .NET 宿主工程**在 [tests/godot-feedback-host/](tests/godot-feedback-host/README.md)：用 Godot 打开即可交互联调（调试登录 / Steam 出票 / 手输票据，提交·列表·详情·评论，错误码直接打在日志区），也能无界面自检：

```bash
godot-mono.console.exe --headless --path tests/godot-feedback-host res://Main.tscn -- --lab-selfcheck
python tests/godot-feedback-host/tools/sync_addon.py -Check   # 校验宿主里的插件副本没有漂移
```
