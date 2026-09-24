# Spec: 一个实例服务多个 Game

Status: resolved

来源：一次完整的设计定稿（形状、术语、寻址、令牌、数据模型、配置归属、管理端、迁移与测试全部已决定；本轮负责记录与拆分实施）。相关 ADR：`docs/adr/0007-multi-game-support.md`（**含"寻址方式反转"的修订章节**）、`docs/adr/0008-steam-configuration-in-the-database.md`。

> **决策变更（同一天，首次部署尝试之后）**：本 spec 最初定的是「用自造的 Game slug 作为路径段」。实机跑过一轮后**寻址方式被反转**：玩家 API 现在按 **Steam AppID** 寻址（`/g/{appId}/api/...`），`games.Slug` 整列连同唯一索引一起删除，客户端也不再被配置标识符、而是由宿主注入运行时 AppID 自己拼出路径段。下文凡涉及寻址与 slug 的段落均已按实现改写；完整理由、被否决的方案与后果见 `docs/adr/0007-multi-game-support.md` 的 `## Revision (same day, after the first deploy attempt)`。原决策之所以站得住，是因为它假设「标识符由人手抄进配置」——客户端改为运行时派生之后这个前提消失了；而 slug 方案自己的失效模式（运维者建的 slug 与客户端请求的不一致，`404 game_not_found` 与"没有这个 Game"无法区分，排查了两轮）是真的踩到了。

> **实现状态（2026-09-24）**：本 spec 已**全部实现并验证**。9 个 issue 均为 `resolved`，各自的完成记录与偏离说明见 `.scratch/multi-game/issues/*.md` 的 `## Comments`。证据：`dotnet build` 0 警告 0 错误；`dotnet test` 216 通过 / 0 失败；`python addons/gd_feedback/tests/verify.py` 124 项 0 失败；迁移在真实 PostgreSQL 上验过全新装 / 生产升级 / 回滚三条路径；端到端在真实 PG + Release 构建上验过按 AppID 寻址与全部错误码。

## Problem Statement

改造前的实现**只能服务一个 Steam 游戏**：`Steam:AppId` 是一个部署级标量，它同时决定了接受哪款游戏的票据、查谁家的游玩时长、以及 `players` / `feedbacks` 里的行属于谁。要服务第二款游戏，唯一的办法是**再部署一套实例**：又一个数据库要迁移/备份/恢复、又一个管理员账号、又一次升级窗口。而现实是同一个运维者发行多款游戏，共用一把 Steam publisher key，管理后台也只有一个人。

同时，"Steam 配置放在环境变量里"这件事在多游戏下彻底站不住：AppId 必须是**每个 Game 的运行时事实**，管理员应当能在不重新构建镜像、不重启容器的前提下新增或修正一个 Game；而 API key 是所有游戏共用的一把，也不能退化成"每个 Game 各存一份"。

另外，客户端侧有一个既有事实决定了整个方案的形状：**Game 无法从 Steam 票据里反推出来**。`ISteamUserAuth/AuthenticateUserTicket/v1` 把 `appid` 当**输入**且不回显；`IPlayerService/GetSingleGamePlaytime/v1` 同样不回显 `appid`（实测见 `.scratch/env-info-playtime-admin-ui/issues/01-probe-get-single-game-playtime.md`）。所以"这个请求属于哪个 Game"必须在**验证之前**就已经知道。

## Solution

一个实例、一个数据库，服务 N 个 **Game**。

