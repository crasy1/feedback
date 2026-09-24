# 04: Godot 插件 1.1.0 — 硬件信息自动采集与契约扩展

**What to build:** 插件适配层自动采集 CPU / GPU / 内存 / OS 并随反馈上报；契约与服务端对齐；版本升到 1.1.0。核心三件套保持引擎无关。

**Blocked by:** 02。（已 resolved）

**Status:** resolved

- [x] `FeedbackContracts.cs`：`PlayerFeedbackDraft` 增加 `Cpu` / `MemoryTotalMb`；`PlayerFeedbackValidation` 增加 `CpuMaxLength = 120` / `MemoryMaxMb` 与归一化方法；`IPlayerFeedbackView` / `PlayerFeedback` / `PlayerFeedbackDetail` 增加 `Cpu`、`MemoryTotalMb`、`PlaytimeMinutes`
- [x] `FeedbackRuntime.cs`：请求体加 `cpu` / `memoryTotalMb`；响应解析加三个键；提交前只做归一化不做致命校验
- [x] `FeedbackClient.cs` 适配层自动采集（**唯一可接触 Godot 的地方**）：`OS.GetProcessorName()`、`OS.GetMemoryInfo()["physical"]`（字节 → MB）、`RenderingServer.GetVideoAdapterName()`、`OS.GetName()` + `OS.GetVersionAlias()`（Linux 再拼 `OS.GetDistributionName()`）
- [x] **优先级：宿主显式传的值优先，适配层只补空缺**；采集失败 / 平台不支持一律 `null`，**不阻断提交**
- [x] GDScript 便捷重载：snake_case 键表**输入与输出两侧**都补 `cpu` / `memory_total_mb` / `playtime_minutes`（输入侧新增 `MetadataInt`，同时接受 int 与 float，因为 GDScript 只有 float）
- [x] **不加** `CollectEnvironmentInfo` 之类的开关
- [x] 同步删除 `README.md` 示例与 `tests/godot-feedback-host/src/FeedbackLab.cs` 里手写的 `operating_system` / `gpu`，并更新"所有权表"与"不自作主张"那一段
- [x] 版本号 `1.0.0 → 1.1.0` **三处同步**：`plugin.cfg`、`addon.manifest.json`、`README.md` 标题
- [x] `Harness.cs`：更新提交体断言，补新字段的序列化 / 归一化 / 解析用例
- [x] `tests/verify.py` 五个阶段通过，尤其**阶段 2**（核心文件不得出现 `Godot`）与**阶段 3**（纯 .NET 干净宿主，0 warning）
- [x] `tests/godot-feedback-host` 的 `sync_addon.py` 漂移检查通过
- [x] 更新 `addons/gd_feedback/README.md` 的公开 API、字典键表、环境采集小节与所有权表

Parent spec: `.scratch/env-info-playtime-admin-ui/spec.md`

## 实施记录

### 验证证据（实跑）

```text
python tests/godot-feedback-host/tools/sync_addon.py
GD_FEEDBACK_HOST_SYNC PASS (installed 12 shipped files at addons/gd_feedback/)

python addons/gd_feedback/tests/verify.py
GD_FEEDBACK_IDENTITY PASS
GD_FEEDBACK_MANIFEST PASS
GD_FEEDBACK_ENGINE_FREE PASS
HARNESS SUMMARY checks=109 failed=0
GD_FEEDBACK_ENGINE_FREE_TESTS PASS
GD_FEEDBACK_GODOT_HOST PASS
GD_FEEDBACK_GODOT_PROBE SKIP (pass --godot-path <godot-mono>)
GD_FEEDBACK_VERIFY PASS
```

基线是 `checks=98`（本次新增 11 项检查）。阶段 4 用 `TreatWarningsAsErrors=true` 在 Debug 与 Release 下都构建了 Godot 宿主，这同时证明了 `OS.GetVersionAlias()`、`OS.GetDistributionName()`、`RenderingServer.GetVideoAdapterName()`、`OS.GetMemoryInfo()` 在 Godot 4.7.2 的 C# 绑定里都存在且零警告。

### 新增的 11 项检查

- `submit sends the bearer token and camelCase type` 扩展：断言 `"cpu":"…"` 与 `"memoryTotalMb":16384` 真的进了请求体（camelCase + 数字类型）。
- 新增 `out-of-range auto-collected fields are dropped, not fatal`：CPU 超长、内存越界时 **提交仍然成功**，且请求体里**不含**这两个键。
- 新增 `environment and playtime fields are parsed`：列表响应里的 `cpu` / `memoryTotalMb` / `playtimeMinutes` 都能解析出来。
- `detail deserializes comments` 扩展：详情响应同样解析 `playtimeMinutes` 与 `cpu`。

### 关键实现说明

- **归一化放在 `PlayerFeedbackValidation.NormalizeCpu` / `NormalizeMemoryTotalMb`，刻意不放进 `Validate`**：这两个字段玩家既没有输入、也无法修正，越界必须是"丢弃"而不是"提交失败"，规则与服务端完全一致（服务端也是丢弃而不是 400）。
- **`gpu` 走 `RenderingServer`**：Godot 4.x 的 `OS` 上根本没有"显卡名"API，只有 `OS.get_video_adapter_driver_info()`——它返回的是驱动名 + 版本，而且文档明确警告首次调用可能耗时数秒，会让提交凭空多等几秒。
- **没有开关**：`FirstNonEmpty` 会把空字符串当成"没给"，所以宿主也**无法**通过传空值关掉采集。这是刻意取舍（ADR-0006），已写进 README 而不是留一个没人用的配置项。
- **实验台与 README 示例都改成不再手写环境字段**，否则新采集到的更准确的 OS / GPU 会被宿主的旧值覆盖（因为"宿主优先"）。

### 过程说明

本 issue 最初交给一个后台 subagent，它在很长时间内零文件改动（很可能卡在本机无出网、`dotnet restore` 拉不动包这类环境问题上）。我把它中断后自己接手完成——`verify.py` 自带构建（走本地 NuGet 缓存），不需要联网。
