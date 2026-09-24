# GD Feedback Lab（Godot 4.7.2 .NET 宿主测试工程）

这是一个**完整可打开的 Godot 4.7.2 .NET 工程**，把 [`addons/gd_feedback`](../../addons/gd_feedback/README.md) 以真实宿主的方式装了进去：插件文件就在 `res://addons/gd_feedback/`，由 `Godot.NET.Sdk` 的默认 glob 编进本程序集，和游戏里一模一样。工程里还 vendored 了第三方的 [`addons/steamworks`](addons/steamworks/README.md)（Facepunch.Steamworks 的 Godot 封装），用来**出真实 Steam 票据**——插件本身不引用 Steam，出票是宿主的责任，这里正好把宿主那一半也演示出来。

用途：**手动联调 + 无界面自检**。它不参与服务端的 `GameFeedback.slnx`（那是 ASP.NET 解决方案），也不在 `dotnet test` 里——这里跑的是引擎本体。

## 打开与运行

0. 先起服务端。本地测试的主路径是 **compose 起最新构建**（应用在 `http://127.0.0.1:3000`）：

   ```bash
   docker compose --env-file .env.local up -d --build
   # 或 python scripts/run_local.py --docker（同一条命令）
   ```

   本工程的 BaseUrl 默认是 `http://127.0.0.1:3000`——**只填服务地址**：玩家 API 的 `/g/{appId}` 前缀由
   `LabAppIdProvider` 提供的 Steam AppID（读的就是 steamworks 那份 `SteamConfig.AppId`）自动补全，
   所以接入时不需要手抄任何标识字符串。点「健康检查」可以先确认 `/health` 通了。
1. Godot 4.7.2 **.NET** 版打开本目录（`tests/godot-feedback-host/`）。
2. 构建 C#（编辑器右上角会提示，或命令行 `dotnet build tests/godot-feedback-host/GdFeedbackHost.csproj`）。
3. Project Settings → Plugins，启用 **GD Feedback**（它会注册 `FeedbackClient` 自定义节点，并把 `GdFeedback` 注册为 Autoload）。想用真实 Steam 票据，再启用 **steamworks**（它会把 `SteamManager` 等注册为 Autoload 并负责 Steam 初始化）。
4. 运行 `Main.tscn`（F5）。界面是运行期用代码搭出来的，`.tscn` 只有一个根 `Control`，所以合并冲突不会发生在场景文件里。

## 三种登录方式

| 方式 | 怎么用 | 需要什么 |
|---|---|---|
| **调试登录**（默认最省事） | 勾上「调试登录」，SteamID64 填 17 位数字，点「登录」 | 服务端用 `docker compose --env-file .env.local up -d --build` 起（Development + 跳过 Steam 验票）；不需要 Steam，也不需要该游戏配好凭据。（用 `dotnet run` 起服务时把 BaseUrl 改成 `http://localhost:5087/g/default`） |
| **Steam 出票** | 取消「调试登录」、勾上「用 Steam 出票」，点「登录」 | Steam 客户端在运行 + 已启用 steamworks 插件（本工程默认勾选「用 Steam 出票」；Steam 没就绪时会在日志区明确告诉你，并以 `ticket_unavailable` fail closed） |
| **手输票据** | 取消两个勾，把 `GetAuthTicketForWebApi("feedback-api")` 得到的**十六进制**票据粘进「票据」框，点「登录」 | 任何能出票的地方（例如从游戏里捞一张票据过来）；不需要在本工程里初始化 Steam |

三种方式都不成立时（没勾调试登录、Steam 未就绪、票据框为空），插件会以 `ticket_unavailable` **fail closed**——这正是要观察的行为之一。容器若以 Production 运行（即没用 `--env-file .env.local`），服务端会走真实验票路径。

## 界面上能验什么