1. **寻址**：玩家 API 只挂在 `/g/{appId}/api/...`，其中 `{appId}` 是该游戏的 **Steam AppID**（纯数字，最多 10 位，例如 `POST /g/1910980/api/auth/steam`）。根路径（`/api/auth/steam`、`/api/feedback`、`/api/feedback/mine`、`/api/feedback/{id}`、`/api/feedback/{id}/comments`）**永久移除**，一律 `404` + `ProblemDetails` 扩展成员 `code = "game_required"`；不存在"默认 Game"。`/health` 与 `/admin` 留在根。
2. **作用域**：`Player` 改成 **每个 Game 一个**（同一 Steam 账号在两款游戏里是两个 Player）；`players` 加必填 `GameId`，`SteamId` 的全局唯一索引换成 `(GameId, SteamId)` 复合唯一索引；`feedbacks` 加必填 `GameId` 外键，配置为 `Restrict`（**不是** cascade）。
3. **令牌绑定**：玩家访问令牌增加 `game` claim，携带 Game 的**数字主键**；为 A 游戏签发的令牌在 B 游戏路径上被拒（`401 game_mismatch`）。用数字主键而不是路径段（AppID），是为了改寻址标识也不会让在线令牌失效或改指。升级前签发、没有该 claim 的令牌同样被拒——客户端会自愈（非登录端点拿到 `401` → 清缓存 → 重新用新票据登录 → 只重试一次），玩家无感。
4. **Steam 配置进入数据库**：新增 `games`（每个 Game 的 `SteamAppId`、`Identity`、`IsActive`、`CredentialId`）与 `steam_credentials`（`EncryptedApiKey`，一份凭据可服务多个 Game）。环境变量不再承载任何 Steam 配置，只剩一个 Development 专用的 `Steam:DebugSkipTicketValidation`。`SteamAppId` 同时是路径段，因此它从"可选"变成管理端**必填**。
5. **API key 静态加密**：ASP.NET Core Data Protection（`IDataProtector`，purpose `GameFeedback.SteamApiKey.v1`），密钥环落到挂载卷并纳入备份；Data Protection 应用名显式钉死为 `GameFeedback`。API key 在管理端是**只写**的：不回显进任何 HTML、不进任何 DTO、不进任何日志。
6. **管理端**：新增 `/admin/games`（CRUD + 停用）与 `/admin/credentials`（CRUD + 只读校验探针）；反馈列表增加 game 列与 game 筛选（沿用唯一 URL 出口）；`games` 表为空时，已登录管理员被强制去"新增游戏"页。

## User Stories

1. 作为运维者，我想用一个实例、一个数据库同时服务多款游戏，以便不必维护 N 套部署、N 次升级和 N 个管理员账号。
2. 作为运维者，我想让每款游戏的客户端只改自己的 base URL，以便服务端升级不需要每款游戏都改代码。
3. 作为管理员，我想在管理端新增/修改/停用一个 Game（名字、Steam AppID、identity、凭据），以便不用改配置文件、不用重新部署。
4. 作为管理员，我想一眼看出哪个 Game 还没配好（缺 AppID 或缺凭据），以便知道为什么玩家登不上。
5. 作为管理员，我想在管理端维护一把共享的 Steam API key，并在改动时不被任何页面或日志回显，以便既能轮换密钥又不会泄漏它。
6. 作为管理员，我想用一个只读探针确认某份凭据可用，以便在玩家报障之前发现 key 失效。
7. 作为管理员，我想在反馈列表看到每条反馈属于哪个 Game 并按 Game 筛选，以便分游戏分诊。
8. 作为管理员，我想在删除一个有反馈/有玩家的 Game 时被拒绝（提示改为停用），以便不会误删玩家写下的反馈。
9. 作为管理员，我想在改动已有 Game 的 Steam AppID 时被明确提示"只有运行在这个新 AppID 下的客户端才能再访问它"，以便知道这是一次影响可达性的改动。
10. 作为玩家，我不想因为服务端升级而重新登录或做任何事，以便这次改动对我完全无感。
11. 作为玩家，我希望同一台 Steam 账号在两款游戏里的反馈互不串台，以便我的隐私边界和 bug 上下文都清晰。
12. 作为开发者，我想让"请求属于哪个 Game"由服务端从 URL 解析而不是由客户端声明，以便客户端无法冒充另一个 Game。
13. 作为开发者，我想让跨 Game 的读取在查询层面就不可能发生，以便所有权检查不是唯一防线。
14. 作为运维者，我想让"游戏没配好"、"Steam 挂了"与"密钥环丢了"在服务端可区分，但对玩家都是 fail closed，以便排查方向明确。
15. 作为运维者，我想让密钥环进备份清单，以便灾难恢复后凭据不会永久解不开。

## Implementation Decisions

### 形状与术语（不做的事）

