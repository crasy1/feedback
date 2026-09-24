# 07: 管理端 games / credentials 页面与首次运行守卫

**What to build:** `/admin/games` 与 `/admin/credentials` 两个页面：Game 的 CRUD（名字、Steam AppID、identity、启用开关、凭据选择）与凭据的 CRUD + 校验；外加"`games` 表为空时强制去新增游戏页"的守卫。

**Blocked by:** 03、05、06。

**Status:** resolved

> **寻址方式在首次部署尝试之后被反转**：Game 没有 slug 字段了，Steam AppID 是**必填**的寻址键。见 `docs/adr/0007-multi-game-support.md` 的 `## Revision (same day, after the first deploy attempt)`。

- [ ] `Components/Pages/Admin/GameList.razor`（列表）+ 新增/编辑表单：`GameAdminService.ListAsync` / `GetAsync` / `CreateAsync` / `UpdateAsync` / `DeleteAsync`；未配好的行（`GameAdminView.IsConfigured == false`）显式标注，并区分两种情形——缺 AppID 的行只可能是升级占位游戏，标"未填 AppID（不可寻址）"（任何路径都解析不到它）；有 AppID 但缺凭据的行才写"玩家登录会 fail closed 为 `401 steam_unavailable`"
- [ ] 新增/编辑表单里 Steam AppID **必填**且必须是纯数字、最多 10 位、不为 0（`GameValidation.ValidateSteamAppId`），提示语写明"它同时是玩家 API 的路径段 `/g/{appId}/api/...`"；**没有** slug 字段
- [ ] 改动已有 Game 的 AppID 允许，但保存前给出**明确提示**：只有运行在这个新 AppID 下的客户端还能访问它；不保留别名/历史表，也**没有**确认勾选框（已经没有 slug 可改名）
- [ ] 删除 Game：`GameDeleteOutcome.HasData` → 拒绝并提示"改为停用"（管理端在进程内直接渲染该结果）；`NotFound` → 友好提示
- [ ] `Components/Pages/Admin/CredentialList.razor`（列表）+ 新增/编辑表单：`SteamCredentialService` 的 CRUD
- [ ] 凭据编辑：key **只写**——不回填进 `value`、不放 hidden 字段、校验失败重渲染表单时也不回填；留空表示保留原密钥（`UpdateAsync` 的 `apiKey` 为空即不动）；没有"显示密钥"控件
- [ ] 校验按钮调用 `VerifyAsync`，显示 可用 / 无效 / 无法确认 / 凭据不存在 四态，不回显任何 key 片段
- [ ] 删除凭据：`CredentialDeleteOutcome.InUse` → 拒绝并提示改指或停用那些 Game
- [ ] 首次运行守卫**两处都要有**：登录后的跳转（`Pages/Admin/Login.cshtml.cs`）与 `Components/Layout/AdminLayout.razor` 初始化；判断统一走 `GameAdminService.HasAnyAsync`
- [ ] 守卫例外只有 `/admin/login`、`/admin/logout`、新增游戏页及其保存动作；静态资源不被捕获；删掉最后一个 Game 会重新触发该状态
- [ ] 管理员仍是单一全局身份、无角色：所有 Game 对所有管理员可见，管理端服务不做作用域过滤
- [ ] Game/凭据的变更写结构化日志（管理员 user id、game id / credential id、动作、before/after 的非敏感字段）；不引入审计表
- [ ] `dotnet build` + `dotnet test` 通过

Parent spec: `.scratch/multi-game/spec.md`

## 备注

- 守卫不能只做中间件：Blazor 交互式路由不经过 ASP.NET Core 中间件，应用内导航会绕过去；也不能只做布局：初始导航那一步就没人管。
- **前提已满足**：迁移只在有存量数据时插入占位 Game，所以全新数据库里 `games` 表是空的，这个守卫在全新部署上一定会触发（见 issue 02）。
- 页面上所有 Game/凭据的读取走应用服务，不要在自己进程里发 HTTP 回环请求。
- 管理员身份是全局的（`IdentityUser`），不要为了"每个 Game 一个管理员"去扩展身份模型——那不在本次范围内。
- 占位 Game 的 AppID 为空是**升级路径的临时状态**，不是"可选配置"：表单必须要求补上它，`GameAdminView.IsConfigured` 也把它算作未配好。

- 游戏表单**没有 slug**、AppID 必填且必须为纯数字、最多 10 位、不为 0；首启守卫落在两处（登录成功后的重定向 + AdminLayout 的 OnAfterRenderAsync/LocationChanged），因为 Blazor 交互式路由不经过中间件。

## Comments

- 2026-09-24 **已完成并验证**。下方的勾选框保留为**当时的计划原样**：其中涉及 slug 寻址的条目已被同日「改用 Steam AppID 寻址」的反转取代，盲勾会把没发生过的事记成发生过，所以保持未勾选；实际实现以 `.scratch/multi-game/spec.md` 与 `docs/adr/0007-multi-game-support.md` 的修订章节为准。
- 验证证据（整批一次性跑过）：`dotnet build` 0 警告 0 错误；`dotnet test` **216 通过 / 0 失败**（真实 Testcontainers postgres:17）；`python addons/gd_feedback/tests/verify.py` **124 项 0 失败**；`sync_addon.py --check` 一致；Godot 宿主工程 0 错误；`docker compose config` 通过；迁移在真实 PostgreSQL 上验过**全新装 / 生产升级（带存量数据）/ 回滚**三条路径；端到端（真实 PG + Release 构建）验过按 AppID 寻址，以及 `game_required` / `game_not_found` / `game_disabled` / `game_mismatch` / `steam_unavailable` 全部命中。