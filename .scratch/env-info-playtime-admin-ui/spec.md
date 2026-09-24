# Spec: 环境信息、游玩时长与管理端玩家筛选

> **已被后续升级部分取代（2026-09-24，多游戏支持）**：本 spec 里「`players` 表不加字段、不改迁移」「`SteamId` 全局唯一」「`Steam:ApiKey` 是部署配置」这三条**不再成立**。现状：`players` 增加了必填 `GameId`、唯一索引改为 `(GameId, SteamId)` 复合；玩家 API 移到 `/g/{appId}/api/...`（按 Steam AppID 寻址）；Steam 的 AppID、identity 与 Publisher Key 已按游戏存进数据库、由管理端维护。本文件作为**历史记录**保留原样，权威说明见 `docs/adr/0007-multi-game-support.md`、`docs/adr/0008-steam-configuration-in-the-database.md` 与 `.scratch/multi-game/spec.md`。

Status: resolved

来源：一次 grilling 会话（决策树全部落定）。原始需求："增加用户表，首次提交反馈时记录用户 steamId、昵称、头像；反馈接口中加上用户反馈时 steam 游玩游戏的时间；优化管理员界面 UI，可以通过用户筛选；反馈增加 CPU、GPU、内存信息。"

## Problem Statement

管理员在分诊一条反馈时，看不到"这个玩家玩了多久"和"他的机器是什么配置"：游玩时长完全没有记录；CPU 和内存也没有；GPU 字段虽然端到端存在，但**从来没有任何客户端代码填过它**，所以实际上一律为空。结果是"新手第一小时的崩溃"与"老玩家的边缘 case"混在一起，硬件相关的崩溃只能靠反复追问玩家。

同时管理端只能按状态 / 类型 / 游戏版本筛选，无法回答"这个玩家一共报过什么"；而且翻页会丢掉当前筛选（既有 bug），列表也没有任何行内操作，分诊必须在详情页之间来回跳。

原始需求里的"增加用户表"**已经存在**：`players` 表自 `20260907112342_InitialCreate` 起就有 `SteamId`（唯一）/`SteamName`/`AvatarUrl`/`CreatedAt`/`UpdatedAt`/`LastLoginAt`，并在 `POST /api/auth/steam` 里 upsert。本 spec 不改账号模型，只把"Player 行在首次**登录**时创建"（而非首次提交反馈）这一事实写进文档。

## Solution

1. **游玩时长**：服务端在创建反馈时调用 Steam Web API `IPlayerService/GetSingleGamePlaytime/v1`，把该玩家在本 AppId 上的累计游玩时长（分钟）**快照**到这条反馈上。尽力而为：取不到就是 `null`，绝不因此拒绝提交。
2. **硬件信息**：`Feedback` 增加 `Cpu`（字符串）与 `MemoryTotalMb`（整数 MB）；Godot 插件的适配层自动采集 CPU / GPU / 内存并填充，宿主显式传的值优先。
3. **管理端**：一套共享样式表 + 视觉整理；新增"按玩家筛选"（SteamID64 前缀或昵称包含）；详情页可一键跳到该玩家的全部反馈；列表行内改状态、一键复制 SteamID64；顺手修掉翻页丢筛选的 bug。

## User Stories

1. 作为管理员，我想看到报障玩家的累计游玩时长，以便区分"新手第一小时的崩溃"和"老玩家的边缘 case"。
2. 作为管理员，我想在既有的 OS/GPU 之外看到 CPU 和内存，以便发现硬件相关的崩溃而不必追问玩家。
3. 作为管理员，我想按玩家筛选反馈列表（SteamID64 或昵称），以便集中审阅同一个人的全部反馈。
4. 作为管理员，我想从一条反馈一键跳到该玩家的全部反馈，以便一眼看到他的历史。
5. 作为管理员，我想在翻页时保留筛选条件，以便翻页不会悄悄把查询范围放大。
6. 作为管理员，我想在列表里直接改状态，以便分诊不必逐条打开详情。
7. 作为管理员，我想一键复制 SteamID64，以便去别处查这个玩家。
8. 作为管理员，我想要一个更清晰一致的管理端界面，以便快速扫读列表。
9. 作为玩家，我不想手打自己的硬件配置，以便提交反馈尽量省事。
10. 作为玩家，我希望服务端联系不上 Steam 时我的反馈照样提交成功，以便 Steam 抖动不会让我白写一遍。
11. 作为运维者，我希望游玩时长查询失败在日志里可见但无害，以便私密资料或 Steam 故障能优雅降级。
12. 作为开发者，我希望插件的"引擎无关内核"保持引擎无关，以便离线校验器继续通过。

