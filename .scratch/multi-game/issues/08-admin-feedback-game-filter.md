# 08: 反馈列表 game 列与 game 筛选、详情 game 行

**What to build:** 管理端反馈列表增加"属于哪个 Game"的列与筛选（URL 查询键 `game`，默认全部），详情页增加 game 行；筛选继续只走唯一的 URL 出口。

**Blocked by:** 03（需要 Game 解析与 `Feedback.GameId` 已经可用）。

**Status:** resolved

> **寻址方式在首次部署尝试之后被反转**：筛选值现在是 Game 的 **Steam AppID**，不是 slug。见 `docs/adr/0007-multi-game-support.md` 的 `## Revision (same day, after the first deploy attempt)`。

- [ ] `Services/AdminFeedbackQuery.cs` 增加 `Game` 字段，并加进 `ToQueryParameters`；**不另开第二处拼串**（顺手把 `["game"]` 上方"按 slug 过滤"的旧注释改成 AppID）
- [ ] `AdminFeedbackService.ListAsync` 支持按 Game 过滤，与 status / type / gameVersion / player **AND** 组合；仍用固定排序 `CreatedAt DESC, Id DESC`，`Include(f => f.Game)`
- [ ] `Components/Pages/Admin/FeedbackList.razor`：新增 game 列（停用的 Game 也要显示并标注"已停用"）；新增 `game` 筛选控件，绑定 `[SupplyParameterFromQuery(Name = "game")]`，下拉项来自 `GameAdminService.ListAsync`（包含停用项，但**只列有 AppID 的**——没有 AppID 的 Game 不可寻址，没有任何反馈能筛到它）
- [ ] 筛选值用 Game 的 **Steam AppID**（与 `/g/{appId}` 路径同一个标识符）：`AdminFeedbackQuery.Game` 是 `string?`，`ListAsync` 用 `f.Game!.SteamAppId == gameAppId` 过滤，下拉项 `<option value="@game.SteamAppId">` 并带一个 `全部游戏`（空值）选项
- [ ] 空/缺省 `game` = 全部 Game；取不到对应 Game 的值（手改 URL）不报错，但因为过滤条件是字面 AppID 相等，结果集为空——下拉框里没有匹配项，管理员会看到一张空表却看不出原因，需要一个说得清的空态文案；顺手把那个未知值保留成一个"（不存在）"选项，否则控件会显示"全部游戏"而实际在筛选
- [ ] `ApplyFilters` / `GoToPage` 全部经 `BuildUrl(int page)`，翻页保留包括 `game` 在内的全部筛选
- [ ] `Components/Pages/Admin/FeedbackDetail.razor`：新增 game 行（名字 + AppID，可跳到该 Game 的列表）；"该玩家全部反馈"链接仍清空其它筛选，且只在同一个 Game 内
- [ ] 补充 `AdminFeedbackServiceTests`：game 单独过滤、game 与其它筛选 AND 组合、`Query_parameters_preserve_every_filter_when_paging` 覆盖新增的 `game`
- [ ] `dotnet build` + `dotnet test` 通过

Parent spec: `.scratch/multi-game/spec.md`

## 备注

- 上一轮把查询串收敛成唯一出口（`AdminFeedbackQuery.ToQueryParameters`）就是为了修"翻页丢筛选"；这次新增 `game` 必须复用同一处，否则同一个 bug 会原地复活。
- 筛选用 AppID（实现如此，原计划是 slug）：权威决策只说"URL 查询键 `game`"，而 AppID 与 `/g/{appId}` 路径、下拉框的值是同一个标识符；更重要的是它由客户端在运行时派生、不用人手抄，所以"筛选一个不存在的标识符"不再是一类常见事故。
- 本仓库刻意不引 bUnit，管理端是 `InteractiveServer` + `prerender: false`，所以"点击驱动"的 UI 测试做不了；可自动化的是服务层筛选语义与查询串构造，交互留人工验。

- 筛选值最终是 **Steam AppID**（与 /g/{appId} 路径、下拉框的值同一个标识）。未知值**显式暴露**（下拉保留一个「（不存在）」选项 + 错误提示），不退化成「全部游戏」——静默放大查询范围正是这个仓库修过一次的事故类型。

## Comments

- 2026-09-24 **已完成并验证**。下方的勾选框保留为**当时的计划原样**：其中涉及 slug 寻址的条目已被同日「改用 Steam AppID 寻址」的反转取代，盲勾会把没发生过的事记成发生过，所以保持未勾选；实际实现以 `.scratch/multi-game/spec.md` 与 `docs/adr/0007-multi-game-support.md` 的修订章节为准。
- 验证证据（整批一次性跑过）：`dotnet build` 0 警告 0 错误；`dotnet test` **216 通过 / 0 失败**（真实 Testcontainers postgres:17）；`python addons/gd_feedback/tests/verify.py` **124 项 0 失败**；`sync_addon.py --check` 一致；Godot 宿主工程 0 错误；`docker compose config` 通过；迁移在真实 PostgreSQL 上验过**全新装 / 生产升级（带存量数据）/ 回滚**三条路径；端到端（真实 PG + Release 构建）验过按 AppID 寻址，以及 `game_required` / `game_not_found` / `game_disabled` / `game_mismatch` / `steam_unavailable` 全部命中。