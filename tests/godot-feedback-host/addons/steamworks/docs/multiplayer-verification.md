# 多人适配验证记录

2026-09-15，Godot 4.7.2 Mono / .NET 10 / Windows。

## 结果

- `dotnet build arena.sln --no-restore -v minimal`：0 错误，116 个项目警告。
- `dotnet test tests/unit/Arena.Gameplay.Tests.csproj --no-build`：1643 通过、0 失败，约 1 分 53 秒。包含上一轮的 PCM 单元测试。
- gdmcp 运行 `res://addons/steamworks/test/MultiplayerRuntimeCheck.tscn`：`STEAM_MULTIPLAYER_PASS checks=78`。
- 主场景 TitleScreen 成功加载，窗口关闭通知执行后正常停止；恢复了原来的 UnitFxMapPreview 编辑场景。
- `git diff --check -- addons/steamworks` 通过。没有暂存或提交变更。

运行日志位于本地 `.scratch/steamworks-multiplayer/`：build.log、tests.log、runtime-final.json、main-runtime.json、main-exit.json。

## 78 项运行检查覆盖

协议头、版本及长度校验；四类传输的内存模拟；原生 Godot 属性 getter/setter；非零通道、可靠/不可靠模式及 UnreliableOrdered 兼容回退；单播、广播、排除广播、部分发送失败；非法目标与超长包；来源准入、拒绝连接、握手超时和取消；无 Hello、重复 Hello、替换 Socket 的握手预算；队列包数和字节上限；可靠队列溢出断开；踢人后拒绝重新握手；快速重连旧包隔离；Close/force 信号语义；真实 SceneMultiplayer 三人发现与 A→服务器→B 转发；根路径配置；反复创建/释放后的 C# 事件订阅清理。

测试中的握手超时与可靠队列溢出会故意输出错误级日志，这是断言所覆盖的预期失败路径。整体结论以 PASS/FAIL 标记为准。

## 审查与修复闭环

Standards 最终剩余 0 项。已修复 Legacy P2P 在回调断开 Steam 后继续读取、重复维护默认配置、待握手数量命名与实际计算不一致的问题。

Spec 最终生产代码剩余 0 项。已分别限制待握手和在线连接数量；Sockets 从 native 接入开始计时；重复 Hello 保留首次时间；替换同 SteamId 原生连接会退役旧绑定并重新计时。

运行验证发现并处理了两个测试/适配问题：

- 自建 SceneMultiplayer 必须设置 RootPath。测试补齐配置，同时修复 SteamMultiPlayer 转发 NodePath 时错误访问 Variant.Obj 的原有行为。
- Godot 自定义 C# Signal 的订阅保存在生成的委托字段，不由原生 GetSignalConnectionList 统计。Debug 回归检查已改为观察本插件生成委托的数量和目标，并验证 Dispose 后恢复基线；没有在生产代码中加入反射。

## 依赖与修改边界

Facepunch.Steamworks 2.5.2 源码、包版本和 DLL 均未修改。DLL SHA256 与研究阶段相同：`BC99B9ECFE87A642A641FC52403F9661F6AE329BEEB1FE756C70A2D1C8158C94`。

本次多人生产代码、测试入口及说明均在 addons/steamworks 内。构建重载时编辑器曾将 project.godot 的音频总线 UID 改写为同一资源路径，核对资源 UID 后已恢复原表示。仓库中先前的语音改动和其他并行工作保留。

## 尚未覆盖

最终验证时 Steam 客户端未运行，SDK 返回 NoSteamClient。78 项多人检查本来就使用内存传输，不依赖其他账号；它们不能替代真实 Steam 双账号、NAT、Relay、线上丢包和压力测试。

额外运行上一轮语音场景时，7 项播放检查通过；需要 Steam 客户端的会话复位检查跳过，后续 Steam 相关步骤没有完成，因此未将该场景整体记为通过。

SMP2 v2 与此前无消息头的协议不互通，两端须同步更新。UnreliableOrdered 暂时回退为 Reliable，尚未实现不重传的顺序不可靠传输。