## Implementation Decisions

### 账号模型（不改）

- `players` 表已存在且字段满足需求，**不加字段、不改迁移**。
- 行在 `POST /api/auth/steam` 里由 `PlayerService.UpsertFromSteamLoginAsync` 创建（`SteamId` 唯一，`varchar(20)`）。
- 昵称 / 头像策略不变：每次登录刷新，Steam 取不到新值时**保留旧值**（`PlayerService.cs:33-41`）。
- 文档需补齐这一事实（原需求误以为"首次提交反馈时创建"；实际是首次登录，且提交反馈必须先登录）。

### 数据模型与迁移

新迁移 `AddFeedbackEnvInfoAndPlaytime`（最新为 `20260907121222_AddIdentitySchema`）：

```text
Feedback.Cpu              varchar(120)  NULL
Feedback.MemoryTotalMb    integer       NULL
Feedback.PlaytimeMinutes  integer       NULL
```

- 全部可空，**不建立索引**（不按这些字段筛选），**不回填**历史行（历史数据无从获取）。
- `PlaytimeMinutes` 语义 = 该玩家在本 AppId 上的**累计**游玩时长，单位**分钟**（Steam 原生单位），UI 显示时换算成小时。
- 写字段的完整链条（一处都不能漏）：`Domain/Feedback.cs` → `Data/Configurations/FeedbackConfiguration.cs`（`HasMaxLength(120)`）→ `Contracts/Requests/CreateFeedbackRequest.cs` → `Contracts/Responses/FeedbackDto.cs` + `FeedbackDetailDto.cs` → `FeedbackService.Validate` 的 `IsOverLong` 链 → `FeedbackService.CreateAsync` 映射 → `ToDto` / `ToDetailDto`。

### 请求 / 响应契约

- `CreateFeedbackRequest` 增加 `string? Cpu` 与 `int? MemoryTotalMb`，并补 `[Description]`（`OpenApiEndpointTests` 断言描述存在）。
- **`playtimeMinutes` 不是请求字段。** 客户端即使传了也被忽略——必须写进 `docs/specs/player-api.md`，否则将来一定会有人加上它，凭空造出一个可伪造字段。
- `FeedbackDto` 与 `FeedbackDetailDto` 都增加 `Cpu`、`MemoryTotalMb`、`PlaytimeMinutes`（玩家在自己反馈里也看得到，与现有"玩家可见全部元数据"一致）。

### 校验通则（重要）

- **玩家既没输入、也无法修正的字段，永不导致 400**：自动采集的 `Cpu`/`MemoryTotalMb`、服务端派生的 `PlaytimeMinutes` 一律走"越界/超长 → 存 `null` + Warning 日志"。
- 理由：400 会让玩家丢掉已经写好的正文，而这些字段他既没填也改不了。
- `MemoryTotalMb` 合理区间 `0 < x ≤ 4,194,304`（4 TiB）；越界 → `null`。
- **现有手填元数据保持既有行为不变**：`gameVersion`/`buildNumber`/`operatingSystem`/`gpu`/`locale`/`map`/`character` 超长仍然 400。

### 游玩时长查询