- **一个实例、一个数据库、N 个 Game**。**不做**每租户物理隔离（每 Game 一个库 / 一个 schema / 一个容器），**不做**每 Game 一套部署。所有 Game 共享一套 schema、一套管理员身份体系、一个进程。
- 术语以 `CONTEXT.md` 为准：**Game**（本实例服务的一个 Steam 应用，由 Steam AppID 标识）、**Player**（**在一个 Game 内**通过 Steam 认证的人）。"一个 Steam 账号对应恰好一个 Player"这条规则**作废**，换成"每个 Game 一个 Player"。
- Feedback、Playtime、Ownership、管理端复核都严格属于一个 Game。

### 寻址与错误码

- 玩家 API 只在 `/g/{appId}/api/...` 下，`{appId}` 是 Steam AppID（纯数字、最多 10 位、不能全为 0），例如 `POST /g/1910980/api/auth/steam`。
- 根路径永久移除，返回 `404` + `code = "game_required"`。这不是弃用窗口。实现上由 `Program.cs` 显式映射 `/api` 与 `/api/{**rest}` 兜底端点（`MapMethods`），而不是靠默认 404——否则状态码页会把响应体换成防伪页。
- `/health`、`/admin` 留在根。
- 路径段不是纯数字时**先于查库**就被 `GameResolver` 拒掉（`GameValidation.NormalizeAppId` 之后再 `char.IsAsciiDigit` 逐字符判断），因此"非数字路径段"与"没有这个 AppID"都落到同一个 `404 game_not_found`。
- 错误码（`ProblemDetails` 的稳定扩展成员 `code`，集中在 `Api/ApiProblems.cs` 的 `ApiCodes`）：

```text
game_required          404  用了根玩家路径
game_not_found         404  没有 Game 的 SteamAppId 等于这个值（非数字路径段同样落这里）
game_disabled          403  Game 存在但已停用
game_mismatch          401  令牌不是为这个 Game 签发的（含缺 claim 的老令牌）
credential_unreadable  401  凭据存在但解不开（Data Protection key ring 问题）
steam_unavailable      401  Game 的凭据未配置；或 Steam 联系不上（沿用既有语义）
steam_ticket_rejected  401  Steam 拒绝了这个 Game 的票据
```

- `credential_unreadable` 与 `steam_unavailable` **必须分开**：密钥环丢了不能长得像 Steam 故障。对客户端两者都是"可重试"，只有服务端诊断不同。
- **解析过滤器只负责 404 / 403**（`ResolveGameFilter`）：凭据缺失不在那里拦，因为那要由登录端点以 `401 steam_unavailable` 报出，且 Development 的调试登录本来就允许在无凭据的 Game 上工作。**AppID 缺失根本进不到这里**：没有 AppID 的 Game 没有任何路径能解析到它，所以 `steam_unavailable` 里"缺 AppID"这一半是不可达的（`game_not_found` 的 detail 也不再列举已配置的 Game，只说明请求的 AppID 并指向管理端）。
- `players`/`feedbacks` 的 `GameId` 一律来自服务端解析出的路径段，**永远不读请求体**；请求体里即使传了 `gameId` / `appId` 也被忽略。

### 令牌

- `game` claim 携带 Game 的**数字主键**（不是路径段的 AppID）：改 Steam AppID 不会让在线令牌失效或指向别处。
- 为 A 游戏签发的令牌在 B 游戏路径上拒绝（`401 game_mismatch`）；claim 缺失或不是整数同样按不匹配处理（`ApiProblems.GameContextExtensions.GetTokenGameId`）。
- 现有 `sub = SteamID64`、24 小时有效期、HS256、无 refresh token 维持不变（ADR-0003 的这几条仍然有效）。

### 数据模型与迁移

本仓库现有**五个** EF 迁移。多游戏这一轮落了两个（前面四个是 `20260907112342_InitialCreate`、`20260907121222_AddIdentitySchema`、`20260924072540_AddFeedbackEnvInfoAndPlaytime`，以及本轮的 `20260924114358_MultiGameSupport`）：

```text
20260924114358_MultiGameSupport        建表 + 加 GameId + 条件插入占位 Game
20260924124609_AddressGamesBySteamAppId 删 games.Slug：AppID 成为唯一寻址键
```

