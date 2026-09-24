# 05: 管理端共享样式、视觉刷新与新字段展示

**What to build:** 把散落在 4 处的内联样式收敛为一份共享样式表，做一次视觉整理，并在详情页展示 CPU / 内存 / 游玩时长、在列表新增"游玩时长"列。

**Blocked by:** 02。（已 resolved）

**Status:** resolved

- [x] 新建 `wwwroot/admin.css`，在 `App.razor` 里 `<link>`；**删除** `Components/Layout/AdminLayout.razor`、`Components/Pages/Admin/FeedbackList.razor`、`Components/Pages/Admin/FeedbackDetail.razor`、`Pages/Admin/Login.cshtml` 里的内联 `<style>`
- [x] **不引入任何 CSS 框架**；不新增依赖
- [x] 视觉规格：状态徽章 待处理=琥珀 / 处理中=蓝 / 已解决=绿 / 已关闭=灰；类型徽章 Bug=红系 / Suggestion=蓝系 / Other=灰；表格斑马纹 + 悬浮高亮；筛选条吸顶；窄屏表格横向滚动（**不加 JS**）；统一的加载态与空态；保留深色顶栏；中文文案不变
- [x] 详情页展示 `Cpu` / `MemoryTotalMb`（显示为 GB 或 MB）/ `PlaytimeMinutes`（换算成小时）；缺失值统一显示 `—`
- [x] 列表新增"游玩时长"列（**不放 CPU / 内存**——CPU 型号串太长会把列宽挤爆）
- [x] 顺手清理 `wwwroot/app.css:32,37` 两处对从未定义的 `--bs-secondary-color` 的模板死引用
- [x] 玩家头像 / 昵称 / Steam 链接的既有展示不回归
- [x] 验证列表与详情的显示（有值 / 无值两种情况）
- [x] `dotnet build` + `dotnet test` 通过

Parent spec: `.scratch/env-info-playtime-admin-ui/spec.md`

## 实施记录

- **共享样式表**：`wwwroot/admin.css`，所有规则限定在 `.admin-root` / `.admin-login` 之下，避免影响仓库里其它页面（模板首页等）。删除的 4 处内联 `<style>` 分别来自 `AdminLayout.razor`、`FeedbackList.razor`、`FeedbackDetail.razor`、`Login.cshtml`。
- **登录页的坑**：`Login.cshtml` 是 `Layout = null` 的独立 HTML，不经过 `App.razor`，所以它**自己**用 `<link rel="stylesheet" href="/admin.css" />` 引入，并把登录页专属样式限定在新增的 `body.admin-login` 之下。这条接线由新增的 `AdminAssetTests` 盯着（HTTP 断言登录页含 `/admin.css` 与 `admin-login`，且 `/admin.css`、`/admin.js` 都能 200 返回）。
- **状态展示的选择**：列表里的状态用**带徽章配色的下拉框**（`.badge.status-select`）——它同时承担"一眼看出状态"和"行内改状态"两件事，避免徽章 + 下拉框并排的冗余。详情页是只读展示，用纯徽章。
- **徽章映射单点化**：`FeedbackStatusDisplay.GetCssClass` 与新增的 `FeedbackTypeDisplay.GetCssClass`，列表与详情共用一份，避免两处漂移（沿用本仓库"状态中文显示使用同一映射"的既有做法）。
- **格式化**：`PlaytimeMinutes` 分钟 → `< 60` 显示"N 分钟"，否则"N.N 小时"；`MemoryTotalMb` → `>= 1024` 显示"N.N GB"，否则"N MB"；空值统一 `—`。
- **验证**：`dotnet build` 0 错误、本 issue 新增代码 0 警告；`dotnet test` 103/103 通过。
- **未能自动化的一处（已在 issue 06 记录）**：管理端是 `prerender: false`，HTTP 响应里没有渲染后的标记；`[SupplyParameterFromQuery]` 又只能由级联值提供，导致 `HtmlRenderer` 无法直接传参渲染列表页。所以"渲染出的表格长什么样"没有自动测试，只有静态资源接线这一层被 `AdminAssetTests` 覆盖。
