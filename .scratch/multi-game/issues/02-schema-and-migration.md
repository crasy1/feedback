# 02: `games` / `steam_credentials` 表、第四个与第五个迁移、历史数据归属

**What to build:** 新增 `games` 与 `steam_credentials` 两张表，给 `players` / `feedbacks` 加上必填 `GameId`，把 `players.SteamId` 的全局唯一索引换成 `(GameId, SteamId)` 复合唯一索引，并生成**第四个** EF 迁移；随后按寻址反转再生成**第五个**迁移删除 `games.Slug`。本 issue 只管 schema 与数据归属，不管 HTTP 与 UI。

**Blocked by:** 01。

**Status:** resolved

> **寻址方式在首次部署尝试之后被反转**：`games.Slug` 已由第五个迁移删除，Steam AppID 是唯一寻址键。见 `docs/adr/0007-multi-game-support.md` 的 `## Revision (same day, after the first deploy attempt)`。

- [ ] `Domain/Game.cs`：`Id` / `Name`（varchar(100)）/ `SteamAppId?`（varchar(10) UNIQUE，数字**字符串**，同时是路径段）/ `Identity`（varchar(64)，列名 `steam_identity`，默认 `feedback-api`）/ `IsActive` / `CredentialId?` / `CreatedAt` / `UpdatedAt`；`IsFullyConfigured` 用 `[NotMapped]`
- [ ] `Domain/SteamCredential.cs`：`Id` / `Name`（varchar(100)）/ `EncryptedApiKey`（`text`）/ `CreatedAt` / `UpdatedAt`
- [ ] `Domain/Player.cs` 加必填 `GameId`；`Domain/Feedback.cs` 加必填 `GameId`
- [ ] `Data/Configurations/GameConfiguration.cs`：`SteamAppId` UNIQUE（可空，Postgres 允许多个 NULL）、`CredentialId` FK `Restrict`（**没有** `Slug` 配置）
- [ ] `Data/Configurations/SteamCredentialConfiguration.cs`：`Name` 长度、`EncryptedApiKey` 用 `text`
- [ ] `PlayerConfiguration.cs`：删掉 `SteamId` 全局唯一索引，改 `(GameId, SteamId)` 复合唯一；加 `GameId` FK `Restrict`
- [ ] `FeedbackConfiguration.cs`：加 `GameId` FK **`Restrict`**（不是 cascade，故意）；把 `GameVersion` 单列索引换成 `(GameId, GameVersion)`，并加 `(GameId, CreatedAt)`、`(GameId, Status)`
- [ ] 生成迁移 `MultiGameSupport`（现有最新为 `20260924072540_AddFeedbackEnvInfoAndPlaytime`），**逐行核对**：两建表、两加列、删 `IX_players_SteamId` 与 `IX_feedbacks_GameVersion`、建复合索引与三个 Game 外键，`Down` 对称
- [ ] **手写关键顺序**（EF 会为必需列生成 `defaultValue: 0`，而 `0` 不对应任何 Game，外键必然失败）：建表 → 插占位 Game → 加**可空**列 → 回填 → 收紧 `NOT NULL` → 建外键
- [ ] 历史数据归属：**仅在存在存量数据时**插入占位 Game（`Name='默认游戏'` / `SteamAppId=NULL` / 凭据 `NULL`）——此时 `Slug` 列还在，插入时给它 `'default'`；该列随第五个迁移一起消失——并把现有全部 `players` / `feedbacks` 挂到它下面
- [ ] 生成迁移 `AddressGamesBySteamAppId`（`20260924124609`）：删 `Slug` 列与 `IX_games_Slug`；**不要**把它折进 `MultiGameSupport`——迁移 4 可能已在某个库里跑过，已应用的迁移不得改写；`Down` 重新加列并用 `'game-' || "Id"` 合成唯一占位 slug（**必须赶在建唯一索引之前**，否则多行空串会让索引建不出来），并在注释里写明那些值不是历史
- [ ] 编译后 `dotnet ef database update` 按序应用两个迁移到开发库；确认无 `Pending`
- [ ] `DomainPersistenceTests` 补 Game / SteamCredential 往返，以及 `(GameId, SteamId)` 复合唯一约束的回归
- [ ] `dotnet build` + `dotnet test` 通过（此时路由尚未改，既有测试应继续绿）