第二个迁移是**刻意拆开的**，没有折进 `MultiGameSupport`：迁移 4 可能已经在某个库里跑过了，而"已应用过的迁移不得改写"是硬约束——否则那个库就再也走不到同一套 schema。`AddressGamesBySteamAppId` 只做删列删索引；它的 `Down` 会把 `Slug` 加回来并用 `'game-' || "Id"` 合成唯一占位值（足以把唯一索引建起来），但那些值不是历史：回滚后必须人工把每个 Game 的 slug 改回客户端真正在用的那个。

```text
games
  Id              serial PK
  Name            varchar(100)  NOT NULL
  SteamAppId      varchar(10)   NULL      UNIQUE     -- 寻址键（路径段 /g/{appId}）；见下
  steam_identity  varchar(64)   NOT NULL  DEFAULT 'feedback-api'
  IsActive        boolean       NOT NULL  DEFAULT true
  CredentialId    integer       NULL      FK -> steam_credentials(Id) Restrict
  CreatedAt / UpdatedAt

steam_credentials
  Id               serial PK
  Name             varchar(100) NOT NULL
  EncryptedApiKey  text         NOT NULL          -- Data Protection 密文
  CreatedAt / UpdatedAt

players
  + GameId  integer NOT NULL FK -> games(Id) Restrict
  - IX_players_SteamId                 （删除全局唯一索引）
  + IX_players_GameId_SteamId UNIQUE

feedbacks
  + GameId  integer NOT NULL FK -> games(Id) Restrict   -- 故意不是 cascade
  - IX_feedbacks_GameVersion
  + IX_feedbacks_GameId_CreatedAt
  + IX_feedbacks_GameId_Status
  + IX_feedbacks_GameId_GameVersion
```

- `games.Slug`（原 `varchar(32) NOT NULL UNIQUE`，唯一索引 `IX_games_Slug`）**已删除**，不再保留第二个人工维护的标识符。
- `SteamAppId` 存**数字字符串**（`varchar(10)`，校验为纯数字且不为 0），不是整数列；`Identity` 的列名是 `steam_identity`。列仍可空，但**只**为迁移插的占位行留口子——那一行不知道自己的 AppID，而它因此**不可寻址**：任何路径都解析不到它，客户端只会拿到 `404 game_not_found`。管理端在新建与编辑时都要求 AppID（`GameValidation.ValidateSteamAppId` 对空值报"Steam AppID 不能为空：它同时是玩家 API 的路径段 /g/{appId}/api/..."）。
- `Restrict` 是刻意的：删除 Game **绝不能**静默删掉玩家反馈；`games.CredentialId` 同样 `Restrict`，删除被引用的凭据也绝不能让在用的 Game 悄悄失去 key。`players.GameId` 也是 `Restrict`——与"有玩家就拒绝删除 Game"这一域结果保持一致（数据库层是保证，服务层返回的 HasData/InUse 只是提示）。
- **迁移必须手写关键顺序**（`MultiGameSupport` 已按此实现）：EF 为新的必需列生成 `defaultValue: 0`，而 `0` 不对应任何 Game，紧接着的外键必然失败。正确顺序是「建表 → 插占位 Game → 加**可空**列 → 回填 → 收紧非空 → 建外键」。
- **历史数据归属**：升级路径上由迁移插入一个占位 Game（`Name = '默认游戏'`、`SteamAppId = NULL`、凭据 `NULL`），把现有全部 `players` / `feedbacks` 挂到它下面。理由：必填外键不能有空值；不能让既有安装在升级后被"空 games 表"守卫挡在自己的历史数据之外。它没有 slug（列已随 `AddressGamesBySteamAppId` 删除；建它的那个迁移当初写过 `Slug = 'default'`，这个值如今在任何地方都不存在了）。管理员随后在 `/admin/games` 填 AppID、选凭据即可——**在填 AppID 之前它不可寻址**：没有任何路径能解析到它，所以不会误服务，也不会产生 `401 steam_unavailable`（`steam_unavailable` 现在只剩"有 AppID 但没凭据"这一半可达）。**注意这不算"从环境变量导入凭据"**（ADR-0008 否决的是后者）。
- **占位 Game 只在"有数据要挂"时插入（已实现）**：迁移的插入语句带 `WHERE EXISTS (SELECT 1 FROM players) OR EXISTS (SELECT 1 FROM feedbacks)`，所以**全新数据库跑完迁移后 `games` 表是空的**，首次运行守卫必然触发（`AGENTS.md` 的"a fresh database has no Games at all"因此成立）。没有这个条件的话，全新安装会凭空多出一个未配置的 `默认游戏`，而"空 games 表 → 强制去新增游戏页"的首次运行态永远不会出现。
- 不回填 `players`/`feedbacks` 的其它字段；不删除任何现有数据。

