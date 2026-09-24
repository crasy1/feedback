# 03: 服务端游玩时长查询

**What to build:** 创建反馈时，服务端向 Steam 查询该玩家在本 AppId 上的累计游玩时长（分钟）并快照到这条反馈上。尽力而为：取不到就是 `null`，**绝不因此拒绝提交**。

**Blocked by:** 01、02。（两者均已 resolved）

**Status:** resolved

- [x] 新建单职责 `Services/SteamPlaytimeService.cs`（依赖 `IHttpClientFactory` + `IOptions<SteamOptions>` + `ILogger`），调用 `IPlayerService/GetSingleGamePlaytime/v1`，走已有的 `HttpClient("Steam")`（**不注册新的 HttpClient、不引入依赖**）
- [x] 参数取值：`key` = `Steam:ApiKey`、`steamid` = 认证主体的 `sub`（**绝不来自请求体**）、`appid` = `Steam:AppId`
- [x] **端点与解析路径已定稿**（issue 01 实测）：`response.playtime_forever`，整数，**分钟**。响应是**扁平对象**——没有 `games[]` 包装，也不回显 `appid`。**`0` 是合法值**（拥有但从未玩过）存 `0`；只有**字段缺失**或类型不符才存 `null`
- [x] **`GetOwnedGames` 备选已被实测否决**：同一账号同一时刻它返回 `game_count: 0`（未发行 / Playtest 应用的时长不计入零售"拥有游戏"列表），会静默给出 `null`。**不要**换回这条路
- [x] **兜底告警（必做）**：响应里取不到期望字段时，打一条 Warning 并附上**有界长度的响应正文**（响应体不含认证材料，可安全记录）。私密资料等情形会走这条路，没有告警就无法与"解析写错"区分
- [x] **3 秒预算**：`CancellationTokenSource.CreateLinkedTokenSource`；超时 / 非 2xx / JSON 异常 / 缺字段 → `null` + Warning，提交照常 `201`（不抛给调用方）
- [x] 端点编排（薄 handler）：`校验 → ResolvePlayerIdAsync → 查游玩时长 → CreateAsync`；PlayerId 解析失败直接 401，**不浪费一次 Steam 调用**
- [x] `FeedbackService.CreateAsync(int playerId, CreateFeedbackRequest request, int? playtimeMinutes, CancellationToken)`——`FeedbackService` **不得**注入 HTTP 依赖
- [x] `Infrastructure/FakeSteamHandler.cs` 增加 `GetSingleGamePlaytime` 桩：`Playtime(steamId, minutes)`、`PlaytimeMissing`（无 `playtime_forever` 字段）、`PlaytimeMalformed`、`SteamServerError` 复用；**桩体用 issue 01 的实测形状**（扁平对象、无 `appid`、无 `games[]`）。**这一步是前置条件**，否则未 stub 的调用会让所有创建反馈用例失败
- [x] 回归覆盖：取到值 → 落库并出现在响应里；**`playtime_forever: 0` → 存 `0`（不是 `null`）**；Steam 500 → `null` 但仍 `201`；缺字段 → `null`；超时 → `null`；请求参数 `steamid`/`appid` 取值正确且 `RequestedPaths` 确实包含该端点
- [x] 防伪造回归：请求体里的 `playtimeMinutes` 被忽略，落库值只来自服务端查询
- [x] 不新增限流（已受 `player-write` 5 次/10 分钟/玩家 保护，且必须持 JWT）
- [x] 结构化日志带上下文（`SteamId`、`AppId`），**不记录 key、票据、令牌**
- [x] `dotnet build` + `dotnet test` 通过

Parent spec: `.scratch/env-info-playtime-admin-ui/spec.md`

## 实施记录

- **`SteamPlaytimeService`**：3 秒预算（`LookupBudget`，公开常量便于测试引用）；调用方主动取消时**原样抛出**（不伪装成"拿不到时长"），其余失败一律 `null` + Warning。
- **兜底告警按 issue 01 的要求实现**：形状不符时把响应正文（截断到 512 字符）写进 Warning。响应体不含认证材料；没有这条日志，"字段名/形状变了"会表现成与"资料私密"完全一样的静默 `null`。
- **`FakeSteamHandler` 的取舍**：`GetSingleGamePlaytime` **不设置也有默认响应**（`Playtime(2361)`）。创建反馈总会查一次时长，若这里也像 `GetPlayerSummaries` 那样强制排队，每个不关心该字段的既有用例都得先补一句无关的桩。另加 `PlaytimeDelay` 用于覆盖超时分支。
- **新增测试（6 条）**：`Create_snapshots_steam_playtime`（含 `steamid=`/`appid=480` 参数断言）、`Create_stores_zero_playtime_as_zero`、`Create_succeeds_with_null_playtime_when_steam_fails`、`Create_succeeds_with_null_playtime_when_field_is_absent`、`Create_succeeds_with_null_playtime_when_lookup_times_out`、`Create_ignores_body_playtime_minutes`。
- **验证**：`dotnet build` 0 错误 0 警告（本 issue 新增代码）；`dotnet test` **94/94 通过**（超时用例耗时约 3 秒，即预算本身）。
