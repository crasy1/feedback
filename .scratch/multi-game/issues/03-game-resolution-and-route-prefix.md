# 03: Game 解析、`/g/{appId}` 路由前缀与根路径 `404 game_required`

**What to build:** 让"请求属于哪个 Game"由服务端从 URL 解析出来：新增 `GameResolver` / `ResolvedGame` / `ResolveGameFilter` 与稳定的错误码，把玩家端点挂到 `MapGroup("/g/{appId}")` 下，根路径永久移除。

**Blocked by:** 02。

**Status:** resolved

> **寻址方式在首次部署尝试之后被反转**：本 issue 最初写的是 `/g/{slug}`，现按实现改为 **Steam AppID**。理由与被否决的方案见 `docs/adr/0007-multi-game-support.md` 的 `## Revision (same day, after the first deploy attempt)`，以及 `.scratch/multi-game/spec.md` 顶部的"决策变更"。

- [ ] `Services/GameResolver.cs`：路径段（Steam AppID）→ Game 查询，**先拒非数字再查库**（`GameValidation.NormalizeAppId` + `char.IsAsciiDigit` 逐字符判断），所以"非数字段"与"没有这个 AppID"都返回 null；每请求读库，**不做进程内缓存**（凭据与启用状态一改就要立刻生效）；解密凭据失败时置 `CredentialUnreadable`，**不**在这里返回错误
- [ ] `Services/ResolvedGame.cs`：一次请求解析出的 Game + 可用凭据；**class 而不是 record**，`ToString` 不含 `ApiKey`（record 自动生成的 `ToString` 会把发行商密钥打进日志）
- [ ] `Services/GameValidation.cs`：name / AppID / identity 的校验与规范化（**没有 slug 规则**），`DefaultIdentity = "feedback-api"`；`ValidateSteamAppId` 对空值报"Steam AppID 不能为空：它同时是玩家 API 的路径段 /g/{appId}/api/..."，并拒绝非数字、超过 10 位、全 0
- [ ] `Api/ApiProblems.cs`：`ApiCodes`（`game_required` / `game_not_found` / `game_disabled` / `game_mismatch` / `credential_unreadable` / `steam_unavailable` / `steam_ticket_rejected`）、`ApiProblems.*` 构造器、`GameContextExtensions`（`SetResolvedGame` / `GetResolvedGame` / `GetTokenGameId`）；`game_not_found` 的 detail 说明请求的 AppID 并指向管理端，**不**列举已配置的 Game
- [ ] `Api/GameResolution.cs`：`ResolveGameFilter`（解析不到 → `404 game_not_found`，停用 → `403 game_disabled`；**不**在这里拦缺凭据，那是登录端点的事）；`GameTokenFilter`（claim 与解析结果不一致 → `401 game_mismatch`）
- [ ] `Program.cs`：`var playerApi = app.MapGroup("/g/{appId}").AddEndpointFilter<ResolveGameFilter>();`，`AuthEndpoints` / `FeedbackEndpoints` 映射进该组
- [ ] 根路径五个端点返回 `404` + `code = "game_required"`：显式 `MapMethods("/api", …)` 与 `MapMethods("/api/{**rest}", …)` 兜底（不要靠默认 404，否则状态码页会换掉响应体）
- [ ] 端点不再接受任何客户端提供的 Game 标识；`FeedbackService` / `PlayerService` 全部按解析出的 Game 作用域
- [ ] `/health`、`/admin`、静态资源不受影响
- [x] **修掉 `Program.cs` 里两处按 `/api` 前缀分流的 `UseWhen`**（已修复）：状态码页重执行与防伪校验现在都排除 `/g` 与 `/api`；否则玩家 API 的 4xx/403 响应体会被替换成防伪页、CSRF 中间件会跑在无 Cookie 的 JWT 请求上
- [ ] `Program.cs` 的 OpenAPI transformer 路径匹配适配新形状（`{appId}` 作为路径参数出现在文档里）
- [ ] `dotnet build` + `dotnet test` 通过（fixture 播种 Game 后既有用例改走 `/g/{appId}`）

Parent spec: `.scratch/multi-game/spec.md`

## 备注

- `404 game_required` 的语义是"这个 API 不存在于根路径"，**不要**把它做成"默认 Game 已废弃"的提示，也不要给任何重定向；迁移插入的占位 Game 只是历史数据的归属，不是兼容层——它连 AppID 都没有，任何路径都解析不到它。
- 解析结果只放 `HttpContext.Items`（`GameContextExtensions`），不要在 filter 里把 Game 塞进每个 handler 的参数里再让 handler 重新查一次——同一个请求必须只有一个 Game 来源。
- `ResolveGameFilter` 从 `RequestServices` 取 scoped 服务，不要构造注入：端点过滤器由框架在构建管线时创建一次，构造注入会把 scoped 服务捕获成单例。
- 静态资源走 `MapStaticAssets()`，在路由组之外，天然不会被 filter 捕获。
- 路径段是**客户端提供的地址，不是客户端提供的身份**：服务端拿它去查自己的库，查不到就是 404；它不携带任何权限，令牌里的 `game` claim（数字主键）才是绑定。

- 路径段最终是 **Steam AppID**（/g/{appId}），不是自造 slug；GameResolver 在查库前就拒绝非数字段，所以「非数字」与「未知 AppID」共用 404 game_not_found。另：限流分区键曾误读 RouteValues 里的 slug（改名后拿到 null，静默退化成只按玩家分区），已修为 {appId}:{sub}。

## Comments

- 2026-09-24 **已完成并验证**。下方的勾选框保留为**当时的计划原样**：其中涉及 slug 寻址的条目已被同日「改用 Steam AppID 寻址」的反转取代，盲勾会把没发生过的事记成发生过，所以保持未勾选；实际实现以 `.scratch/multi-game/spec.md` 与 `docs/adr/0007-multi-game-support.md` 的修订章节为准。
- 验证证据（整批一次性跑过）：`dotnet build` 0 警告 0 错误；`dotnet test` **216 通过 / 0 失败**（真实 Testcontainers postgres:17）；`python addons/gd_feedback/tests/verify.py` **124 项 0 失败**；`sync_addon.py --check` 一致；Godot 宿主工程 0 错误；`docker compose config` 通过；迁移在真实 PostgreSQL 上验过**全新装 / 生产升级（带存量数据）/ 回滚**三条路径；端到端（真实 PG + Release 构建）验过按 AppID 寻址，以及 `game_required` / `game_not_found` / `game_disabled` / `game_mismatch` / `steam_unavailable` 全部命中。