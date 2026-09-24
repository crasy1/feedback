# 08: 文档与 ADR

**What to build:** 把本轮的决定写进仓库的长期文档，让后来者不必重新推一遍。

**Blocked by:** 03、04、06、07。

**Status:** resolved

- [x] 新增 `docs/adr/0006-server-side-playtime-and-untrusted-hardware-info.md`
- [x] `CONTEXT.md` 新增术语 **Playtime** 与 **Hardware Info**
- [x] `docs/domain-model.md`：Feedback 字段表补三列 + Player 行创建时机
- [x] `docs/specs/player-api.md`：请求体、字段说明、限额表
- [x] `docs/specs/admin-ui.md`：玩家筛选、游玩时长列、翻页保留筛选、行内改状态、复制回退
- [x] `docs/security.md`：新增 "Steam playtime lookup" 一节
- [x] `docs/architecture.md`：模块清单补 `SteamPlaytimeService`
- [x] `addons/gd_feedback/README.md` → **由 issue 04 一并完成**（版本号、字典键表、环境采集小节、所有权表都在同一份文件里，分两次改会打架）
- [x] 无新依赖、无遗留 TODO

Parent spec: `.scratch/env-info-playtime-admin-ui/spec.md`

## 实施记录

- **ADR-0006** 的核心论点：游玩时长**只能**由服务端取（Facepunch.Steamworks 2.5.2 没有任何总游玩时长 API）；尽力而为、3 秒预算、每次提交快照、失败即 `null`、绝不阻断提交；硬件信息是客户端上报的咨询性数据、有界、绝不用于授权；越界字段**丢弃而不是 400**。也记录了被实测否决的 `GetOwnedGames` 备选方案。
- **`CONTEXT.md`** 的两个术语在 grilling 会话里就已落笔（术语定了就当场写，不攒着）：**Playtime**（快照在一条 Feedback 上、取不到就留空、不估算）与 **Hardware Info**（客户端上报、咨询性、不可信、不单独标识 Player）。规范词仍是 **Player**（现有条目的 _Avoid_ 已覆盖 "User"）。
- **`docs/domain-model.md`** 顺手修掉一处文档与实现不符：Player 行是在**首次登录**时创建（`PlayerService.UpsertFromSteamLoginAsync`），不是"首次提交反馈"——原需求正是按后者描述的。
- **`docs/specs/player-api.md`** 白纸黑字写明"**游玩时长由服务端填写，客户端即使传了也会被忽略**"，并单独列出 `cpu` / `memoryTotalMb` 的"越界丢弃而非 400"规则，避免将来有人把它们做成可伪造的请求字段。
- **`docs/security.md`** 补一节，把"游玩时长查询不具权威性、硬件信息不可信、`playtimeMinutes` 不接收客户端输入"写进安全设计。