- **端点**：`IPlayerService/GetSingleGamePlaytime/v1`，参数 `key`（已有 `Steam:ApiKey`）、`steamid`（来自认证主体的 `sub`）、`appid`（已有 `Steam:AppId`），走已有的 `HttpClient("Steam")`（`BaseAddress = https://api.steampowered.com`，`Program.cs:54-58`）。不引入新依赖。
- **端点存在性已实测证实**（未知方法返回 404，本方法返回 403 只差 key），但它**不在 Steam 客户端的 protobuf 定义里**，所以响应字段名无法从协议定义推出。因此必须实现一条兜底：**解析不到期望字段时打 Warning 并记录有界长度的响应正文**（响应体不含认证材料，可安全记录）——把"字段名猜错"从静默的 `null` 变成日志里一条明确的告警。
- **解析路径已实测定稿**：`response.playtime_forever`，整数，**分钟**；响应是**扁平对象**——没有 `games[]` 包装、也不回显 `appid`；**`0` 是合法值**（拥有但从未玩过）存 `0`，只有字段缺失或类型不符才存 `null`。
- **`GetOwnedGames` 备选已被实测否决**：同一账号同一时刻它返回 `game_count: 0`（未发行 / Playtest 应用的游玩时长不计入零售"拥有游戏"列表），会静默给出 `null`。**不要**换回这条路——`GetSingleGamePlaytime` 是唯一可用的端点。
- **模块归属**：新建单职责 `SteamPlaytimeService`（依赖 `IHttpClientFactory` + `IOptions<SteamOptions>` + `ILogger`）。不塞进 `SteamAuthService`（那是"验票 + 资料"），也不塞进 `FeedbackService`（它现在只依赖 `AppDbContext`，注入 HTTP 会毁掉它作为"纯持久化 + 所有权查询"的可测性）。
- **编排在端点**（薄 handler，业务在 service）：`校验 → ResolvePlayerIdAsync → 查游玩时长 → CreateAsync`。PlayerId 解析失败直接 401，不浪费一次 Steam 调用。
- **签名**：`FeedbackService.CreateAsync(int playerId, CreateFeedbackRequest request, int? playtimeMinutes, CancellationToken)`。
- **超时 3 秒**：`CancellationTokenSource.CreateLinkedTokenSource` 包一层；超时 / 非 2xx / JSON 异常 / 缺字段 → `null` + Warning 日志，提交照常 201。
- **失败一律 `null`，绝不抛给调用方**（与 `SteamAuthService.GetPlayerSummaryAsync` 的"尽力而为"风格一致）。
- 已知会拿不到值的场景（都退化为 `null`，UI 显示 `—`）：玩家资料/游戏详情私密、未拥有该游戏、家庭共享、Steam 故障或限流、**调试登录的假 SteamID**。
- 限流无需新增：这次调用发生在 `POST /api/feedback` 内部，已受 `player-write`（5 次/10 分钟/玩家）保护，且必须持 JWT 才能触发。

### Godot 插件（1.0.0 → 1.1.0）

- `PlayerFeedbackDraft` 增加 `Cpu` / `MemoryTotalMb`；`PlayerFeedbackValidation` 增加对应上限（CPU ≤120，内存整数区间同上）。
- **自动采集发生在 `FeedbackClient.cs` 适配层**（唯一接触 Godot 的文件），核心三件套 `FeedbackContracts.cs` / `FeedbackAbstractions.cs` / `FeedbackRuntime.cs` 仍然零 `using Godot`——`tests/verify.py` 阶段 2 的正则断言必须继续通过。
- 采集来源（已核实 Godot 4.x 本地文档）：
  - CPU 型号：`OS.GetProcessorName()`（**Windows/macOS/Linux/iOS 有效，Android/Web 返回空串** → 空则 `null`）
  - 内存：`OS.GetMemoryInfo()["physical"]`，**字节**，除 `1024²` 取整为 MB
  - GPU：**`RenderingServer.GetVideoAdapterName()`**（headless/服务端构建返回空串 → `null`）
  - OS：`OS.GetName() + " " + OS.GetVersionAlias()`，Linux 上再拼 `OS.GetDistributionName()`
- **不要用** `OS.get_video_adapter_driver_info()`：Godot 文档明确警告它"首次调用可能耗时数秒"，会给提交引入秒级卡顿。
- **优先级：宿主显式传的值优先，适配层只补空缺。** 配套改动：`addons/gd_feedback/README.md` 的示例与 `tests/godot-feedback-host/src/FeedbackLab.cs` **删掉手写的 `operating_system`/`gpu`**，让它们成为"自动填充的默认值"，文档不再教旧写法。
- **不加开关**：没有 `CollectEnvironmentInfo` 之类的配置项（AGENTS.md 反对投机功能）。代价已知且被接受：宿主无法关闭自动采集。
- 字段缺失一律 `null`，**不阻断提交**：采集失败、平台不支持、headless，都只是少了几个字段。
- 新响应键：`cpu`、`memory_total_mb`、`playtime_minutes`；GDScript 便捷重载的 snake_case 键表只读 7 个键（`FeedbackClient.cs:131-137`），**输入与输出两侧都要补**。
- **版本号三处必须同步**：`plugin.cfg`、`addon.manifest.json`、`README.md` 标题——`tools/package.py` 阶段 1 会校验三者一致，不一致直接失败。
- `Harness.cs:269-295` 断言了提交体的字段集合（camelCase `gameVersion`/`map`），新增字段必须同步更新该断言并补新用例。

