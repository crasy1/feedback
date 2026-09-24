# 05: 凭据存储与 Data Protection 静态加密

**What to build:** 让 Steam API key 以密文存在数据库里、由服务端按需解密使用：`Services/ApiKeyProtector.cs` + `SteamCredentialService` 的存储路径，钉死 Data Protection 应用名、purpose 与密钥环落盘位置。

**Blocked by:** 02。

**Status:** resolved

- [ ] `Program.cs`：`AddDataProtection().SetApplicationName("GameFeedback").PersistKeysToFileSystem(...)`，路径来自 `DataProtection:KeysPath`（缺省 `<content root>/keys`，缺省值只适合开发，生产必须挂卷）
- [ ] `Services/ApiKeyProtector.cs`：包装 `IDataProtector`，purpose 固定 `GameFeedback.SteamApiKey.v1`；`Protect(string)` / `TryUnprotect(string, int credentialId)`，解密失败返回 `null` 并记 `Error`（含 credentialId，绝不含密钥或密文）
- [ ] `Services/GameResolver.cs`：凭据解不开时置 `ResolvedGame.CredentialUnreadable = true`（**不**在此处返回错误），由登录端点报 `401 credential_unreadable`——**不能**降级成 `steam_unavailable`
- [ ] 凭据的加密/保存路径只经过 `SteamCredentialService`；领域层与 UI 层不接触 `IDataProtector`；明文只在 service、protector 与 Steam 调用之间流动
- [ ] `SteamAuthService` / `SteamPlaytimeService` 接收的是解密后的 key（由 `ResolvedGame` 携带），密文不出现在任何 DTO、页面或日志
- [ ] 凭据变更的结构化日志只含管理员 user id、凭据 id、`Name`、以及"key 是否被替换"这一布尔事实；**绝不含 key 值或密文**
- [ ] `CredentialAdminView` 之类的管理端视图**不含** key 字段、也不含密文
- [ ] 凭据加密往返测试：写入 → 新 DI scope 读回 → 解密值可用；数据库里查不到明文；`TryUnprotect` 在 key ring 不匹配时返回 `null` 且不抛
- [ ] `dotnet build` + `dotnet test` 通过

Parent spec: `.scratch/multi-game/spec.md`

## 备注

- 应用名与 purpose 都必须钉死：默认应用名派生自 content root 路径，镜像构建之间一变，所有已存凭据永久解不开；purpose 改动同理。
- 密钥环是新秘密：写进部署文档的备份清单（和数据库一起），并且不能把 dev 密钥环目录提交进仓库。
- 不要把 Data Protection 密钥环和 `Jwt__SigningKey` 混为一谈：后者仍是配置里的独立密钥。
- `EncryptedApiKey` 用 `text`，不要按"密文大概多长"卡一个 `varchar`——key ring 与 payload 变化都会影响长度。

- 应用名钉死 GameFeedback、purpose 为 GameFeedback.SteamApiKey.v1、密钥环落 DataProtection:KeysPath（compose 固定 /app/keys 并挂持久卷，已加进 .gitignore）。额外记录了一条界限：密钥环本身是明文 XML 落盘，所以这层加密防的是「只有数据库转储泄漏」，不防能读宿主文件系统的人——已写进 docs/security.md。

## Comments

- 2026-09-24 **已完成并验证**。下方的勾选框保留为**当时的计划原样**：其中涉及 slug 寻址的条目已被同日「改用 Steam AppID 寻址」的反转取代，盲勾会把没发生过的事记成发生过，所以保持未勾选；实际实现以 `.scratch/multi-game/spec.md` 与 `docs/adr/0007-multi-game-support.md` 的修订章节为准。
- 验证证据（整批一次性跑过）：`dotnet build` 0 警告 0 错误；`dotnet test` **216 通过 / 0 失败**（真实 Testcontainers postgres:17）；`python addons/gd_feedback/tests/verify.py` **124 项 0 失败**；`sync_addon.py --check` 一致；Godot 宿主工程 0 错误；`docker compose config` 通过；迁移在真实 PostgreSQL 上验过**全新装 / 生产升级（带存量数据）/ 回滚**三条路径；端到端（真实 PG + Release 构建）验过按 AppID 寻址，以及 `game_required` / `game_not_found` / `game_disabled` / `game_mismatch` / `steam_unavailable` 全部命中。