### Steam 配置进入数据库

- `games.SteamAppId` / `games.steam_identity` 是每个 Game 的配置；`steam_credentials.EncryptedApiKey` 是加密后的共享 publisher key，一份凭据可以被多个 Game 引用（`games.CredentialId`）。
- 环境变量不再承载任何 Steam 配置：`Steam:ApiKey`、`Steam:AppId`、`Steam:Identity` 从 `appsettings*.json`、`docker-compose.yml`、`.env.example`、`scripts/run_local.py`、README 中**全部删除**；唯一留下的 Steam 相关变量是 Development 专用的 `Steam:DebugSkipTicketValidation`（"跳过全部验票"的开关不能暴露在只靠管理员密码保护的 UI 里）。
- 路径段现在由客户端按运行时 AppID 自己拼（见下），所以管理端**必填** AppID：`GameValidation.ValidateSteamAppId` 对空值报"Steam AppID 不能为空：它同时是玩家 API 的路径段 /g/{appId}/api/..."，非数字、超过 10 位、全 0 也都拒绝。
- **启动不再校验 Steam 凭据**：Steam 的 `ValidateOnStart` 与 `IsPlaceholderSteamValue` 占位告警一并移除（配置里已经没有可校验的东西）。代价是：新部署存在一个"games 表为空 / Game 没凭据 → 玩家登录 fail closed"的窗口（没有 AppID 的 Game 连路径都没有，谈不上 fail closed），且**没有**任何 env 种子或首次导入——数据库是唯一事实来源，第一条凭据由管理员在 UI 里录入。
- **加密**：`Services/ApiKeyProtector.cs` 包装 `IDataProtector`（purpose `GameFeedback.SteamApiKey.v1`）；`Program.cs` 显式 `SetApplicationName("GameFeedback")` 并把密钥环 `PersistKeysToFileSystem` 到 `DataProtection:KeysPath`（缺省 `<content root>/keys`）；密钥环落到挂载卷并纳入备份；解密失败记 `Error` 并返回 `credential_unreadable`，**绝不能**降级成 `steam_unavailable`。
- **只写不变量**：API key 不回显进任何 HTML（包括校验失败后重渲染的表单）、不进任何 DTO、不进任何日志；`ResolvedGame` 刻意是 class 而非 record 且 `ToString` 不含 key（record 自动生成的 `ToString` 会把 key 打进任何一次结构化日志）；凭据变更日志只记管理员 user id、凭据 id 与"key 是否被替换"这一事实。

### 管理端