### 管理端 UI

- **共享样式**：新建 `wwwroot/admin.css`，在 `App.razor` 里 `<link>`；删除 `AdminLayout.razor`、`FeedbackList.razor`、`FeedbackDetail.razor`、`Pages/Admin/Login.cshtml` 里 4 处内联 `<style>`，样式集中一处。不用 CSS 隔离（共享的表格/按钮/徽章会在每页重复，且隔离样式跨组件不生效）。
- **不引入 CSS 框架**（AGENTS.md：没有确证需求不引入新依赖；本项目管理端只有 3 个页面）。顺手清理 `wwwroot/app.css:32,37` 两处对从未定义的 `--bs-secondary-color` 的模板死引用（保留 `app.css` 本身，`Error.razor` 用了 `text-danger`）。
- **视觉规格**：状态徽章 待处理=琥珀 / 处理中=蓝 / 已解决=绿 / 已关闭=灰；类型徽章 Bug=红系 / Suggestion=蓝系 / Other=灰；表格斑马纹 + 悬浮高亮；筛选条吸顶；窄屏表格横向滚动（不加 JS）；统一的加载态与空态；保留深色顶栏；中文文案不变（自用运维工具，不做本地化）。
- **详情页**展示新增的 CPU / 内存 / 游玩时长；**列表新增"游玩时长"一列**（CPU 型号串太长会把列宽挤爆，不上列表）。
- **按玩家筛选**：查询参数 `player`。
  - 输入**纯数字** → `SteamId.StartsWith(term)`（允许只记得前几位）
  - 否则 → `EF.Functions.ILike(SteamName, "%" + term + "%")`（`%`/`_` 需转义）
  - 空串 = 不过滤；与现有筛选 **AND** 组合
  - 昵称匹配用不上唯一索引，但 `players` 表量级很小，可接受；将来变大再考虑索引
- **详情页**"查看该玩家全部反馈" → `/admin/feedback?player={SteamId}`，**清空其它筛选**（链接文案承诺的是"全部"）。
- **修掉翻页丢筛选的 bug**：根因是 `GoToPage`（`FeedbackList.razor:162-170`）只写了 `page`/`pageSize`。收敛成一个私有 `BuildUrl(int page)`，`ApplyFilters` / `GoToPage` / 跳转全走它，让筛选集不可能再分歧；并补上此前完全缺失的 UI 层筛选/翻页测试。
- **行内改状态**：改完**重跑当前查询**（状态筛选生效时那一行本来就该消失/出现，只改本地会留下一条"不该在"的记录）；保留当前页与全部筛选；不二次确认（状态随时能再改）；失败显示错误且不动列表。
- **一键复制 SteamID64**：`IJSRuntime` + `navigator.clipboard.writeText`，配几行 `wwwroot/admin.js`；非安全上下文（内网 http）下 clipboard 不可用，**必须有回退分支**（显示可选中文本 + 提示手动复制）。

### 文档与 ADR

- 新 ADR `docs/adr/0006-server-side-playtime-and-untrusted-hardware-info.md`：记录"游玩时长由服务端向 Steam 取（客户端 SDK 无法提供）、尽力而为、每次提交快照；硬件信息是客户端上报的咨询性数据、有界、绝不用于授权"。
- `CONTEXT.md` 新增术语：**Playtime / 游玩时长**（服务端从 Steam 取的权威值，快照在反馈上）与 **Hardware Info / 硬件信息**（CPU/GPU/内存，客户端上报、咨询性、不可信）。并再次点明"User"不是本仓库的词，规范词是 **Player**。
- `docs/domain-model.md`：Feedback 字段表补 `Cpu`/`MemoryTotalMb`/`PlaytimeMinutes`；补一句"Player 行在**首次登录**时创建"。
- `docs/specs/player-api.md`：请求体加 `cpu`/`memoryTotalMb`；写明"**游玩时长由服务端填写，客户端即使传了也会被忽略**"；说明这些字段越界不会 400 而是被丢弃。
- `docs/specs/admin-ui.md`：玩家筛选、列表新增游玩时长列、筛选项在翻页时的保留语义。

