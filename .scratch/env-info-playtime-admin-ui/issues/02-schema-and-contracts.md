# 02: 数据模型、DTO 与迁移 — CPU / 内存 / 游玩时长

**What to build:** `Feedback` 增加 `Cpu`、`MemoryTotalMb`、`PlaytimeMinutes` 三列（全部可空），玩家 API 的请求与响应契约同步扩展，并生成迁移。**不改账号模型**（`players` 表已满足需求）。

**Blocked by:** 无。

**Status:** resolved

- [x] `Domain/Feedback.cs` 增加 `public string? Cpu`、`public int? MemoryTotalMb`、`public int? PlaytimeMinutes`
- [x] `Data/Configurations/FeedbackConfiguration.cs` 增加 `Cpu` 的 `HasMaxLength(120)`；**不建索引**
- [x] `Contracts/Requests/CreateFeedbackRequest.cs` 增加 `string? Cpu` 与 `int? MemoryTotalMb`，并补 `[Description]`（`OpenApiEndpointTests` 断言描述存在）
- [x] **不**在请求 DTO 里加 `playtimeMinutes`——它是服务端派生字段
- [x] `Contracts/Responses/FeedbackDto.cs` 与 `FeedbackDetailDto.cs` 都增加 `Cpu`、`MemoryTotalMb`、`PlaytimeMinutes`
- [x] `FeedbackService.CreateAsync` 映射 `Cpu` / `MemoryTotalMb`；`ToDto` / `ToDetailDto` 输出三个字段
- [x] 校验通则落地：`Cpu` 超长（>120）与 `MemoryTotalMb` 越界（不在 `0 < x ≤ 4,194,304`）一律**存 `null` + Warning 日志，绝不 400**；现有手填元数据的"超长 → 400"行为**保持不变**
- [x] 生成迁移 `AddFeedbackEnvInfoAndPlaytime`（最新为 `20260907121222_AddIdentitySchema`），**检查生成的迁移**确认只有三列、无索引、无意外改动；开发环境应用
- [x] 不回填历史行（历史数据无从获取）
- [x] `DomainPersistenceTests` 补三列往返
- [x] `dotnet build` + `dotnet test` 通过

Parent spec: `.scratch/env-info-playtime-admin-ui/spec.md`

## 实施记录

- **迁移**：`20260924072540_AddFeedbackEnvInfoAndPlaytime`。生成后逐行核对：仅三个可空列（`Cpu` `character varying(120)`、`MemoryTotalMb` `integer`、`PlaytimeMinutes` `integer`），`Down` 对称删除，**无索引、无其它改动**；`AppDbContextModelSnapshot` 只多三处 `Property`（+10 行）。已 `dotnet ef database update` 应用到开发库（`localhost:5432/gamefeedback`），应用后 `migrations list` 不再有 `Pending`。
- **踩到的坑（记下来）**：`dotnet ef database update --no-build` 会报 `PendingModelChangesWarning`——因为迁移与快照是**编译进程序集**的，用生成迁移之前的程序集就会看到"模型有未落盘变更"。必须先重新编译再执行。
- **校验通则**：`Validate` 刻意**不**校验 `Cpu`/`MemoryTotalMb`（代码注释写明理由）；`CreateAsync` 里由 `NormalizeCpu` / `NormalizeMemoryTotalMb` 越界即丢弃并记 Warning。
- **OpenAPI 示例**：`Program.cs` 的 `FeedbackRequestExample()` 补 `cpu` / `memoryTotalMb`，Swagger "Try it out" 预填不再缺字段。
- **新增测试**：
  - `FeedbackEndpointTests.Create_stores_auto_collected_environment_fields`（创建 + 详情都回传）
  - `FeedbackEndpointTests.Create_drops_overlong_cpu_without_rejecting`（121 字符 → 201 且为 null）
  - `FeedbackEndpointTests.Create_drops_out_of_range_memory_without_rejecting`（0 / −1 / 4194305 → 201 且为 null）
  - `DomainPersistenceTests.Environment_and_playtime_fields_round_trip`
- **验证**：`dotnet build` 0 错误；`dotnet test` **88/88 通过**（真实 PostgreSQL Testcontainers + Steam HTTP 桩）。
- **顺带修复的既有缺陷（与本 issue 无关）**：`SteamLoginTests.Missing_or_oversized_ticket_returns_400` 里的字面量是 `5000`，而 `MaxTicketLength` 已在 `a84f8c1` 从 4096 提到 8192——5000 落进了接受区间，于是请求真的打到 Steam，桩没排队响应就抛异常，测试必然拿到 500。已改为 `8193` 并加注释说明原因。**这不是本次功能引入的失败**：`git show HEAD:src/GameFeedback/Api/AuthEndpoints.cs` 证实 HEAD 已是 8192，而该测试文件本次未被改动。
- **既有告警（非本次引入）**：`Program.cs:300/302` 的 `CS8602`/`CS8604` 在 HEAD 上同样存在（我的示例字段插入只让行号偏移了 2 行）。