- **健康检查** → 宿主侧直接 GET `/health`，先确认栈起来了再点登录
- **登录** → `feedback_logged_in`
- **提交反馈** → `feedback_submitted`（带上 `game_version`/`build_number`/`operating_system`/`locale`/`map`/`character` 元数据；用的是 GDScript 友好的重载，顺手验证字典路径）
- **我的反馈** → `feedback_mine_loaded`（逐条打印 id/type/status/title）
- **读取详情** → `feedback_detail_loaded`（含评论）
- **追加评论** → `feedback_comment_added`
- **离线自检** → 空标题必然在本地校验被挡下，用来确认「校验先于网络」
- 任何失败都会打印 `feedback_failed`：**错误码 + HTTP 状态码 + 是否可重试**，可以拿它逐条核对[错误码表](../../addons/gd_feedback/README.md#错误码)

想观察所有权与限流：用**另一个** SteamID64 调试登录后去读不属于自己的 id（应得 `not_found`）；连续提交 6 条以上反馈（第 6 条应得 `rate_limited`, 可重试）。

## 无界面自检

不需要界面、不需要网络、不需要 Steam，也不需要服务端在跑：

```bash
godot-mono.console.exe --headless --path tests/godot-feedback-host res://Main.tscn -- --lab-selfcheck
```

它分两阶段，成功后打印 `GD_FEEDBACK_LAB PASS` 并以 0 退出：

1. 空标题提交 → 断言拿到 `validation_failed`（证明信号经 `CallDeferred` 回到主线程后确实发出来了）；
2. 打开调试登录、把 BaseUrl 指到 `http://127.0.0.1:1` → 断言拿到 `transport_failed` 或 `server_error`（证明 `System.Net.Http` 在 Godot 运行时里真的能发请求，且失败被正确分类）。

> 注意用 `godot-mono.**console**.exe`：GUI 版可执行文件不产生 stdout。

## 插件副本与同步

`addons/gd_feedback/` 是从插件源按 `addon.manifest.json` 白名单**安装**出来的副本（真实宿主就是这么装的）。改插件源码后刷新它，或校验有没有漂移：

```bash
python tests/godot-feedback-host/tools/sync_addon.py          # 安装/刷新
python tests/godot-feedback-host/tools/sync_addon.py -Check    # 只校验（打印 GD_FEEDBACK_HOST_SYNC PASS/FAIL）
```

Godot 首次导入会自己生成 `*.cs.uid`、`*.import` 和 `.godot/`；这些是生成物，`-Check` 会忽略它们（`.godot/` 已在仓库 `.gitignore` 里）。

## vendored 的 steamworks 插件

`addons/steamworks/` 是**第三方**插件（Facepunch.Steamworks 2.5.2 的 Godot C# 封装，作者 weitaoji），只是被放进这个测试工程用来出票；`sync_addon.py` **只管** `gd_feedback` 的副本，不会碰它。要更新它，从上游/`arena` 工程整目录覆盖即可。

它在真实宿主里的两个影响，值得知道：

- **构建会有 49 条告警**（CS8618/CS8625/CS8602…，全部来自它的代码），所以本工程的 `GdFeedbackHost.csproj` **刻意不设** `TreatWarningsAsErrors` —— 真实宿主 `arena.csproj` 同样不设。`gd_feedback` 自己的严格门禁在 `verify.py` 第 4 阶段（干净宿主 fixture + `TreatWarningsAsErrors=true` + 0 warning），不在这个工程里。
- **Steam 初始化归它管**：启用插件后才会有 `SteamManager` autoload 去 `SteamClient.Init`。它的 `SteamConfig.tres` 默认 AppId 是 **480**（Spacewar 测试 App）；要跟真实游戏对齐，得改成正式 AppId，并且**在后台「游戏」页把该游戏的 Steam AppID 配成同一个值**——AppID 已经不在环境变量里了，两边不一致就会验票失败。

## 这个工程不做什么

- 不替代插件自带的验证门禁：`python addons/gd_feedback/tests/verify.py` 才是发布门禁（离线、严格构建、4 个阶段；仅第 3 阶段的干净宿主 harness 一段就有 116 项检查）；本工程是它的**引擎内补充**，也是给人点着用的台子。
- 不给 `gd_feedback` 引入 Steam 依赖。插件只依赖注入进来的 `ITicketProvider`；**出票这一段是本工程（宿主）自己实现的**（`src/LabTicketProvider.cs`），插件侧至今零 Steam 引用——`verify.py` 第 2 阶段会一直盯着这条不变量。
- 不包含真实密钥、不写入访问令牌（`CacheAccessToken = false`）；票据与访问令牌都不会进日志（只记长度）。