## Testing Decisions

- 好的测试只断言外部行为：HTTP 请求/响应、状态码、响应体、应用服务返回值；内部重构不得让它失败。
- **主接缝仍是 HTTP 边界**：`WebApplicationFactory` + 真实路由/JWT/Identity/限流 + 真实 PostgreSQL（Testcontainers）；只 stub Steam 的 HTTP 传输。
- **`FakeSteamHandler` 必须扩展**：它对**未 stub 的 Steam 调用直接抛异常**（`Infrastructure/FakeSteamHandler.cs`），所以 `GetSingleGamePlaytime` 的桩是前置条件，否则所有"创建反馈"用例都会炸。新增 `Playtime(steamId, minutes)`、`PlaytimeMissing`（无 `playtime_forever` 字段）、`PlaytimeMalformed` 等变体，并可断言 `RequestedPaths` 确实包含 `GetSingleGamePlaytime`。**桩体必须使用 issue 01 实测的形状**：扁平对象、无 `appid`、无 `games[]`；并覆盖 `playtime_forever: 0` → 存 `0`（不是 `null`）。
- 需要覆盖的游玩时长分支：取到值 → 落库且出现在响应里；Steam 500 → `null` 但**仍然 201**；响应缺字段 → `null`；超时 → `null`；`appid` / `steamid` 参数取值正确（来自 `Steam:AppId` 与认证主体的 `sub`，**不来自请求体**）。
- 需要覆盖的字段分支：`cpu`/`memoryTotalMb` 正常落库并回传；`cpu` 超长 → 存 `null` 且**不 400**；`memoryTotalMb` 越界 → 存 `null` 且**不 400**；请求体里的 `playtimeMinutes` 被忽略（防伪造回归）。
- 持久化：`DomainPersistenceTests` 补新列往返；迁移本身由自动迁移路径覆盖。
- **管理端**：补上此前缺失的 UI 层筛选/翻页测试（现有测试只到 `AdminFeedbackService`）。至少覆盖：`player` 数字前缀匹配、昵称包含匹配、`player` 与状态/类型/版本 AND 组合、**翻页保留全部筛选**（今天的 `GoToPage` 会失败，这正是回归点）、非法 `player` 值不报错只降级为不过滤。
- **插件**：`addons/gd_feedback/tests/verify.py` 五个阶段是硬门槛，尤其阶段 2（三个核心文件不得出现 `Godot`）与阶段 3（纯 .NET 干净宿主，`TreatWarningsAsErrors=true`，0 warning）；`Harness.cs` 需补新字段的提交体断言与序列化用例；`tests/godot-feedback-host` 的 `--lab-selfcheck` 与 `sync_addon.py` 漂移检查必须继续通过。
- 全部测试不得需要真实 Steam 凭据；`.env` 里的真实 key 只用于一次性人工探测，绝不进测试。

## Out of Scope

- 玩家编辑/删除自己的反馈；管理员编辑/删除/任何内容审核
- **账号模型不变**：不给 `players` 加字段，不把 Player 行推迟到"首次提交反馈"再创建，不做 `/admin/players` 玩家列表页
- 不在 `Player` 上冗余"当前游玩时长"（只在反馈上快照）
- 不做"刷新游玩时长"之类的回填/重查动作；不做历史数据回填
- 不采集**本次会话**时长（与"Steam 累计游玩时长"是不同指标）
- 不采集内存剩余量、CPU 逻辑核数、显存
- 不提供自动采集的开关配置项
- 不做玩家筛选的**下拉框**（玩家一多就不可用）
- 不引入 CSS 框架、不引入任何新依赖
- `docs/overview.md` 的既有 out-of-scope 清单（公开反馈板、投票、路线图、邮件/OAuth 登录、复杂角色、多租户、微服务……）继续有效

## Further Notes

### 这一轮已经核实的事实（省得下次重新踩）

