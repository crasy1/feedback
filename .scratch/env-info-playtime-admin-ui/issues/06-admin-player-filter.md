# 06: 按玩家筛选 + URL 构造收敛（修掉翻页丢筛选）

**What to build:** 管理端反馈列表支持按玩家筛选（SteamID64 前缀或昵称包含），详情页可一键跳到该玩家的全部反馈；同时把查询串构造收敛到一处，顺带修掉"翻页丢筛选"的既有 bug。

**Blocked by:** 05。（已 resolved）

**Status:** resolved

- [x] `AdminFeedbackService.ListAsync` 支持按玩家筛选：**纯数字 → SteamID64 前缀匹配**；否则 → **昵称不区分大小写的包含匹配**；空串不过滤；与 status / type / gameVersion **AND** 组合
- [x] LIKE 元字符按字面处理（`%` / `_` / `\` 全部转义）
- [x] 沿用 `Include(f => f.Player)` 与固定排序 `CreatedAt DESC, Id DESC`；分页与 `pageSize` 归一逻辑不变
- [x] `FeedbackList.razor`：新增查询参数 `[SupplyParameterFromQuery(Name = "player")]`、筛选输入框与 `player` 绑定
- [x] **收敛为一个私有 `BuildUrl(int page)`**：`ApplyFilters`（page 重置为 1）与 `GoToPage` 全部走它
- [x] **修掉既有 bug**：`GoToPage` 原先只写 `page`/`pageSize`，会丢掉 status / type / gameVersion；现在保留**全部**筛选（含新的 `player`）
- [x] 详情页加"查看该玩家全部反馈" → `/admin/feedback?player={SteamId}`，**清空其它筛选**
- [x] 非法 `player` 值不报错，降级为不过滤（`Enum.TryParse` 与 LIKE 都是纯过滤，不会抛）
- [x] 补上此前完全缺失的筛选/翻页回归测试
- [x] `dotnet build` + `dotnet test` 通过

Parent spec: `.scratch/env-info-playtime-admin-ui/spec.md`

## 实施记录

### 根因与结构性修复

原来的 bug 是"**两处各自拼查询串**"：`ApplyFilters` 写全 5 个参数，`GoToPage` 只写 2 个，于是翻页静默把查询范围放大。修复不只是补参数，而是收敛成单一出口，并顺手消灭了第二类隐患：

- 新增 `Services/AdminFeedbackQuery.cs`（record：`Status` / `Type` / `GameVersion` / `Player`），提供唯一的 `ToQueryParameters(page, pageSize)`；组件的 `BuildUrl(int page)` 是唯一调用点，`ApplyFilters` 与 `GoToPage` 都走它 → "两个出口漂移"在结构上不可能再发生。
- 同时把 `ListAsync` 的 4 个平铺参数换成这个 record。原先 `gameVersion` 与 `player` 都是 `string?` 且相邻，传反了编译器不会拦；现在不可能。

### 两个实测出来的坑（都写进了代码注释与测试）

1. **Npgsql 的两参 `ILike` 会生成 `ESCAPE ''`**，空转义符等于**关闭**转义处理。于是 `\_` 被当成"字面反斜杠 + 通配下划线"，搜含下划线的昵称永远 0 结果。用原生 SQL 对照确认 Postgres 默认反斜杠转义本身正确后，改用三参重载显式传转义字符：`EF.Functions.ILike(prop, pattern, LikeEscape)`。
2. **以反斜杠结尾的输入**若不转义，Postgres 会直接报 `pattern must not end with escape character`（500）。转义顺序必须是先 `\` 再 `%` / `_`。

### 测试

`AdminFeedbackServiceTests` 新增 7 条（原 3 条 `ListAsync` 调用同步改为传 record）：

- `List_filters_by_player_steam_id_prefix`（用唯一的 15 位数字前缀隔离共享库数据）
- `List_filters_by_player_nickname_case_insensitively`
- `List_player_filter_treats_like_wildcards_literally`（`_` / `%` 两例，各带一个"若未转义就会被误命中"的诱饵玩家）
- `List_player_filter_handles_trailing_backslash`
- `List_combines_player_filter_with_other_filters`（AND 语义）
- **`Query_parameters_preserve_every_filter_when_paging`** ← 这条就是"翻页丢筛选"的回归测试
- `Query_parameters_omit_empty_filters`

### 与 spec 的一处偏差（诚实记录）

Spec 里写的是"补上 UI 层筛选/翻页测试"。实际做到的是**服务层筛选语义 + 查询串构造**两层，**没有**真正的"点击驱动"UI 测试，原因有二，都不是偷懒：

1. 管理端是 `InteractiveServer` + `prerender: false`：初始 HTTP 响应里没有渲染后的标记，内容要等电路连上，所以 HTTP 断言拿不到表格。
2. `[SupplyParameterFromQuery]` 属性**只能由级联值提供**，`HtmlRenderer` 直接传参会抛
   `The property 'Page' … cannot be accepted explicitly because it only accepts cascading values`；要复刻 Blazor 内部的查询参数级联机制，成本与收益不成比例。

本仓库刻意不引 bUnit（MVP spec 的测试决定），所以"点击下一页"这一层留给人验。可以自动化且**已自动化**的部分是：筛选语义（含转义）、查询串构造、以及静态资源接线（`AdminAssetTests`）。原来那个 bug 的根因（两个出口漂移）已被结构性消除，并由 `Query_parameters_preserve_every_filter_when_paging` 盯住。