- 管理员仍是**单一全局身份体系、没有角色**，一个管理员看到全部 Game。
- `/admin/games`：名字、Steam AppID（**必填**）、identity、启用开关、凭据选择；CRUD；未配好（缺 AppID 或缺凭据）要显式标注（`GameAdminView.IsConfigured`）。两种"未配好"的呈现不同：缺 AppID 的行只可能是升级占位游戏，列表直接标"未填 AppID（不可寻址）"；缺凭据的行有 AppID、可被寻址，玩家登录才会以 `401 steam_unavailable` fail closed。
- `/admin/credentials`：CRUD + 只读"校验凭据"探针（`SteamCredentialService.VerifyAsync` 调 `ISteamUser/GetPlayerSummaries`：`200` = 可用，`403`/`401` = 无效，其它 = 无法确认），**绝不回显 key**；编辑时留空 key 表示保留原值。
- 改动已有 Game 的 AppID 允许，但保存前要提示"只有运行在这个新 AppID 下的客户端还能访问它"；**不**保留别名或历史表，也**没有**确认勾选框——已经没有 slug 可改名了。
- 删除 Game：有反馈或有玩家 → 拒绝并提示"改为停用"（`GameDeleteOutcome.HasData`；管理端没有 HTTP API，所以这是域结果而不是状态码）。删除凭据：被任何 Game 引用 → 拒绝（`CredentialDeleteOutcome.InUse`）。删掉最后一个 Game 会重新触发首次运行态。
- 反馈列表增加 game 列与 game 筛选；详情页增加 game 行；筛选继续只走**唯一 URL 出口**（`AdminFeedbackQuery.ToQueryParameters`），新增的 `game` 参数也走它。**筛选值是 AppID**（`AdminFeedbackQuery.Game` 是 `string?`，`ListAsync` 里 `f.Game!.SteamAppId == gameAppId`，下拉项 `<option value="@game.SteamAppId">`，只列有 AppID 的 Game）：空/缺省 = 全部游戏，手改 URL 传了不存在的值则按字面相等筛出空结果（不报错），并把该值保留成一个"（不存在）"选项，免得下拉框显示"全部游戏"而实际在筛选。
- **首次运行守卫**：`games` 表为空（`GameAdminService.HasAnyAsync`）时，已登录管理员被强制到"新增游戏"页，其它管理页一律不可用。守卫必须**同时**存在于登录后的跳转与 `AdminLayout` 初始化里——Blazor 交互式路由不经过 ASP.NET Core 中间件。例外：`/admin/login`、`/admin/logout`、新增游戏页本身及其保存动作；静态资源不能被守卫捕获。

### 限流

- 玩家写/评论分区从"按 SteamID"改为**按 (Game, SteamID)**（实现为 `{appId}:{sub}`；AppID 唯一故等价，且限流中间件在路由之后执行，`RouteValues["appId"]` 那时已可用）；认证前的 IP 分区不变。

### 游玩时长

- 语义完全不变（ADR-0006 继续主管）：反馈上的一次快照、服务端在提交时取、取不到是 `null`、绝不阻断提交。唯一变化是查询改用**解析出的 Game 的 AppId 与凭据**，而不是全局标量。

### 审计

- Game 与凭据的变更写成结构化日志：管理员 user id、game id / credential id、动作、before/after 值（**绝不含 key 值或密文**）。**不引入审计表**——刻意避免一个没人维护的子系统。

### 客户端（`addons/gd_feedback/`，版本 1.3.0）

- **插件改过一次**（与最初的"零代码改动"相反，这也是寻址反转的一部分）：`BaseUrl` 现在只是反馈服务地址（`https://feedback.example.com`），`/g/{appId}` 由插件自己补。AppID 来自宿主注入的可选接口 `IGameAppIdProvider`（空对象默认实现 `UnavailableGameAppIdProvider`，同在 `FeedbackAbstractions.cs`）——宿主返回游戏正在运行的 AppID，也就是它已经传给 `SteamClient.Init` 的那个值。插件仍然 100% 不引用 Steam（ADR-0004 未变，其 verify 门槛仍在强制"引擎无关、零依赖内核"），**正因如此**这个值只能注入。
- 宿主没实现该接口、或返回 `null`/空白：插件原样使用 `BaseUrl`，所以"BaseUrl 里自带路径"的老用法继续可用。
- 宿主返回的不是数字 AppID（不是纯数字，或超过 10 位；比如误填了 slug）：插件 **fail closed**，报 `invalid_configuration` 且不发任何请求（`FeedbackRuntime.cs:358-376`）——把"标识符抄错"变成一个本地配置错误，而不是服务端 `404`。
- 登录端点 `404 game_not_found` / `403 game_disabled` 仍映射为不可重试的配置类错误。
- 自愈路径仍在（`FeedbackRuntime.cs:413-429`）：非登录端点 `401` → 清缓存 → 重新登录 → 只重试一次，因此"老令牌缺 claim 被拒"对玩家无感。
- 路径归一化（补尾斜杠）现在是 `FeedbackRuntime.cs:378-380`，不再是旧的 341-343。

## Testing Decisions

