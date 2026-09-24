# 04: `game` 令牌声明、Steam 服务参数化与限流分区

**What to build:** 把 Game 贯穿到令牌与 Steam 调用里：令牌增加绑定 Game 的 `game` claim 并在路径上校验；`SteamAuthService` / `SteamPlaytimeService` 改为使用解析出的 `ResolvedGame`；限流分区从 SteamID 改为 (Game, SteamID)。

**Blocked by:** 03。

**Status:** resolved

> **寻址方式在首次部署尝试之后被反转**：路径段从自造 slug 改成 Steam AppID，因此限流分区键是 `{appId}:{sub}` 而不是 `slug:sub`。见 `docs/adr/0007-multi-game-support.md` 的 `## Revision (same day, after the first deploy attempt)`。

- [ ] `Services/TokenService.cs`：签发时加入 `game` claim（常量 `TokenService.GameClaimType`），值为 Game 的**数字主键**（不是路径段的 AppID：改寻址标识不该让在线令牌失效）
- [ ] `Api/GameResolution.cs` 的 `GameTokenFilter`：`http.GetTokenGameId() != game.Id` → `ApiProblems.GameMismatch()`（`401 game_mismatch`）；claim 缺失或不是整数由 `GetTokenGameId` 返回 `null`，因此同样被拒
- [ ] `MapInboundClaims = false` 下 claim 取出来是字符串：解析用 `int.TryParse` + `InvariantCulture`，不要靠隐式转换
- [ ] `SteamAuthService`：去掉通过 `IOptions<SteamOptions>` 读 `ApiKey` / `AppId` / `Identity`，改为接收 `ResolvedGame`（或它的 AppId/identity/解密后的 key）；`SteamOptions` 只保留 `DebugSkipTicketValidation`
- [ ] `SteamPlaytimeService` 同上：`appid` 与 `key` 来自解析出的 Game，`steamid` 仍只来自认证主体
- [ ] `AuthEndpoints`：`!game.IsFullyConfigured` 时 fail closed —— `CredentialUnreadable` → `401 credential_unreadable`，否则 → `401 steam_unavailable`；Development 的调试登录可在此之上工作（它不调 Steam）。**注意**：没有 AppID 的 Game 根本不会被解析出来（`GameResolver` 先拒非数字、再按 `SteamAppId` 查），所以这里实际只剩"缺凭据"一半可达；`steam_unavailable` 的 detail 文案应描述"管理端还没有可用的 Steam 凭据（Web API Key 缺失或未选择）"，不要再提"缺 AppID"
- [ ] `PlayerService.UpsertFromSteamLoginAsync` 按 `(GameId, SteamId)` upsert；`FeedbackService` 的创建/列表/详情/评论全部按 Game 作用域过滤（跨 Game 的 id 读成 `404`）
- [ ] 限流分区键 `GetPlayerPartitionKey` 从 `sub` 改成 `{appId}:{sub}`（读 `RouteValues["appId"]`；限流中间件在路由之后执行，那时路由值已可用）
- [ ] `Program.cs`：删除 `WarnAboutPlaceholderSteamConfiguration` / `IsPlaceholderSteamValue` 与 Steam 的 `ValidateOnStart`（配置里已无可校验项）
- [ ] Steam 调用失败、未配置、解密失败三种情况在日志与错误码上可区分；对玩家都是 fail closed
- [ ] `dotnet build` + `dotnet test` 通过

Parent spec: `.scratch/multi-game/spec.md`

## 备注

- 令牌已经签发出去的老客户端不会带 `game` claim，这正是"被拒 + 自愈"设计的入口，不要为了兼容去放行缺 claim 的令牌。
- 限流分区键即使解析失败也不要退化成"全局一个桶"：解析失败时请求本来就该是 401/404。`RouteValues["appId"]` 拿不到时退回只用 `sub`（等价于旧的按 SteamID 分区），不要变成常量桶。
- `SteamAuthService` 不得自己再去数据库查 Game（那会把 HTTP 集成服务和数据访问耦在一起）；Game 由调用方传入。
- `ResolvedGame` 是 class 而非 record 且 `ToString` 不含 key：任何把它交给 `ILogger` 的写法都不会泄漏发行商密钥，不要"顺手"改回 record。

- game claim 携带 Game 的**数字主键**；SteamAuthService / SteamPlaytimeService 的 AppID、identity、key 全部改为调用方按请求传入。限流分区键为 {appId}:{sub}。

## Comments

- 2026-09-24 **已完成并验证**。下方的勾选框保留为**当时的计划原样**：其中涉及 slug 寻址的条目已被同日「改用 Steam AppID 寻址」的反转取代，盲勾会把没发生过的事记成发生过，所以保持未勾选；实际实现以 `.scratch/multi-game/spec.md` 与 `docs/adr/0007-multi-game-support.md` 的修订章节为准。
- 验证证据（整批一次性跑过）：`dotnet build` 0 警告 0 错误；`dotnet test` **216 通过 / 0 失败**（真实 Testcontainers postgres:17）；`python addons/gd_feedback/tests/verify.py` **124 项 0 失败**；`sync_addon.py --check` 一致；Godot 宿主工程 0 错误；`docker compose config` 通过；迁移在真实 PostgreSQL 上验过**全新装 / 生产升级（带存量数据）/ 回滚**三条路径；端到端（真实 PG + Release 构建）验过按 AppID 寻址，以及 `game_required` / `game_not_found` / `game_disabled` / `game_mismatch` / `steam_unavailable` 全部命中。