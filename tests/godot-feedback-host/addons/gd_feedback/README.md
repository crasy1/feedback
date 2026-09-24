# GD Feedback 1.3.0

Steam 玩家反馈客户端（Godot 4.7 / .NET 10 插件）：玩家用 Steam 票据登录，提交反馈与评论，读取自己的反馈历史。
它对接的是 [Steam Game Feedback System](../../README.md) 的玩家 API（契约见 [docs/specs/player-api.md](../../docs/specs/player-api.md)）。

三条产品约束，写在最前面：

- **零宿主依赖**：`dependencies` 为空，不引用任何 Steam 绑定，不引用任何 Godot 之外的包。
- **核心引擎无关**：`FeedbackRuntime` / `FeedbackContracts` / `FeedbackAbstractions` 不含任何 `using Godot`，可用纯 .NET 测试驱动。
- **不自作主张**：不猜地图/角色等游戏语义（由调用方填），不打印票据与访问令牌，不在未开启调试开关时走调试登录。唯一的例外是**环境信息**（OS / GPU / CPU / 内存）：它会自动采集并作为默认值上报，宿主显式传的值优先，见下文「环境信息自动采集」。

## 支持矩阵

| 项目 | 值 |
|---|---|
| Godot | 4.7.2（`Godot.NET.Sdk/4.7.2`） |
| .NET | 目标框架 `net10.0`；代码只使用 .NET 8 的 BCL 面，便于落进更早的宿主 |
| 平台 | 与宿主一致（插件本身不含平台相关代码；Steam 出票在宿主侧） |
| 验证方式 | `python addons/gd_feedback/tests/verify.py`（离线，不需要 Godot） |

## 安装

1. 把 `addons/gd_feedback/` 整个目录放到目标项目的 `addons/` 下。
2. 在 Godot 里 **构建 C# 项目**（`dotnet build`），否则插件脚本还没编译进程序集。
3. Project Settings → Plugins 启用 **GD Feedback**：它会注册自定义节点 `FeedbackClient`，并把 `GdFeedback` 注册为 Autoload；禁用时两者都会被移除。
4. 首次导入时 Godot 会为脚本生成 `.cs.uid`、为 `icon.svg` 生成 `.import` —— 这些文件**不在**发布白名单里，由宿主编辑器生成。

## 快速开始

宿主必须先提供票据来源（插件不代劳，见下节所有权表）。以 Facepunch.Steamworks 为例：

```csharp
using GdFeedback;
using Steamworks;

/// <summary>把 Facepunch 的 Web API 票据交给插件；票据是十六进制串，且必须释放。</summary>
public sealed class SteamTicketProvider : ITicketProvider
{
	public async Task<string?> GetWebApiTicketAsync(string identity, CancellationToken cancellationToken)
	{
		AuthTicket? ticket = await SteamUser.GetAuthTicketForWebApiAsync(identity, 10.0);
		if (ticket is null)
		{
			return null;   // 出票失败一律 fail closed，插件不会退回任何"看起来成功"的路径
		}
		try
		{
			return Convert.ToHexString(ticket.Data).ToLowerInvariant();
		}
		finally
		{
			ticket.Dispose();
		}
	}
}
```

还要把**当前游戏运行的 Steam AppID** 交给插件（同样是宿主的事，因为插件不引用 Steam）。
给了它，插件就会自动把请求路径补成 `/g/{appId}/api/...`，于是 `BaseUrl` 只填服务地址，
接入时**不需要手抄任何标识字符串**：

```csharp
using GdFeedback;

/// <summary>把当前游戏运行的 Steam AppID 交给插件；本项目本来就在用它调 SteamClient.Init。</summary>
public sealed class SteamAppIdProvider : IGameAppIdProvider
{
	public string? GetSteamAppId() => SteamClient.AppId.ToString(CultureInfo.InvariantCulture);
}
```

不实现它也能用：那时插件原样使用 `BaseUrl`，你需要自己把 `/g/{appId}` 写进 `BaseUrl`。

C# 调用：

> 票据长度：Steam 的 Web API 票据最大 **2560 字节**，`Convert.ToHexString` 之后是 **5120 字符**（真机实测就是这个长度）。
> 服务端的限额是 8192；客户端只做**合理性上限**（16384），不会误挡真实票据——这条差异是刻意的，见 ADR-0004。

