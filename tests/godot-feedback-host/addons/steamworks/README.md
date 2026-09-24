# Steamworks 插件

Godot 4.7 C# Steamworks 插件,基于 [Facepunch.Steamworks](https://wiki.facepunch.com/steamworks/) 2.5.x,把 Steam API 封装成 Godot 节点/信号体系。所有类型位于 `Godot` 命名空间。

功能:客户端/专用服务器初始化、好友、大厅匹配、四种网络通道(P2P / P2P Messages / Normal Socket / Relay Socket)、Godot 高层多人(`MultiplayerPeer` / `MultiplayerApi` 封装)、队伍语音、Steam 云存储、成就、截图、覆盖界面等。

## 依赖

| 依赖 | 说明 |
|---|---|
| `GodotTools` NuGet 包 | `Log`、`SteamUtil`、`ProtoBufMsg`、扩展方法、`Project`/`Consts` 常量 |
| `GodotTools.SourceGenerators` | `[Single]` 特性 |
| GodotSharp.SourceGenerators(Chickensoft,随包传递) | `[SceneTree]`、`[Singleton]`、`[Instantiable]` 特性 |
| `addons/tools` 插件(同测试工程) | `Paths`、`Actions.Record`(录音按键)、`Game.AudioBus`、`GameManager` |

各平台 Steam 动态库位于 `assets/lib/`(`win64/steam_api64.dll`、`linux64/libsteam_api.so`、`osx/libsteam_api.dylib` 等),运行时由 `SteamUtil.InitEnvironment` 复制到 `user://`。

## 快速开始

1. 在项目设置中启用本插件(`addons/steamworks`)。插件会自动:
   - 注册自动加载单例 **`SteamManager`**(`src/SteamManager.tscn`);
   - 添加编辑器主屏配置界面(`SteamworksEditor`)。
2. 在编辑器主屏(或直接改 `SteamConfig`)配置 **AppId**(默认 480,Steam 官方测试用 Spacewar)。
3. 启动游戏,`SteamManager._Ready()` 自动完成:初始化 Steam 动态库环境 → 挂载全部 `S*` 组件 → 按配置以客户端(`SClient.Connect()`)或服务器(`SServer.StartServer`)模式连接。
4. 监听 `SClient.Instance.SteamClientConnected` 信号后再访问 Steam 数据:

```csharp
SClient.Instance.SteamClientConnected += () =>
{
    Log.Info($"已连接 {SteamClient.Name} {SteamClient.SteamId}");
};
```

## 目录结构

```
addons/steamworks/
├── SteamworksPlugin.cs      # EditorPlugin 入口(注册 autoload + 主屏)
├── src/
│   ├── SteamManager.tscn/.cs  # 自动加载单例,初始化并挂载所有组件
│   ├── SteamConfig.tres/.cs   # 插件配置(存于资源 meta)
│   ├── SteamworksEditor.tscn/.cs # 编辑器主屏配置界面
│   ├── component/           # S* 组件(每个对应一个 Steam 接口)
│   ├── socket/              # SteamSocket 四种连接封装(protobuf 消息)
│   ├── multiplayer/         # Godot MultiplayerPeer/MultiplayerApi 封装
│   ├── nodes/               # 队伍语音节点
│   ├── ui/                  # 大厅/用户信息/成就 UI 场景
│   ├── enum/                # Channel、LobbyType 等枚举
│   └── data/
├── assets/lib/              # 各平台 steam api 动态库
└── test/                    # 插件内测试场景(SteamTest、Test2d、TestMsg)
```

## 架构

