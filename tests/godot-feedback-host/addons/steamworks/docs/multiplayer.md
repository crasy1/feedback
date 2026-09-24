# 多人适配与 SMP2 v2

## 本次约束与范围

用户要求保持 Facepunch.Steamworks 2.5.2 源码和包版本不变，所有生产修改和新增回归夹具都位于 addons/steamworks。原计划中的依赖修复因此改为插件内协议边界，不修改 NuGet 缓存、不读取运行时私有结构，也不引入反射补丁。

保留 P2P、P2P Messages、Normal Socket、Relay Socket 四种后端和现有主机中继拓扑。公共建连工厂保留原参数，并支持可选 SteamNetworkOptions。SteamIdToPeerId 只返回已握手登记的 ID；未知来源返回 0。

## 通信格式与兼容

两端必须一起更新到 SMP2 v2。SteamMultiPlayer 创建大厅时写入 steam_multiplayer_protocol=2；加入后验证该字段，不兼容时离开并返回明确错误。直接使用 SteamPeer 的旧协议连接不会完成握手，客户端超时错误也会提示所需版本。

所有高层多人数据包含 32 字节的小端头：

| 偏移 | 字段 |
|---|---|
| 0..3 | ASCII SMP2 |
| 4 | 版本 2 |
| 5 | Data / Hello / Welcome / Acknowledge / Disconnect |
| 6 | 实际 Reliable 或 Unreliable 模式 |
| 7 | 保留，必须为 0 |
| 8..11 | 发送者 Godot peer ID |
| 12..15 | 游戏逻辑通道 |
| 16..23 | 发送会话标识 |
| 24..31 | 接收会话标识 |

头部由插件生成并在入队前验证，交给 Godot 的数组只包含负载。最大负载为 512 KiB 减 32 字节；更严格的后端限制通过发送失败返回。UnreliableOrdered 暂时按上游兼容方式映射为 Reliable，接收端也报告 Reliable；没有宣称实现不重传的顺序不可靠模式。

低层通道 0（普通消息）、1（语音）、2（握手）保留。高层物理通道为 3 + logicalChannel * 2 + unreliableBit。Sockets 映射到原生 lane，连接时配置对应数量的 lanes；P2P/Messages 注册并轮询相应通道。Default channel 0 的可靠/不可靠流分开，游戏数据不会落到语音或握手通道。

Facepunch 的 Sockets 回调 channel / messageNum / recvTime 不用于恢复元数据；消息头提供所需模式和通道。不依赖它暴露原生 flags。

## 握手与生命周期

- 主机成功创建传输后为 Connected；客户端为 Connecting。
- 客户端生成 Godot 合法正整数 ID 和随机非零会话标识，服务器 ID 固定 1。
- Hello 提交客户端 ID/会话；Welcome 返回服务器会话并回显客户端会话；Ack 回显双方会话后登记连接。
- 不同 lanes 没有共同排序。先于 Ack 到达、但回显正确双方会话的数据也可完成确认，避免合法可靠数据被丢弃。
- 客户端绑定预期 Steam 服务器；SteamMultiPlayer 为服务器和客户端设置大厅成员缓存准入策略。普通数据只接受登记绑定的 SteamId、peer ID 与双方会话组合。
- 握手有数量及时间上限，RefuseNewConnections 对新成员生效；快速重连会退役旧会话，近期旧 Hello/Data 被拒绝。每个 Peer 最多保留 256 个退役记录，整个 Peer 关闭后可释放。
- Close 先置 Disconnected，再解绑和清理，不发本地 peer_disconnected；force=true 也不发本地信号。关闭幂等，不存在旧的 Task.Delay 清理误关新会话。
- P2P 底层没有带确认的远端关闭保证，Disconnect 控制包是尽力通知；大厅成员离开事件也会关闭对应游戏连接。需要重新连接时创建新 Peer。
- 房主踢人先从本地连接中移除并阻止其重新握手，再发大厅通知。SteamMultiPlayer.Dispose 解除全部外部事件；示例 Test2d 退出时释放会话。

## 预算与观测

SteamConfig.NetworkOptions 从 SteamConfig Resource 的 meta 读取快照，也可向建连工厂传入 SteamNetworkOptions。新设置不要求修改现有资源，缺省值如下：

| meta key | 默认 |
|---|---:|
| NetworkChannelCount | 4 |
| NetworkReceivePacketsPerFrame | 128 |
| NetworkReceiveBatchSize | 16 |
| NetworkReceiveBudgetMilliseconds | 2 |
| NetworkMaxQueuedPackets | 1024 |
| NetworkMaxQueuedBytes | 8388608 |
| NetworkHandshakeTimeoutSeconds | 10 |
| NetworkMaxPendingConnections | 32 |
| NetworkMaxConnections | 32 |

待握手上限只计算未完成的握手，已连接人数另由 MaxConnections 控制。Sockets 从 native 接入时占用待握手额度并计时，没有 Hello 或持续重发 Hello 都不能绕过超时。

Legacy P2P 批量读取；Messages 按通道轮转，Sockets 按原生接收队列批次读取。后两者显式传 receiveToEnd:false。包数是每次轮询的硬预算，时间在每批前检查，单次 native 调用/回调本身不可被抢占。不同全局组件和 Socket Peer 有各自预算，不是整个游戏进程共用一个计时器。

队列同时限制包数与负载字节数。不可靠包超限计数后丢弃；可靠包超限明确记录并断开来源，防止静默丢包。Peer 暴露发送/接收包与字节、发送失败、拒绝包、不可靠丢弃、当前队列字节和高水位；字节计数包含游戏包协议头，不包含控制包，不是链路层带宽。

P2P/Messages 的原生接收预算由全局 SteamConfig 驱动；传入工厂的会话选项控制该 Peer 的通道、队列和握手，Sockets 另使用其会话选项控制自己的原生接收预算。

## 验收

addons/steamworks/test/MultiplayerRuntimeCheck.tscn 是 Debug 无网络回归入口，挂载同目录的 MultiplayerRuntimeCheck.cs。使用 gdmcp 运行该场景；成功打印 STEAM_MULTIPLAYER_PASS，失败打印 STEAM_MULTIPLAYER_FAIL。测试场景由 Godot 工具创建，不修改生产主场景；按本次“仅插件内修改”的限制，场景用途记录在本文。

覆盖：编解码边界、原生 getter/setter、非零通道与模式、单播/广播/排除广播、发送失败、非法目标与长度、握手超时、准入、force/Close、跨 lane ACK 排序、踢人、队列溢出、快速重连旧包、SceneMultiplayer 三人发现与 A→服务器→B 转发、会话订阅释放。

该检查使用真实 Godot MultiplayerPeerExtension 和 SceneMultiplayer，但 Steam 传输是内存模拟。双账号实际通信、NAT/Relay 和丢包压力测试需要独立环境，不以此模拟结果替代。

## 参考

- 本地 godot-docs-search 的 class_multiplayerpeer.md、class_multiplayerpeerextension.md、class_scenemultiplayer.md。
- [GodotSteam 固定提交](https://codeberg.org/godotsteam/godotsteam/src/commit/5853a7741d174cfa37edee1ca44a11581a989d0b/godotsteam_multiplayer_peer.cpp)：模式/通道状态、分批接收、握手登记与关闭。
- 上游也将 UnreliableOrdered 回退为 Reliable；force 信号行为则以 Godot 文档约定为准。
