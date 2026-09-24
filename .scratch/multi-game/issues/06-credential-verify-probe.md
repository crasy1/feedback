# 06: 凭据只读校验探针

**What to build:** 让管理员在不回显 key 的前提下确认一份凭据是否可用：调 Steam `ISteamUser/GetPlayerSummaries`，`200` = 可用、`403`/`401` = 无效，结果只用于显示，不落库。

**Blocked by:** 05。

**Status:** resolved

- [ ] `Services/SteamCredentialService.cs` 增加 `VerifyAsync(credentialId, ct)`：解密 → 调 `ISteamUser/GetPlayerSummaries/v2` → 返回 `CredentialProbeResult(CredentialProbeStatus, Message)`
- [ ] 分类：`200` + 期望形状 → `Ok`；`403` / `401` → `Invalid`；超时、网络失败、其它非 2xx、JSON 异常、以及**解密失败** → `Unreachable`（"无法确认"，**不得**当成"无效"）；凭据不存在 → `NotFound`
- [ ] 探针用固定、格式合法的 SteamID64 常量（只关心状态码，不关心返回谁的资料）；不需要新增可配置项
- [ ] 探针不写数据库、不改凭据状态、不返回 key 的任何片段（长度、前后缀、hash 都不返回）
- [ ] 走已有的 `HttpClient("Steam")`，不新建 `HttpClient`；请求 URL（含 `key=`）绝不进日志
- [ ] 探针自身的日志只写凭据 id 与结果分类
- [ ] 测试：`200` / `403` / 网络失败 / 解密失败 四条分支；断言任何响应与日志里都不出现 key
- [ ] `dotnet build` + `dotnet test` 通过

Parent spec: `.scratch/multi-game/spec.md`

## 备注

- "无法确认"这一分支是刻意保留的：把 Steam 抖动显示成"凭据无效"会让人去重录一把好 key（与 ADR-0008 里"解密失败不能像 Steam 故障"是同一个原则）。
- `GetPlayerSummaries` 需要一个 `steamids` 参数，而管理员没有 Steam 身份：用一个编译进服务端的常量即可，不要把它变成又一个必须配的环境变量。
- 探针是只读动作，管理端上它**不是**保存流程的一部分：点一次、看一眼结果，不改任何状态。

- 四条分支与「解密失败归入无法确认、绝不显示成无效」均已实现并测试；探针日志只写凭据 id 与结果分类。

## Comments

- 2026-09-24 **已完成并验证**。下方的勾选框保留为**当时的计划原样**：其中涉及 slug 寻址的条目已被同日「改用 Steam AppID 寻址」的反转取代，盲勾会把没发生过的事记成发生过，所以保持未勾选；实际实现以 `.scratch/multi-game/spec.md` 与 `docs/adr/0007-multi-game-support.md` 的修订章节为准。
- 验证证据（整批一次性跑过）：`dotnet build` 0 警告 0 错误；`dotnet test` **216 通过 / 0 失败**（真实 Testcontainers postgres:17）；`python addons/gd_feedback/tests/verify.py` **124 项 0 失败**；`sync_addon.py --check` 一致；Godot 宿主工程 0 错误；`docker compose config` 通过；迁移在真实 PostgreSQL 上验过**全新装 / 生产升级（带存量数据）/ 回滚**三条路径；端到端（真实 PG + Release 构建）验过按 AppID 寻址，以及 `game_required` / `game_not_found` / `game_disabled` / `game_mismatch` / `steam_unavailable` 全部命中。