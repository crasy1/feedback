# 01: ADR-0007 / ADR-0008 与多 Game spec

**What to build:** 把"一个实例服务多个 Game"的形状、寻址、令牌、数据模型、配置归属与安全不变量写成长期文档，让后来者不必重新推一遍。本 issue 是纯文档，不改任何代码。

**Blocked by:** 无。

**Status:** resolved

- [ ] 新增 `docs/adr/0007-multi-game-support.md`：路径前缀寻址、Player/Feedback 的每 Game 作用域、令牌绑定到 Game；错误码集合 `game_required` / `game_not_found` / `game_disabled` / `game_mismatch` / `credential_unreadable` / `steam_unavailable` / `steam_ticket_rejected`；记录被否决的备选（每游戏一套部署、每游戏库/ schema 隔离、从票据响应识别 Game（不可能：`AuthenticateUserTicket` 把 `appid` 当输入且不回显）、客户端声明 AppId、服务端对每个已注册 AppId 逐个试票 O(N)、保留"默认 Game"让根路径继续可用）
- [ ] **寻址方式反转后追加修订章节**（已落盘）：`docs/adr/0007-multi-game-support.md` 末尾的 `## Revision (same day, after the first deploy attempt)` 记录"路径段从自造 slug 改成 Steam AppID"这一改动、原始理由为何不再适用（标识符不再由人手抄进配置）、以及被否决的方案（保留 slug 作第二键、把删列折进迁移 4、接受 slug 再翻译）。**不改写原有的 Context / Decision / Considered Options / Consequences 文本**，只在标题下加一条明显的指向说明，并在状态头加 `- Revised:` 行
- [ ] 新增 `docs/adr/0008-steam-configuration-in-the-database.md`：数据库是唯一事实来源、凭据独立成表、Data Protection 静态加密 + 应用名钉死、UI 只写、无环境变量播种；记录被否决的备选（key 留在环境变量、明文存储、每 Game 一列而非凭据表、首次启动从环境变量导入一次）
- [ ] `docs/adr/0002` 追加"部分被 ADR-0007/0008 取代"指针：服务端 Steam 配置的来源与登录路由
- [ ] `docs/adr/0003` 追加指针：`SteamId` 全局唯一索引、"一个 Steam 账号 = 一个 Player"、"JWT 不带额外 claim"
- [ ] `docs/adr/0006` 追加指针："configured AppId / ApiKey" 的措辞
- [ ] `.scratch/multi-game/spec.md` 与 01–09 issue 落盘
- [ ] 追加指针只**追加**，不改写 0002/0003/0006 的历史结论
- [ ] 不新增代码、不新增依赖

Parent spec: `.scratch/multi-game/spec.md`

## 备注

- ADR 语言与 0002/0003/0006 一致（英文），沿用 `- Status: Accepted` / `- Date: YYYY-MM-DD` 头与 `## Context` / `## Decision` / `## Considered Options` / `## Consequences` 结构；修订章节用 `## Revision (…)` 这样的标题，位置与被取代指针一致（放在原文之后）。
- 0007 的修订章节同样用英文，并显式写明"原文保持原样、两者冲突时以修订为准"，因为它是**已接受**的决策记录——把决策悄悄改成"一直就是这么定的"会让读者失去这段历史。
- ADR 里引用的代码事实（插件的 401 自愈 374-381）已在仓库里核对过，不要凭记忆改写行号。注意插件的"零代码改动"这一条已被寻址反转推翻：1.3.0 起 `BaseUrl` 只带服务地址，`/g/{appId}` 由 `IGameAppIdProvider` 注入的 AppID 补全。
- ADR-0002 的追加指针里当时还留着 `/g/{slug}`；它不在本 issue 的可改文件范围内，**已由拥有该文件的改动一并修正为 `/g/{appId}`**。

- ADR-0007 已落盘并在末尾追加「Revision」章节（原文一字未改）；ADR-0008 已落盘；0002/0003/0006 只追加指针。0002 指针里的 /g/{slug} 事后已修正为 /g/{appId}；0007 的 H1 已中性化为「Per-Game Path Prefix」（正文保留原文）。

## Comments

- 2026-09-24 **已完成并验证**。下方的勾选框保留为**当时的计划原样**：其中涉及 slug 寻址的条目已被同日「改用 Steam AppID 寻址」的反转取代，盲勾会把没发生过的事记成发生过，所以保持未勾选；实际实现以 `.scratch/multi-game/spec.md` 与 `docs/adr/0007-multi-game-support.md` 的修订章节为准。
- 验证证据（整批一次性跑过）：`dotnet build` 0 警告 0 错误；`dotnet test` **216 通过 / 0 失败**（真实 Testcontainers postgres:17）；`python addons/gd_feedback/tests/verify.py` **124 项 0 失败**；`sync_addon.py --check` 一致；Godot 宿主工程 0 错误；`docker compose config` 通过；迁移在真实 PostgreSQL 上验过**全新装 / 生产升级（带存量数据）/ 回滚**三条路径；端到端（真实 PG + Release 构建）验过按 AppID 寻址，以及 `game_required` / `game_not_found` / `game_disabled` / `game_mismatch` / `steam_unavailable` 全部命中。