```csharp
FeedbackClient feedback = GetNode<FeedbackClient>("/root/GdFeedback");
feedback.TicketProvider = new SteamTicketProvider();

feedback.FeedbackSubmitted += id => GD.Print($"已提交反馈 #{id}");
feedback.FeedbackFailed += (status, code, message, retryable) => GD.PushError($"{code}: {message}");

_ = feedback.SubmitAsync(new PlayerFeedbackDraft(
	PlayerFeedbackType.Bug,
	"进入竞技场时客户端崩溃",
	"1v1 模式加载 arena_01 必现崩溃，普通对局不受影响。",
	GameVersion: "1.2.3",
	BuildNumber: "456",
	Locale: "zh-CN",
	Map: "arena_01",
	Character: "mage"));
// 操作系统 / 显卡 / CPU / 内存不用手填：插件会自动采集（见「环境信息自动采集」）。
// 传了就用你传的，没传才用采集值。
```

GDScript 调用（**只能等信号，不能 await C# 的 Task** —— 这是 Godot 的既定规则）：

```gdscript
extends Control

@onready var feedback: Node = get_node("/root/GdFeedback")

func _ready() -> void:
	feedback.feedback_submitted.connect(_on_submitted)
	feedback.feedback_failed.connect(_on_failed)

func submit_bug() -> void:
	feedback.submit_async("Bug", "进入竞技场时客户端崩溃", "1v1 加载 arena_01 必现崩溃。", {
		"game_version": "1.2.3",
		"map": "arena_01",
		"character": "mage",
	})

func _on_submitted(feedback_id: int) -> void:
	print("已提交 #%d" % feedback_id)

func _on_failed(status_code: int, error_code: String, message: String, retryable: bool) -> void:
	push_error("%s: %s" % [error_code, message])
```

等某个操作完成（GDScript）：

```gdscript
func submit_and_wait() -> void:
	feedback.submit_async("Bug", "标题", "正文")
	await feedback.feedback_submitted     # 也可以 await feedback.feedback_failed
```

## 环境信息自动采集

提交反馈时，插件会补上调用方没给的机器信息，作为排查依据：

| 字段 | 来源 | 取不到时 |
|---|---|---|
| `operating_system` | `OS.get_name()` + `OS.get_version_alias()`；Linux 再拼发行版名 | 留空 |
| `gpu` | `RenderingServer.get_video_adapter_name()` | 留空（headless / 服务端构建必为空） |
| `cpu` | `OS.get_processor_name()` | 留空（Android 与 Web 上 Godot 没有实现） |
| `memory_total_mb` | `OS.get_memory_info()` 的 `physical`（字节换算成 MB） | 留空 |

三条规则：

1. **宿主显式传的值优先**，插件只补空缺。想覆盖就照常传 `OperatingSystem` / `Gpu` / `Cpu` / `MemoryTotalMb`（GDScript 侧是 `operating_system` / `gpu` / `cpu` / `memory_total_mb` 键）。
2. **越界只会被丢弃，不会让提交失败**：CPU 超过 120 字符、内存不在 1..4194304 MB 内，就直接不带这个字段。玩家既没有输入它们、也无法修正它们；服务端同样是丢弃而不是报 400。
3. **没有开关，也无法关闭**：传空值会被当成"没给"，一样走采集。这是刻意的取舍——环境信息是排查 bug 的必要上下文，默认行为才会真的有人上报。

> `gpu` 走 `RenderingServer` 而不是 `OS`：Godot 4.x 的 `OS` 上并没有"显卡名"这个 API，只有 `OS.get_video_adapter_driver_info()`（返回驱动名 + 版本，而且文档警告首次调用可能耗时数秒），所以不用它。

## 配置

配置是 `FeedbackConfig`（继承 `Resource`，`[GlobalClass]`），按顺序查找：

1. `res://feedback_config.tres` —— **宿主自己的文件，推荐放这里**（升级插件不会覆盖它）
2. `res://addons/gd_feedback/feedback_config.tres` —— 插件目录内兜底（插件不附带此文件，按需自建）
3. 内置默认值（`http://localhost:5087`，identity `feedback-api`）

| 属性 | 默认 | 说明 |
|---|---|---|
| `BaseUrl` | `http://localhost:5087` | 反馈服务地址，必须是绝对的 http/https。**只填服务地址**：`/g/{appId}` 前缀由宿主的 `IGameAppIdProvider` 自动补全 |
| `Identity` | `feedback-api` | 票据 identity，**必须与后台里该游戏配置的票据 identity 一致**（默认就是 `feedback-api`） |
| `RequestTimeoutSeconds` | `15` | 单请求超时 |
| `AllowDebugLogin` | `false` | 调试登录开关；服务端对应开关只在 Development 生效 |
| `DebugSteamId` | 空 | 调试登录声明的 SteamID64（17 位数字） |
| `Proxy` | 空 | 显式代理；空表示 .NET 默认策略 |
| `CacheAccessToken` | `false` | 是否把访问令牌写入 `user://gd_feedback/token.json` |
| `VerboseLogging` | `false` | 是否打印 Info 级日志（Warning/Error 始终打印） |