- 好的测试只断言外部行为：HTTP 请求/响应、状态码、响应体、应用服务返回值。
- **集成 fixture 不再通过配置设置 `Steam:AppId`**（该设置已不存在），改为**播种 Game 行与凭据行**。
- `FakeSteamHandler` 对未 stub 的 Steam 调用直接抛异常，且必须能按 Game 应答（两 Game 用例要能证明每次登录打到 Steam 的 AppId 是对的）。
- 新增覆盖（至少）：
  - 按 Steam AppID 寻址：路径段解析到 `games.SteamAppId` 相符的那个 Game，令牌里的 `game` claim 是它的数字主键；
  - 为 A 游戏签发的令牌在 B 游戏路径上 `401 game_mismatch`；缺 `game` claim 的老令牌同样被拒；
  - 根路径（五个）返回 `404 game_required`；
  - 没有 Game 的 AppID → `404 game_not_found`；**非数字路径段同样 `404 game_not_found`**；停用 Game → `403 game_disabled`；**有 AppID 但缺凭据 → `401 steam_unavailable`**（"缺 AppID"那一半不可达，不要当作用例）；凭据解不开 → `401 credential_unreadable`；
  - **`SteamAppId = NULL` 的 Game 行（升级占位游戏）不可寻址**：任何路径都解析不到它，客户端拿到 `404 game_not_found`；
  - 跨 Game 的 feedback id 读成 `404`；同一 Steam 账号在两款游戏里是两个相互独立的 Player；
  - API key 不出现在任何管理端响应体、任何渲染出的页面、任何 DTO、任何日志行（含保存失败后重渲染的表单）；
  - 删除有反馈/有玩家的 Game → `GameDeleteOutcome.HasData`；删除被引用的凭据 → `CredentialDeleteOutcome.InUse`；
  - 改动既有 Game 的 AppID 后新 AppID 立即生效、旧 AppID `404 game_not_found`；
  - 管理端反馈筛选的 `game` 查询值就是 AppID（服务层筛选语义 + 查询串构造），翻页保留该筛选；
  - 空 `games` 表时的强制跳转，且登录/登出/新增游戏页/静态资源不被拦；
  - 凭据加密往返（新 scope 读回后可用于 Steam 调用），且明文不出现在数据库里；
  - 游玩时长用**解析出的 Game 的 AppId** 查询；
  - 写/评论限流按 `{appId}:{steamId}` 分区：一个 Game 的额度耗尽不影响同一账号在另一个 Game；
  - 插件离线门槛新增两条：宿主给出的 AppID 能把路径补成 `/g/1910980/api/...`；宿主返回 slug 这类非正整数时 `invalid_configuration` 且零请求。
- 全部测试不得需要真实 Steam 凭据。
- `addons/gd_feedback/tests/verify.py` 五个阶段、`sync_addon.py --check`、host 的 `--lab-selfcheck` 继续是硬门槛。

## Out of Scope

- **每租户物理隔离**（每 Game 一个数据库 / schema / 容器）与**每 Game 一套部署**：明确不做。
- 不做"默认 Game"，不做根路径兼容层或重定向，不设弃用窗口（迁移插入的占位 Game 只是历史数据的归属，不是兼容层——根路径仍然 404 `game_required`，而占位 Game 自己连路径都没有）。
- 不保留 slug，也不做 slug 别名 / 历史表 / 旧 slug 重定向（列本身已删除）。
- 不引入审计表（审计只写结构化日志）。
- 不做角色与权限：管理员仍是单一全局身份体系，没有"只能看某个 Game"的管理员。
- 不在 UI 里任何位置回显 API key（没有"显示密钥"控件，没有掩码回填，没有复制按钮）；不提供密钥导出。
- 不从环境变量导入/播种凭据或 Game（含"首次启动导入一次"）。
- 不做 Data Protection 密钥环的轮换 / 多实例共享（单实例 + 备份即可）。
- 不改 Feedback 的内容模型、状态机、评论模型；不做玩家编辑/删除反馈。
- 除本次寻址反转带来的那次改动（版本 1.3.0：`IGameAppIdProvider` 与 `/g/{appId}` 补全）之外，不再改插件的契约。

## Further Notes

### 现状（本轮文档落笔时工作区里已经存在的实现）