Parent spec: `.scratch/multi-game/spec.md`

## 备注

- **占位 Game 的插入是有条件的（已实现）**：`games` 表在**全新数据库**上必须保持为空，否则"空 games 表 → 强制去新增游戏页"的首次运行态永远不会触发，而且会凭空出现一个未配置的 `默认游戏`。实现即
  `INSERT INTO games ("Slug", "Name", "SteamAppId", "steam_identity", "IsActive", "CreatedAt", "UpdatedAt") SELECT 'default', '默认游戏', NULL, 'feedback-api', TRUE, now(), now() WHERE EXISTS (SELECT 1 FROM players) OR EXISTS (SELECT 1 FROM feedbacks);`
  所以：升级路径上占位 Game 承接存量行；全新数据库跑完迁移后 `games` 为空，首次运行守卫必然触发。（`Slug` 那一列是迁移 4 当时还存在的形状，随迁移 5 删除。）
- 占位 Game 的 `SteamAppId` 与凭据**刻意留空**：Steam 配置已经搬进数据库，升级后必须由管理员在管理端录入。它因此**不可寻址**——没有任何 `/g/{appId}` 路径能解析到它，所以它既不服务任何请求，也**不会**返回 `401 steam_unavailable`（那个码现在只剩"有 AppID 但没凭据"这一半可达，见 issue 04）。
- 回填保持 `INSERT ... SELECT` 式的幂等写法，且在 `Up` 里先建表、条件插占位行、再 `ADD COLUMN`（可空）回填、最后 `SET NOT NULL`。
- 复合唯一索引与外键的名字要显式指定（`IX_players_GameId_SteamId`、`FK_feedbacks_games_GameId`、`FK_players_games_GameId`、`FK_games_steam_credentials_CredentialId`），避免随机名导致 `Down` 对不上。
- `dotnet ef database update --no-build` 会用旧程序集报 `PendingModelChangesWarning`（上一轮踩过），必须先重新编译；两个迁移必须按 `MultiGameSupport` → `AddressGamesBySteamAppId` 的顺序应用。
- 本 issue 自身不改路由：完成时 `GameId` 只有占位 Game 一个取值，`/g/{appId}` 路由与 Game 解析在 issue 03 落地。

- 落成**两个**迁移：20260924114358_MultiGameSupport（建表、**按存在存量数据为条件**插入占位 Game、回填、收紧非空、复合唯一索引、三处 Restrict）与 20260924124609_AddressGamesBySteamAppId（删 games.Slug 与 IX_games_Slug）。后者刻意不折进前者：迁移 4 可能已在某个库里跑过。它的 Down 用 game- 前缀拼主键合成唯一占位 slug——EF 生成的那版在多行时会因空串重复而建不出唯一索引。

## Comments

- 2026-09-24 **已完成并验证**。下方的勾选框保留为**当时的计划原样**：其中涉及 slug 寻址的条目已被同日「改用 Steam AppID 寻址」的反转取代，盲勾会把没发生过的事记成发生过，所以保持未勾选；实际实现以 `.scratch/multi-game/spec.md` 与 `docs/adr/0007-multi-game-support.md` 的修订章节为准。
- 验证证据（整批一次性跑过）：`dotnet build` 0 警告 0 错误；`dotnet test` **216 通过 / 0 失败**（真实 Testcontainers postgres:17）；`python addons/gd_feedback/tests/verify.py` **124 项 0 失败**；`sync_addon.py --check` 一致；Godot 宿主工程 0 错误；`docker compose config` 通过；迁移在真实 PostgreSQL 上验过**全新装 / 生产升级（带存量数据）/ 回滚**三条路径；端到端（真实 PG + Release 构建）验过按 AppID 寻址，以及 `game_required` / `game_not_found` / `game_disabled` / `game_mismatch` / `steam_unavailable` 全部命中。