本机开发时不想改 `.tres`，可以用 EditorSettings 覆盖 BaseUrl（不回写项目、不进版本库；只在编辑器与编辑器运行时生效）：

```gdscript
EditorSettings.set_setting("gd_feedback/base_url", "http://localhost:5087")
```

**代理的跨平台差异**（务必知道）：`HttpClient` 在 Windows 会读环境变量与用户/WPAD 代理设置，在 Linux（含 Steam Deck）默认**绕过一切代理**。要稳定行为就在 `Proxy` 里显式写地址。

## 公开 API

C#（强类型）：

```text
Task<FeedbackResult<PlayerSession>>         LoginAsync()
Task<FeedbackResult<PlayerFeedback>>        SubmitAsync(PlayerFeedbackDraft draft)
Task<FeedbackResult<PlayerFeedback>>        SubmitAsync(string type, string title, string content, Dictionary metadata = null)
Task<FeedbackResult<IReadOnlyList<PlayerFeedback>>> ListMineAsync()
Task<FeedbackResult<PlayerFeedbackDetail>>  GetDetailAsync(int feedbackId)
Task<FeedbackResult<PlayerFeedbackComment>> AddCommentAsync(int feedbackId, string content)
Task                                        ClearSessionAsync()
void                                        Configure(FeedbackConfig config = null, ITicketProvider ticketProvider = null,
													  IGameAppIdProvider gameAppIdProvider = null)
```

信号（全部在主线程发出）：

| 信号 | 参数 |
|---|---|
| `feedback_logged_in` | `(string steam_id, string steam_name)` |
| `feedback_submitted` | `(int feedback_id)` |
| `feedback_comment_added` | `(int feedback_id, int comment_id)` |
| `feedback_mine_loaded` | `(Array items)` —— 每项是 Dictionary |
| `feedback_detail_loaded` | `(Dictionary detail)` |
| `feedback_failed` | `(int status_code, string error_code, string message, bool retryable)` |

列表项 / 详情的字典键（snake_case）：`id`、`type`、`title`、`content`、`status`、`game_version`、
`build_number`、`operating_system`、`gpu`、`cpu`、`memory_total_mb`、`playtime_minutes`、`locale`、`map`、
`character`、`created_at`（ISO 8601 UTC）；
详情另带 `comments`（每项为 `id`、`author_type`、`content`、`created_at`）。

`memory_total_mb` 与 `playtime_minutes` 缺失时为 `0`（`playtime_minutes` 由服务端填写，客户端只读）。

## 错误码

按 `error_code` 分支，不要解析 `message`（message 是英文兜底文案，UI 请自行本地化）。

| 错误码 | 含义 | 可重试 |
|---|---|---|
| `validation_failed` | 本地校验未通过（请求未发出），或服务端 400 带明细 | 否 |
| `ticket_unavailable` | 票据提供者交不出票据（Steam 未登录/不可用） | 否 |
| `ticket_invalid` | 票据为空、长得不像票据（>16384 字符），或被服务端判为无效 | 否 |
| `invalid_steam_id` | 调试登录声明的 SteamID64 形状非法 | 否 |
| `steam_verification_rejected` | 服务端 401 且 Steam 明确否决了这张票（无效/过期/appid·identity 不匹配） | 否 |
| `steam_verification_unavailable` | 服务端 401 但**没能验成**（网络、超时，或该游戏在后台还没配好 Steam 凭据）；与票据本身无关 | 是 |
| `game_not_found` | 登录端点 404：反馈服务上没有这个 AppID 的游戏（该游戏还没在后台建出来，或两边 AppID 不一致）。**配置问题**，与票据无关 | 否 |
| `game_disabled` | 登录端点 403：这个游戏已被管理员停用。**配置问题**，与票据无关 | 否 |
| `unauthorized` | 非登录端点的 401（重登一次后仍失败） | 否 |
| `not_found` | 反馈不存在或不属于当前玩家（服务端对两者一律 404） | 否 |
| `rate_limited` | 429：命中服务端限流 | 是 |
| `server_error` | 服务端 5xx | 是 |
| `server_rejected` | 其它 4xx | 否 |
| `invalid_response` | 响应不是期望的 JSON 形状 | 否 |
| `transport_failed` | 请求未送达（网络失败/超时，已自动重试一次） | 是 |
| `invalid_base_url` / `invalid_configuration` | 配置问题 | 否 |

重试策略：传输失败自动重试**一次**；**429 不自动重试**——服务端窗口是 10 分钟级别，
在毫秒级重试只会在同一额度上再打一次，因此由 UI 决定何时重试（`retryable = true`）。

## 安全边界