- 「用户表」已经存在：`players`（`SteamId` 唯一 `varchar(20)`、`SteamName` ≤120、`AvatarUrl` ≤512、`CreatedAt`/`UpdatedAt`/`LastLoginAt`），由 `20260907112342_InitialCreate` 建立，在 `AuthEndpoints.cs:85-86` upsert。管理端列表与详情**已经**在显示昵称 + 头像。
- `FeedbackComment.PlayerId` 是普通可空列，**没有 FK、没有索引**（与本 spec 无关，但容易误判成外键）。
- `AdminFeedbackService.ListAsync` 是唯一的筛选/排序/分页入口；排序固定 `CreatedAt DESC, Id DESC`；`gameVersion` 是**精确匹配**。
- 服务端已注册 `HttpClient("Steam")`（`BaseAddress = https://api.steampowered.com`，`Timeout = 10s`），持有 `Steam:ApiKey` / `Steam:AppId`。
- **`IPlayerService/GetSingleGamePlaytime/v1` 的响应形状已实测确认**：`response.playtime_forever`（整数，单位分钟），扁平对象、无 `games[]`、**不回显 `appid`**、**`0` 合法**。端点存在性同样是实测的：未知方法 `IPlayerService/GetBogusNonexistentMethod/v1/` → **404**，本方法 → **403**（正文要求校验 `key=`）。它**不在** Valve 客户端 protobuf 的 `service Player` RPC 列表里（那里只有 `GetOwnedGames` / `ClientGetLastPlayedTimes` / `GetFriendsGameplayInfo` / `GetRecentPlaytimeSessionsForChild` / `GetDurationControl`），属 **Web 层独有方法**，所以形状只能实测、不能从协议推出。原始观测样本见 issue 01。
- **`GetOwnedGames` + `appids_filter` 是被实测否决的死路**：同一账号同一时刻它返回 `game_count: 0`，而 `GetSingleGamePlaytime` 给出数千分钟的非零值。未发行 / Playtest 类应用的时长不计入零售"拥有游戏"列表。若改用它，`null` 会静默发生且与"资料私密"无法区分。
- **免 key 访问此端点不可能**：`GetPlayerSummaries` → 400（缺 `key`）、`GetSingleGamePlaytime` → 403、`GetOwnedGames` → 401。所以带 key 的 URL 绝不能出现在工具调用或日志里。
- **代理所在沙箱禁止出站 TLS**（连 `api.github.com` 都是 TLS 握手失败，`-SkipCertificateCheck` 亦然），实测探针必须由人在本机执行；`partner.steamgames.com` 在本环境也取不到。
- Facepunch.Steamworks **2.5.2**（经 `godottools/0.0.7` 传递引入）**没有任何总游玩时长 API**（只有 `SteamUGC` 创意工坊物品时长与 `SteamUser.GetDurationControl` 家长控制数字）。这就是"游玩时长只能由服务端取"的根据，也是在 ADR-0006 里要写明的理由。
- `OS.get_video_adapter_name()` **在 Godot 4.x 不存在**；GPU 型号只能用 `RenderingServer.get_video_adapter_name()`。
- Go 客户端 `steamapi` 的 README 提示其 `partner.steam-api.com` 主机需要 **publisher key**（本项目用的正是 Publisher Web API Key），且该主机强制 HTTPS。

### 自动化踩坑清单（实现时逐条核对）

1. `FakeSteamHandler` 对未 stub 的调用抛异常 → 先加桩，否则大面积红。
2. `Harness.cs` 里有提交体字段集合断言 → 加字段必须同步改。
3. 版本号三处同步，`package.py` 会校验。
4. `verify.py` 阶段 2/3 是引擎无关性硬门槛；采集只能写在 `FeedbackClient.cs`。
5. 阶段 4 用 `TreatWarningsAsErrors=true` → `OS.GetMemoryInfo()` 返回 `Godot.Collections.Dictionary`/`Variant`，取值要显式转换（`AsUInt64()` 之类），别留隐式转换警告。
6. `FeedbackList.razor` 的 `GoToPage` 是既有 bug，收敛 `BuildUrl` 时一并修掉。
7. `docs/specs/player-api.md` 必须写明"客户端传 `playtimeMinutes` 会被忽略"。
8. 管理端筛选/翻页**今天没有 UI 层测试**，修 bug 必须同时补测试。
9. `GetSingleGamePlaytime` 的响应形状**已实测**（`response.playtime_forever`，扁平、无 `appid`、`0` 合法）；`GetOwnedGames` 备选**已被实测否决**（`game_count: 0`）。issue 03 里"取不到期望字段就告警并记录响应正文"仍要保留——私密资料会走那条路，没有告警就无法与解析写错区分。