- **`SteamComponent`**(`Node` 基类):`ProcessAlways`、默认不处理帧,`_Ready` 时把节点名设为类名。
- **`S*` 组件**:全部标注 `[Singleton]`(Chickensoft 生成器,生成静态 `Instance` 属性),由 `SteamManager` 统一 `AddChild`,通过 `SClient.Instance`、`SFriends.Instance` 等访问。
- **回调驱动**:初始化显式设置 `asyncCallbacks: false`，Facepunch 的 `SteamClient.RunCallbacks()` / `SteamServer.RunCallbacks()` 由 `SClient`/`SServer` 的 `_Process` 在 Godot 主线程驱动,仅在有效连接时开启。避免后台回调同时驱动并操作节点，参见 [客户端初始化说明](https://wiki.facepunch.com/steamworks/SteamClient.Init)。
- **游戏退出**:`GameManager.AddBeforeGameQuitAction` 注册了 `SClient.Disconnect`、`SServer.StopServer`、关闭所有 socket。

## 配置 — SteamConfig

静态类,读写 `src/SteamConfig.tres` 的 meta(编辑器主屏界面即写入这里):

| 配置 | 默认 | 说明 |
|---|---|---|
| `AppId` | 480 | Steam AppId |
| `Debug` | false | 显示 SteamManager 调试 UI(好友/成就/云存储/覆盖界面按钮面板) |
| `AsServer` | false | 启动时以专用服务器模式初始化(`SServer.StartServer`)而非客户端 |
| `CallbackDebug` | false | 打印 steamworks 底层调试回调 |
| `SampleRate` | 44100 | 语音采样率,需与音频流一致否则失真 |
| `FileLogLevel` / `GdLogLevel` | Information | Serilog 文件 / Godot 控制台日志等级 |
| `CustomBusLayout` | false | 使用自定义音频总线布局(队伍语音) |
| `SplashEnabled` | false | 启用启动画面(通过 override.cfg) |

## 组件 API

### SClient — 客户端生命周期

| 成员 | 说明 |
|---|---|
| 信号 `SteamClientConnected` / `SteamClientDisconnected` | 连接/断开 |
| `Connect()` | Debug 构建直接 `SteamClient.Init`;Release 走 `RestartAppIfNecessary`(未通过 Steam 启动会拉起 Steam)。失败弹对话框提示后退出游戏 |
| `Disconnect()` | `SteamClient.Shutdown()` |

### SFriends — 好友

| 成员 | 说明 |
|---|---|
| `Friends` / `Me`(静态) | `Dictionary<SteamId, Friend>`(含自己)/ 当前用户 |
| `Avatar(steamId, size)` | 异步取头像(`Godot.Image`,带缓存);`AvatarSize.Small/Middle/Large` = 32/64/184 |
| `DisplayCustom(str)` / `CloseDisplay()` | 设置/清除好友列表中的自定义状态 |
| `ShowGroup(group, size)` / `CloseGroup()` | "一起玩"分组展示 |
| `SetRichPresence(key, value, subKv)` | 富状态 |
| `OpenOverlay(OverlayType)` 等 | 打开覆盖界面:好友/社区/玩家/设置/成就/商店(`OpenStoreOverlay(appId)`)/网页(`OpenWebOverlay(url)`)/用户(`OpenUserOverlay`)/邀请(`OpenGameInviteOverlay`) |

### SMatchmaking — 大厅(单大厅模型)

约定:一个用户同一时刻只在一个大厅,所有操作经由此单例。创建时自动 `SetPublic` 并写入 `Project.Application.Version`,搜索时自动按版本过滤(保证客户端版本一致)。

| 成员 | 说明 |
|---|---|
| `Lobby`(静态属性) | 当前大厅(无效时 `Lobby.IsValid == false`) |
| `CreateLobbyAsync(maxUser = 4)` | 创建并进入 |
| `JoinLobbyAsync(lobby)` / `JoinLobbyAsync(friend)` | 加入大厅/加入好友所在大厅 |
| `LeaveLobby()` | 离开并发 `LobbyLeaved` 信号 |
| `Search(minSlots = 1, maxResult = 10, lobbyData = null)` | 按空位/自定义键值搜索 |
| `Invite(steamId)` | 邀请玩家 |
| `Kick(steamId)` | 仅房主可用,通过聊天协议 `[KICK_MEMBER]` 通知 |

信号:`LobbyCreated(result, id)`、`LobbyEntered(id)`、`LobbyLeaved(id)`、`LobbyInvite(id, steamId)`、`LobbyMemberJoined/Leave/Disconnected(id, steamId)`、`LobbyDataChanged(id)`、`LobbyMemberDataChanged(id, steamId)`、`LobbyChatMessage(id, steamId, message)`、`LobbyMemberKick(id, steamId)`。

### SNetworking — P2P(ISteamNetworking)

| 成员 | 说明 |
|---|---|
| 信号 `ReceiveData(steamId, channel, byte[] data)` | 收到 P2P 数据(每帧轮询所有 `Channel`) |
| 信号 `UserConnected / UserConnectFailed / UserDisconnected(steamId)` | 连接事件(自动接受会话请求) |
| `ConnectedIds` | 已连接玩家 |
| `SendP2P(steamId, string|byte[], channel, sendType = Reliable)`(静态) | 发送 |
| `Disconnect(steamId)` | 断开某玩家 |

连接建立后自动挂载 `TeamVoice`(队伍语音)。

### SNetworkingSocketMessages — P2P Messages(ISteamNetworkingMessages)

API 与 `SNetworking` 相同(信号 `ReceiveData`/`UserConnected`…、静态 `SendP2P`、`Disconnect`),底层走 `SteamNetworkingMessages`。

### SNetworkingSockets — Socket 工厂

| 方法 | 返回 | 说明 |
|---|---|---|
| `CreateNormal(port)` | `NormalServer` | 普通 IP socket 服务端(`NetAddress.AnyIp`) |
| `ConnectNormal(host, port)` | `NormalClient` | 连接 `host:port`(host 空 = localhost) |
| `CreateRelay(port)` | `RelayServer` | Steam 中继(SDR)socket 服务端 |
| `ConnectRelay(serverSteamId, port)` | `RelayClient` | 通过 SteamId 连中继服务端 |

`_Ready` 时自动 `ProtoBufUtil.Init()`(Godot 类型 protobuf 支持);游戏退出时关闭所有 socket。详见下文 [SteamSocket](#steamsocket-socket-封装)。

### SServer — 专用服务器

| 成员 | 说明 |
|---|---|
| `StartServer(modDir, desc, mapName = null)` | `SteamServer.Init`(GamePort 27015 / QueryPort 27016)+ 匿名登录,服务器名/版本取自 ProjectSettings |
| `ServerList(mapName, serverType = Internet)` | 异步查询服务器列表,`ServerType`:Internet/History/Favourites/Friends/LocalNetwork,返回 `List<ServerInfo>` |
| `StopServer()` | 关闭 |

### SRemoteStorage — Steam 云

`Write(filename, string|byte[])`、`ReadString/ReadBytes(filename)`(不存在返回 `null`)、`FileExists`、`FileDelete`、`FileSize`、`FileTime`、`GetFileList()`、`GetInfo()`(配额信息)。

### SUser — 用户与语音录制

| 成员 | 说明 |
|---|---|
| 信号 `RecordVoiceData(ulong steamId, byte[] compressData)` | 每帧有录音数据时发出(压缩格式) |
| `ServerConnected` | 与 Steam 服务器连接状态(P2P 依赖) |
| `StartRecord()` / `StopRecord()` | 录音开关;默认绑定输入动作 `Actions.Record`(按住说话,来自 addons/tools) |
| `DecompressVoice(bytes)`(静态) | 解压为单声道 16 位 PCM |
| `GetInfo()` | 打印用户信息(等级、采样率、NAT、手机验证等) |

### SUserStats — 成就

静态事件 `AchievementUnlocked(Achievement)`(成就 `current==0 && max==0` 视为解锁);成就列表直接读 `SteamUserStats.Achievements`。

### SScreenshots — 截图

连接后自动 `SteamScreenshots.Hooked = true`(Steam 按键 F12 截图);主动截图用 `SteamScreenshots.TriggerScreenshot()`。

### 其余组件(仅挂回调打日志)

`SApp`(DLC/启动参数,`AppInfo()`)、`SInput`(Steam 手柄)、`SInventory`(库存)、`SMusic`(音乐播放器)、`SParental`、`SParties`、`SRemotePlay`、`SServerStats`、`STimeline`、`SUgc`(创意工坊)、`SUtil`(电量/国家/语言等,`GetInfo()`)、`SNetworkingUtils`(网络调试输出)。需要更多功能时直接使用 Facepunch 对应静态类。

## SteamSocket — Socket 封装

`src/socket/`,基于 `ISteamNetworkingSockets`,消息统一为 **`ProtoBufMsg`**(GodotTools 库的类型:内含 `Type` + `Data`,`msg.Deserialize<T>()` 取负载)。

抽象基类 `SteamSocket`(`SteamComponent` 子类,作为 `SteamManager` 子节点运行,`_Process` 驱动 `Receive()`):

| 信号 | 说明 |
|---|---|
| `Connected(ulong steamId)` | 连接建立 |
| `Disconnected(ulong steamId)` | 连接断开 |
| `ReceiveMessage(ulong steamId, GodotObject msg)` | 收到消息,`msg` 为 `ProtoBufMsg` |

四个实现(服务端 `Create()` / 客户端 `Connect()` 后开始工作):

| 类 | 连接方式 | 特点 |
|---|---|---|
| `NormalServer` / `NormalClient` | `host:port` IP 直连 | 和普通 socket 一样,需公网 IP/端口 |
| `RelayServer` / `RelayClient` | SteamId + 端口 | 走 Steam 中继网络(SDR),无需公网,延迟较高 |

服务端发送:`Send(msg)` 广播全部连接、`Send(steamId, msg)` 发单人。客户端发送:`Send(msg)`。`Close()` 关闭(Server 侧会自动释放节点)。

注意:**只有对方在自己的好友列表里**,连接/消息才会转发为信号(内部经 `SFriends.Friends` 过滤)。

```csharp
// 服务端
var server = SNetworkingSockets.CreateNormal(10000);
server.Create();
server.Connected += id => Log.Info($"玩家 {id} 连入");
server.ReceiveMessage += (id, msg) =>
{
    if (msg is ProtoBufMsg m && m.Is<string>())
        Log.Info($"{id} 说: {m.Deserialize<string>()}");
};
server.Send(ProtoBufMsg.From("hello"));

// 客户端
var client = SNetworkingSockets.ConnectNormal("127.0.0.1", 10000);
client.Connect();
client.Send(ProtoBufMsg.From("hello"));
```

性能参考(源码注释中的实测结论):速度 `enet > p2p > normal > relay`,稳定性 `enet > normal > p2p > relay`。

## Godot 高层多人 — multiplayer/

当前使用 **SMP2 v2**，两端需要一起更新；与此前无消息头的 Peer 不互通。Facepunch 2.5.2 源码和包版本保持不变。模式/通道、会话握手、错误返回、关闭语义和有预算的接收已集中到 `SteamPeer`，Sockets 使用原生 lanes。`UnreliableOrdered` 当前仍兼容回退到 Reliable。

协议字段、配置键、迁移及测试入口见 [多人适配说明](docs/multiplayer.md)。`SteamIdToPeerId` 现在查询已握手的映射，未知来源返回 0。`SteamMultiPlayer` 使用后应调用 `Dispose()` 解除事件订阅；`LeaveLobby()` 用于结束当前会话后复用。

本次构建、1643 项单元测试和 78 项运行检查结果见 [多人验证记录](docs/multiplayer-verification.md)。真实 Steam 网络测试的环境限制也记录在该文件中。

把 Steam 网络包装成 Godot 原生多人 API,可直接配合 `MultiplayerSpawner` / `MultiplayerSynchronizer` / RPC 使用。

### SteamMultiPlayer(MultiplayerApiExtension)

包装 `SceneMultiplayer` 并绑定大厅生命周期。用法(类注释原文总结):

```csharp
// 服务端(房主)
var multiplayer = new SteamMultiPlayer();
GetTree().Multiplayer = multiplayer;
await multiplayer.CreateLobbyAsync(4);          // 1. 先创建大厅
var peer = SteamSocketPeer.CreateRelayServer(60937); // 或其他 peer
multiplayer.MultiplayerPeer = peer;             // 2. 再赋值 peer
// ... 正常使用 RPC/同步器;退出时 multiplayer.LeaveLobby()

// 客户端
var multiplayer = new SteamMultiPlayer();
GetTree().Multiplayer = multiplayer;
await multiplayer.JoinLobbyAsync(lobby);        // 1. 先加入大厅
var peer = SteamSocketPeer.CreateRelayClient(hostSteamId, 60937);
multiplayer.MultiplayerPeer = peer;             // 2. 赋值 peer
```

校验规则(`_SetMultiplayerPeer` 内):服务端 peer 必须已创建大厅且是房主;客户端 peer 必须已加入大厅且不是房主;违反直接抛异常并 `LeaveLobby()`。`LeaveLobby()` 会离开大厅并把 peer 切回 `OfflineMultiplayerPeer`。

### SteamPeer(MultiplayerPeerExtension 抽象基类)

- PeerId 映射:服务端固定 `ServerPeerId = 1`,客户端用 `SteamId.AccountId`;`SteamIdToPeerId` 转换。
- 发送目标语义(Godot 标准):`0` = 全部,负数 = 排除某 peer,正数 = 指定 peer;客户端始终发给服务器。服务器中继已启用。
- 两个实现:

| 工厂方法 | 底层 | 说明 |
|---|---|---|
| `SteamP2PPeer.CreateServer(socketType)` / `CreateClient(steamId)` | P2P 或 P2PMessage | 内置握手协议(`Consts.SocketHandShake`),客户端创建即发起握手,握手成功后 `PeerConnected` |
| `SteamSocketPeer.CreateRelayServer(port)` / `CreateRelayClient(steamId, port)` / `CreateNormalServer(port)` / `CreateNormalClient(host, port)` | Sockets | 默认端口 `60937` |

`socketType` 使用 `SteamSocketType` 枚举:`P2P` / `P2PMessage` / `Relay` / `Normal`。

## 队伍语音 — nodes/

- **`VoiceStream`**:核心流处理节点。父节点必须是 `IVoiceStreamPlayer`,否则报错自毁。使用 `BufferLength`（默认 0.1 秒）；PCM 只推送完整有效的 16 位单/双声道帧，不补满队列为静音。自动把播放器切到 `TeamVoice` 音频总线（不存在时回退到 `Master`，不做频谱分析）；用频谱分析发出 `Speak` / `Silent` 信号（连续 10 帧有声后开始说话，静音或停止时复位）。该检测读取整个 TeamVoice 总线，不能区分不同成员。
- **`VoiceStreamPlayer` / `VoiceStreamPlayer2D` / `VoiceStreamPlayer3D`**(`[GlobalClass]`):实现 `IVoiceStreamPlayer` 的 Godot 播放器,内部自带 `VoiceStream` 子节点。`ReceiveRecordVoiceData(steamId, compressData)` 喂入数据。
- **`NVoiceStreamPlayer`**:NAudio(WaveOutEvent)实现,仅限桌面平台,延迟约 50ms。
- **`TeamVoice`**(`[Singleton]`):队伍语音管理。`SNetworking` 连接后自动挂载。成员录音经 `SNetworking.SendP2P(..., Channel.Voice, SendType.NoDelay)` 广播,接收端按 steamId 分发到各成员播放器。

录音信号只携带实际压缩字节，并为接收者保留独立数据副本。录音缓冲区复用，解压临时缓冲区使用池；Steam 客户端无效时不调用录音 API，断开后停止录音轮询和输入。队伍成员忽略自己及重复加入，节点退出时解除语音信号订阅。

```csharp
TeamVoice.Instance.AddTeamMember(friend.Id);   // 加入队伍语音(自动开始播放)
TeamVoice.Instance.Mute(friend.Id);            // 静音某成员
TeamVoice.Instance.Play(friend.Id);            // 取消静音
TeamVoice.Instance.RemoveTeamMember(friend.Id);
TeamVoice.Instance.RemoveAllTeamMember();
TeamVoice.Instance.GetTeamMember(friend.Id);   // IVoiceStreamPlayer?
// 信号:MemberJoin(steamId) / MemberLeave(steamId)
```

## UI 场景 — ui/

均使用 `[SceneTree]` 生成 `Instantiate()` 静态工厂:

| 场景 | 说明 |
|---|---|
| `SteamLobby`(`Control`) | 大厅面板:创建/加入/退出、邀请、聊天、踢人、类型切换(Private/FriendsOnly/Public/Invisible)、可加入开关。静态 `SteamLobby.Search(...)` 代理大厅搜索 |
| `SteamUserInfo` | `SteamUserInfo.Instantiate(Friend)` 生成好友/成员卡片(头像+名称) |
| `AchievementUi` | `AchievementUi.Instantiate(Achievement)` 成就卡片 |

## 枚举 — enum/

| 枚举 | 值 |
|---|---|
| `Channel` | `Msg`(其他消息)、`Voice`(语音)、`Handshake`(P2P 握手)— P2P 通道划分 |
| `SteamSocketType` | `P2P`、`P2PMessage`、`Relay`、`Normal` |
| `AvatarSize` | `Small`(32×32)、`Middle`(64×64)、`Large`(184×184) |
| `LobbyType` | `Private`、`FriendsOnly`、`Public`、`Invisible` |
| `OverlayType` | `friends`、`community`、`players`、`settings`、`officialgamegroup`、`stats`、`achievements` |
| `UserOverlayType` | `steamid`、`chat`、`jointrade`、`stats`、`achievements`、`friendadd`、`friendremove`、`friendrequestaccept`、`friendrequestignore` |
| `RichPresenceKey` | `status`、`connect`、`steam_display`、`steam_player_group`、`steam_player_group_size` |
| `ServerType` | `Internet`、`History`、`Favourites`、`Friends`、`LocalNetwork` |

## 测试场景 — test/

| 场景 | 内容 |
|---|---|
| `SteamTest.tscn` | P2P 收发、Normal/Relay socket 服务端/客户端收发演示(选好友→发消息),配合 `TestMsg` protobuf 消息 |
| `Test2d.tscn` / `Test2dPlayer.tscn` | 多人 2D 移动同步测试 |

`SteamManager` 调试面板(Debug 配置开启)中 "Test" 按钮跳转到 `SteamTest`。

自动回归：`tests/unit/addons/steamworks/VoicePcmTests.cs` 验证 PCM 格式、符号、帧数与边界；运行 `res://tests/runtime/addons/steamworks/SteamworksRuntimeCheck.tscn` 检查播放状态、成员去重和退出清理，成功输出 `STEAMWORKS_RUNTIME_PASS`。检查使用合成音频数据，不录制麦克风、不发送网络语音；双账号真实通话仍需单独验证。

## 注意事项

- `SteamManager` 是 `CanvasLayer` 自动加载,主场景中可直接 `GetAutoLoad<SteamManager>()`。
- `SClient.Connect()` 在 Release 构建下若未通过 Steam 启动会触发 `RestartAppIfNecessary` 退出并由 Steam 拉起;调试请用 Debug 构建。
- SteamSocket 体系只对**好友列表内**的连接转发信号。
- `Kick` 踢人依赖聊天消息协议(`[KICK_MEMBER]` + steamId),不是 SDK 原生踢人。
- 语音采样率三处需一致:`SteamConfig.SampleRate`、`VoiceStream.SampleRate`、Steam 客户端实际值(`SteamUser.SampleRate`,连接后自动按配置设置)。
- Godot 类型(如 `Vector2`)要经过 protobuf 传输,需 `ProtoBufUtil.Init()`(`SNetworkingSockets._Ready` 已自动调用)。