- 票据与访问令牌只存在于 `FeedbackRuntime` 内部；日志只记录错误码、状态码与 SteamID。
- 令牌默认**只在内存**；`CacheAccessToken` 打开才写 `user://`，`ClearSessionAsync()` 会删除。
- 调试登录默认关闭，且只接受合法的 17 位 SteamID64；服务端侧的对应开关只在 Development 生效。
- 出票失败一律 fail closed：宁可报 `ticket_unavailable`，也不降级成匿名或"看起来成功"。
- 所有权（只能读写自己的反馈）由服务端强制；插件把 404 原样上报为 `not_found`，不区分"不存在"与"不属于你"。
- 分发时要提醒玩家：程序集可被反编译，**不要把任何长期密钥放进游戏**。本插件不含长期密钥。

## 所有权表

| 内容 | 所有者 |
|---|---|
| 玩家 API 契约、所有权、限流、状态与回复 | 服务端（本仓库） |
| Steam 初始化、AppId、票据获取与释放 | **宿主**（插件不引用 Steam） |
| `res://feedback_config.tres` 的实际取值（生产 BaseUrl 等） | 宿主 |
| 反馈表单 UI、本地化、错误码到文案的映射 | 宿主 |
| 传输、JSON、本地校验、令牌缓存、重试与错误码 | 本插件 |
| Godot 适配器（`Node`、信号、`CallDeferred` 回主线程） | 本插件 |
| 环境信息采集（OS / GPU / CPU / 内存）与默认值 | 本插件（宿主显式传的值优先） |

## 验证

```powershell
python addons/gd_feedback/tests/verify.py
```

离线运行，不需要 Godot 游戏工程；四个阶段：身份与白名单、核心引擎无关性、纯 .NET 干净宿主 fixture + harness
（30+ 条断言，含"校验先于网络""缺票 fail closed""日志不含令牌""401 只重登一次"等），以及 Godot 宿主 fixture 的
Debug/Release 严格构建（`TreatWarningsAsErrors=true`，0 warning）。需要引擎内探针时再补 `-GodotPath`：

```powershell
python addons/gd_feedback/tests/verify.py --godot-path D:\Scoop\apps\godot-mono\current\godot-mono.exe
```

打包（按 `addon.manifest.json` 白名单，并断言归档内容与白名单逐项一致）：

```powershell
python addons/gd_feedback/tools/package.py                 # 输出到 <repo>/artifacts/gd_feedback-1.0.0.zip
python addons/gd_feedback/tools/package.py --verify-only      # 只校验身份与白名单
```

## 移植到其他项目

1. 复制（或解压归档）`addons/gd_feedback/` 到目标项目的 `addons/` 下。
2. `dotnet build`，然后在 Project Settings → Plugins 启用。
3. 在目标项目根建 `feedback_config.tres`（`FeedbackConfig` 资源），填生产 `BaseUrl`（只填服务地址）。
4. 在宿主里实现 `ITicketProvider`（Steam 出票）并赋给 `FeedbackClient.TicketProvider`。
5. 在宿主里实现 `IGameAppIdProvider`（返回当前游戏运行的 Steam AppID）并赋给
   `FeedbackClient.GameAppIdProvider`——玩家 API 的 `/g/{appId}` 前缀由插件自动补全，
   接入方不必手抄标识字符串。宿主本来就在用同一个值调 `SteamClient.Init`。

本插件的权威定义在服务端仓库：`CONTEXT.md`（术语）、`docs/adr/0004-godot-feedback-client-addon.md`（决定与取舍）。

## 宿主侧建议（安装到 Godot 游戏项目时自行粘贴）

装到另一个 Godot 仓库（例如 `Arena of Realms`）时，建议把下面两条术语补进**那个仓库**根部的 `CONTEXT.md`，
以区别于该仓库里已有的战斗表现词汇（该仓库的"战斗浮字 / CombatFeedback"指的是伤害飘字，与本插件无关）：

```markdown
**玩家反馈**：玩家通过 Steam 认证提交的反馈或建议，由玩家反馈客户端上报到反馈服务，可附评论。
_Avoid_: 战斗浮字、伤害数字、反馈特效

**访问令牌**：反馈服务在验证 Steam 票据后签发的短期令牌，玩家反馈客户端凭它调用玩家 API。
_Avoid_: JWT、session、API key
```

该仓库还需要自行完成（本插件不代劳）：启用插件、把 `GdFeedback` Autoload 指向宿主的票据提供者、
把 `FeedbackConfig.BaseUrl` 指向 `https://<反馈服务域名>`（**不要**带 `/g/...`，那段由
`IGameAppIdProvider` 交出的 AppID 自动补全）、
在 `project.godot` 里确认 `Steam` 使用**正式 AppId**（不是 480 测试 App，且要与后台里该游戏配置的 Steam AppID 一致），
并为新增的场景更新该仓库的场景目录文档。
