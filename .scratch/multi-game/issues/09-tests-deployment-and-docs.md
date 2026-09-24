# 09: 测试、部署配置与文档收尾

**What to build:** 把前五个阶段落到测试与部署上：集成 fixture 改为播种 Game/凭据、补齐多 Game 覆盖、清掉部署文件里的 Steam 配置、补上密钥环的挂载与备份说明，并校对全套文档与 ADR 一致。

**Blocked by:** 04、05、06、07、08。

**Status:** resolved

> **寻址方式在首次部署尝试之后被反转**：`/g/{appId}` 取代 `/g/{slug}`，冒烟脚本的参数是 `--app-id`，插件也改到了 1.3.0。见 `docs/adr/0007-multi-game-support.md` 的 `## Revision (same day, after the first deploy attempt)`。

- [ ] `tests/GameFeedback.Tests/Infrastructure/GameFeedbackApplicationFactory.cs`：删掉 `Steam:AppId = "480"` 之类的配置注入，改为播种 Game 行 + 凭据行
- [ ] `FakeSteamHandler` 支持按 Game 应答（两 Game 用例要能断言每次登录/取时长打到 Steam 的 AppId 与 key 是对的）
- [ ] 按 `docs/testing.md` 新增章节补齐覆盖（多 Game 作用域、配置与凭据、管理端、校验），至少包括：
  - [ ] 按 Steam AppID 寻址：路径段解析到 `SteamAppId` 相符的 Game，令牌的 `game` claim 是它的数字主键
  - [ ] A 游戏令牌在 B 游戏路径上 `401`；缺 `game` claim 的老令牌 `401`
  - [ ] 根路径五个端点 `404 game_required`
  - [ ] 没有 Game 的 AppID → `404 game_not_found`；**非数字路径段同样 `404 game_not_found`**；停用 Game → `403 game_disabled`；**有 AppID 但缺凭据 → `401 steam_unavailable`**；凭据解不开 → `401 credential_unreadable`
  - [ ] **`SteamAppId = NULL` 的 Game 行（升级占位游戏）不可寻址**：任何路径都解析不到它
  - [ ] 跨 Game 的 feedback id 读成 `404`；同一 Steam 账号在两款游戏里是两个独立 Player
  - [ ] API key 不出现在任何管理端响应体、渲染页面、DTO、日志行（含校验失败重渲染）
  - [ ] 删除在用 Game / 在用凭据 → 域结果拒绝（`HasData` / `InUse`）
  - [ ] 改动既有 Game 的 AppID 后新 AppID 立即生效、旧 AppID `404 game_not_found`；管理端反馈筛选的 `game` 值就是 AppID，且翻页保留该筛选
  - [ ] 空 `games` 表时的强制跳转；`/admin/login`、`/admin/logout`、新增游戏页、静态资源不被拦
  - [ ] 凭据加密往返 + 解密失败是独立错误（`401 credential_unreadable`，不是 `steam_unavailable`）
  - [ ] **全新数据库跑完迁移后 `games` 表为空**（占位 Game 只在有存量数据时插入），首次运行守卫因此会触发
  - [ ] 游玩时长用解析出的 Game 的 AppId 查询
  - [ ] 限流按 `{appId}:{steamId}` 分区：一个 Game 额度耗尽不影响同账号在另一个 Game
  - [ ] 插件：宿主注入的 AppID 把路径补成 `/g/1910980/api/...`；宿主返回非数字 AppID（如 slug）时 `invalid_configuration` 且零请求
- [ ] `scripts/smoke_player_api.py`：`--app-id <appId>`（纯数字，否则带明确信息以退出码 `2` fail fast），所有玩家路由改写成 `/g/{appId}/api/...`，`Location` 断言为 `/g/{appId}/api/feedback/{id}`，并覆盖根路径 `404 game_required`、未知/停用 Game
- [ ] `docker-compose.yml` / `.env.example` / `scripts/run_local.py` / `README.md`：删除 `Steam__ApiKey` / `Steam__AppId` / `Steam__Identity` 及其说明，保留 `Steam__DebugSkipTicketValidation`
- [ ] `appsettings.example.json`：去掉 `Steam` 段里的 `ApiKey` / `AppId` / `Identity`（若整段只剩 debug 开关，保留该开关）
- [ ] 部署配置：新增 `DataProtection__KeysPath`，为密钥环加持久卷并写进备份清单（含"丢失密钥环 = 凭据需重录"的说明）
- [ ] 部署文档说明首次运行流程：启动 → 管理员登录 → 被引导到"新增游戏" → 录入 **AppID** 与凭据 → 玩家登录才可用
- [ ] 校对 `docs/overview.md`、`docs/architecture.md`、`docs/domain-model.md`、`docs/security.md`、`docs/testing.md`、`docs/specs/player-api.md`、`docs/specs/admin-ui.md` 与 ADR-0007（含其修订章节）/ 0008 一致，无残留的根路径 `/api/...`、slug 寻址或环境变量 Steam 配置。**`docs/adr/0002-dual-identity-and-steam-auth.md` 的追加指针已修正为 `/g/{appId}`**
- [ ] 确认插件改动已落盘：`python addons/gd_feedback/tests/verify.py` 与 `sync_addon.py --check` 仍通过（本轮插件从 1.2.0 到 1.3.0，新增 `IGameAppIdProvider` 与 `/g/{appId}` 补全，**不再是"零代码改动"**）
- [ ] `dotnet build` + `dotnet test` 通过；`git diff --check` 干净

Parent spec: `.scratch/multi-game/spec.md`

## 备注

- 本仓库的测试原则是"只断言外部行为"，主接缝仍是 `WebApplicationFactory` + 真实路由/JWT/Identity/限流 + Testcontainers PostgreSQL，只桩掉 Steam 的 HTTP 传输。
- 别把 `Steam:DebugSkipTicketValidation` 一起删掉：它是唯一留下的 Steam 相关环境变量，且必须继续被 `ASPNETCORE_ENVIRONMENT=Development` 门禁（生产配置了它就拒绝启动）。
- 文档里凡是引用行号的（插件 341-343、374-381），改动这些文件后要复核是否仍然成立。

- dotnet test 216 通过 / 0 失败（基线 207：删 15 条 slug 时代用例、增 24 条）；插件校验 124 项 0 失败、两份副本同步一致；冒烟脚本参数是 --app-id（非数字以退出码 2 fail fast）；插件 1.3.0 与 Godot 宿主 LabAppIdProvider 已接好；文档校对含 ADR-0002。

## Comments

- 2026-09-24 **已完成并验证**。下方的勾选框保留为**当时的计划原样**：其中涉及 slug 寻址的条目已被同日「改用 Steam AppID 寻址」的反转取代，盲勾会把没发生过的事记成发生过，所以保持未勾选；实际实现以 `.scratch/multi-game/spec.md` 与 `docs/adr/0007-multi-game-support.md` 的修订章节为准。
- 验证证据（整批一次性跑过）：`dotnet build` 0 警告 0 错误；`dotnet test` **216 通过 / 0 失败**（真实 Testcontainers postgres:17）；`python addons/gd_feedback/tests/verify.py` **124 项 0 失败**；`sync_addon.py --check` 一致；Godot 宿主工程 0 错误；`docker compose config` 通过；迁移在真实 PostgreSQL 上验过**全新装 / 生产升级（带存量数据）/ 回滚**三条路径；端到端（真实 PG + Release 构建）验过按 AppID 寻址，以及 `game_required` / `game_not_found` / `game_disabled` / `game_mismatch` / `steam_unavailable` 全部命中。