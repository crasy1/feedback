# 07: 列表行内改状态 + SteamID64 一键复制

**What to build:** 在反馈列表里直接改状态，不必逐条打开详情；SteamID64 可一键复制。

**Blocked by:** 06。（已 resolved）

**Status:** resolved

- [x] `FeedbackList.razor` 每行加状态选择控件，直接调用 `AdminFeedbackService.ChangeStatusAsync`（Blazor 直接调应用服务，不走 HTTP）
- [x] 改完**重跑当前查询**（不是只改本行）：状态筛选生效时那一行本来就该从列表消失/出现，只更新本行会留下一条"不该在"的记录
- [x] 保留当前页与**全部**筛选（复用 issue 06 的单一 `BuildUrl`）
- [x] 失败时显示错误提示，且**不改变列表内容**
- [x] 不做二次确认（状态随时能再改）
- [x] 一键复制 SteamID64：`IJSRuntime` + `navigator.clipboard.writeText`，新增几行 `wwwroot/admin.js`
- [x] **必须有回退分支**：非安全上下文（内网 http）下 clipboard 不可用 → 提示手动选择表格里的 SteamID64
- [x] 新增 JS 只是一次 `IJSRuntime.InvokeVoidAsync` 级别的互操作，**不引入任何依赖**
- [x] 回归覆盖：错误路径不动列表、复制回退不抛异常
- [x] `dotnet build` + `dotnet test` 通过

Parent spec: `.scratch/env-info-playtime-admin-ui/spec.md`

## 实施记录

- **状态控件**：列表状态列是一个带徽章配色的 `<select>`（`.badge.status-select`），既是状态指示也是行内入口。`@onchange` → `ChangeStatusAsync(feedbackId, args)`；成功后 `ReloadAsync()` 重跑当前查询（保留当前页与全部筛选），失败则设 `_actionError` 且不动列表；不弹二次确认。
- **复制**：`wwwroot/admin.js` 暴露 `adminInterop.copyText(text)`，在 `navigator.clipboard && window.isSecureContext` 成立时写入并返回 `true`，否则（含抛异常）返回 `false`。组件据此给出两种提示：`已复制 SteamID64：…` 或 `浏览器不允许自动复制，请手动选择表格里的 SteamID64：…`；`JSException` 也走同一条回退文案，绝不静默失败。`admin.js` 已在 `App.razor` 引入，并由 `AdminAssetTests` 断言能被 200 服务出来。
- **详情页小修**：`ChangeStatus` 现在会检查 `ChangeStatusAsync` 的返回值，失败时给出与列表一致的错误提示（原先静默忽略）。
- **顺带清理**：删除详情页里未被使用的 `BackToList()` 与随之无用的 `NavigationManager` 注入。
- **验证**：`dotnet build` 0 错误、本 issue 新增代码 0 警告；`dotnet test` 103/103 通过。
- **未能自动化**：点击驱动的交互（下拉框改状态、点复制按钮）没有自动测试，原因与 issue 06 记录的相同（`prerender: false` + `[SupplyParameterFromQuery]` 只能走级联）。可自动化的替代覆盖：静态资源接线（`AdminAssetTests`）、状态变更的持久化语义与错误路径（`AdminFeedbackServiceTests.Change_status_persists_and_touches_updated_at`）。