命名已与代码对齐，实现顺序上 phase 2–4 已基本落地（schema/迁移、Game 解析与 `/g/{appId}` 路由、`game` claim、Steam 服务参数化、凭据表与加密、凭据探针），phase 5 的管理端页面与 phase 6 的测试/部署配置尚未完成：

- `Domain/Game.cs`、`Domain/SteamCredential.cs`、`Data/Configurations/{Game,SteamCredential}Configuration.cs`、迁移 `20260924114358_MultiGameSupport`、`20260924124609_AddressGamesBySteamAppId`
- `Services/GameResolver.cs`、`ResolvedGame.cs`、`GameValidation.cs`、`GameAdminService.cs`、`SteamCredentialService.cs`、`ApiKeyProtector.cs`
- `Api/GameResolution.cs`（`ResolveGameFilter` / `GameTokenFilter`）、`Api/ApiProblems.cs`（`ApiCodes` / `ApiProblems` / `GameContextExtensions`）
- `Program.cs`：`MapGroup("/g/{appId}")`、`/api` 兜底端点、Data Protection、`{appId}:{sub}` 限流分区
- `addons/gd_feedback/`：`FeedbackAbstractions.cs` 的 `IGameAppIdProvider` / `UnavailableGameAppIdProvider`，`FeedbackRuntime` 里 `/g/{appId}` 的补全与 `invalid_configuration` 的 fail closed

### 实现时仍要核对的坑

1. ~~**`Program.cs` 里两处按 `/api` 前缀分流的 `UseWhen` 还没改**~~ **已修复**：状态码页重执行与防伪校验两处判断现在都同时排除 `/g` 与 `/api`（`Path.StartsWithSegments("/g") || Path.StartsWithSegments("/api")`）。记录在此是因为"玩家路由不在 `/api` 前缀下"这件事以后还可能被漏掉。
2. **OpenAPI 的 transformer 按路径后缀/前缀匹配**：`RequestBodyExampleFor` 已改成按 `/api/auth/steam` 这类**后缀**匹配（玩家路径现在都带 `/g/{appId}` 前缀），但 `pathKey.StartsWith("/api/feedback")` 这类判断仍可能写死旧路径；文档里 `{appId}` 是路径参数。
3. **`AdminFeedbackQuery.ToQueryParameters` 是唯一的查询串出口**（上一轮修"翻页丢筛选"时收敛出来的），`game`（值是 **AppID**）必须加在这里，不要另开第二处拼串。注意该文件里 `["game"] = Game` 上方可能还留着"按 slug 过滤"的旧注释。
4. **`tests/GameFeedback.Tests/Infrastructure/GameFeedbackApplicationFactory.cs` 里的 `Steam:AppId = 480` 要删掉**，改成播种 Game 与凭据。
5. **`AdminSeeder` 只负责第一个管理员**；首次运行态判断的是 `games` 表是否为空（`GameAdminService.HasAnyAsync`），不要和它混。
6. **`scripts/smoke_player_api.py` 用的是 `--app-id <appId>`**（已不是 `--slug`）：必须是纯数字，否则带明确信息以退出码 `2` fail fast；它把每个 `/api/...` 路径改写成 `/g/{appId}/api/...`，`Location` 断言是 `/g/{appId}/api/feedback/{id}`，并覆盖根路径 `404 game_required`；`.env.example` / `docker-compose.yml` / `scripts/run_local.py` / README 里删除的 Steam 变量必须同步补上 `DataProtection__KeysPath` 与密钥环卷。
7. **迁移落盘顺序**：`dotnet ef database update --no-build` 会用旧程序集，必须重新编译（上一轮踩过 `PendingModelChangesWarning`）。两个迁移（`MultiGameSupport` 在前、`AddressGamesBySteamAppId` 在后）必须按序应用。
8. **`GameResolver` 每请求读库、无进程内缓存**是刻意的：凭据或启用状态一改就要立刻生效。
9. **AppID 与路径段的等价关系是寻址的全部**：`GameResolver` 先拒非数字再查库，所以"非数字路径段"与"没有这个 AppID"共用 `404 game_not_found`；如果以后有人想给非数字段一个更具体的错误码，先想清楚它会不会把"标识符抄错"重新变成需要两轮排查的问题。
