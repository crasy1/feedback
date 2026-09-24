# 01: 实证探测 Steam `GetSingleGamePlaytime` 的响应形状

**What to build:** 确认 `IPlayerService/GetSingleGamePlaytime/v1` 的**真实响应字段名**，把解析器从"推测"变成"确证"。

**人工输入：** SteamID64 `76561198100743150`（已提供），AppId `1910980`。

**为什么不能省：** 本 spec 的失败策略是"拿不到就存 `null`"，所以字段名猜错会**静默地永远存 null**，表面上与"玩家资料私密"完全一样，不会自己暴露。

**Status:** resolved

- [x] 确认端点存在（未知方法 404 vs 本方法 403，实测）
- [x] 确认无法免 key 访问
- [x] 确认响应形状无法从 Steam 客户端 protobuf 推出
- [x] 用真实 key 跑探针，记录确切字段名
- [x] 把结果写回本 issue 的 `## Answer`

Parent spec: `.scratch/env-info-playtime-admin-ui/spec.md`

## Answer

### 实测结果

`IPlayerService/GetSingleGamePlaytime/v1/?steamid=…&appid=…` → **HTTP 200**：

```json
{
  "response": {
    "playtime_2weeks": 81,
    "playtime_forever": 2361,
    "last_playtime": 1790234027,
    "first_playtime": 1644978779,
    "playtime_windows_forever": 2310,
    "playtime_mac_forever": 51,
    "playtime_linux_forever": 0,
    "playtime_current_session": 3,
    "playtime_deck_forever": 0,
    "playtime_disconnected": 0
  }
}
```

（上面省略了各平台的 `first_*` / `last_*` 时间戳字段。）

**定稿要点**

- **字段路径 = `response.playtime_forever`**，整数，单位**分钟**（实测 2361 分钟 ≈ 39.35 小时）。与命名惯例推测一致。
- 响应是**扁平对象**：**没有 `games[]` 包装**，也**不回显 `appid`**（无法从中校验查询的是哪个 AppId）。
- 同一响应还带有（本 spec **不使用**）：`playtime_2weeks`、`playtime_current_session`、`playtime_disconnected`、`first_playtime`/`last_playtime`、以及 `playtime_{windows,mac,linux,deck}_forever` 平台拆分。
- **`0` 是合法值**（"拥有但从未玩过"）：应存 `0`。只有**字段缺失**或类型不符才存 `null`。

### 重要：`GetOwnedGames` 对照组失败 → 它**不是**可行备选

同一账号、同一时刻，`IPlayerService/GetOwnedGames/v1` 配 `appids_filter[0]=1910980` 实测返回：

```json
{ "response": { "game_count": 0 } }
```

**`GetSingleGamePlaytime` 说这个账号玩了 2361 分钟，`GetOwnedGames` 却说它拥有 0 款游戏。** 最可能的原因是该 AppId 属于尚未发行 / Playtest 的应用（开发者自测）：这类游玩时长不计入零售的"拥有游戏"列表。

**结论：原 spec 与 issue 03 里的备选方案"改用 `GetOwnedGames`，形状有 protobuf 确证、零猜测"作废**——它在真实账号上会静默返回 `null`，而且因为失败策略本身就是 null，这个错误同样不会自己暴露。

你选 `GetSingleGamePlaytime` 不只是"更窄"：**它是这两个端点里唯一可用的那个。**

### 已证实的其他事实

1. **端点存在**：未知方法 `IPlayerService/GetBogusNonexistentMethod/v1/` → 404；本方法 → 403（正文要求校验 `key=`）。403 而非 404 ⇒ 路由已解析。
2. **必须带 key**：`GetPlayerSummaries` → 400（缺 `key`）、`GetSingleGamePlaytime` → 403、`GetOwnedGames` → 401。所以带 key 的 URL 绝不能出现在工具调用或日志里，代理一侧不得发起带 key 的探测。
3. **形状无法从 protobuf 推出**：Valve 客户端的
   [steammessages_player.steamclient.proto](https://raw.githubusercontent.com/SteamDatabase/Protobufs/master/steam/steammessages_player.steamclient.proto)
   中 `service Player` 的 RPC 列表没有 `GetSingleGamePlaytime`，它是 **Web 层独有方法**。
4. **代理所在沙箱禁止出站 TLS**（连 `api.github.com` 都 TLS 握手失败，`-SkipCertificateCheck` 亦然），故探针由人本机执行。

### 传递给 issue 03 的定稿要求

- 解析路径固定 `response.playtime_forever`；**字段缺失/类型不符 → `null`**；**`0` 存 `0`**。
- `FakeSteamHandler` 的桩使用**实测形状**：扁平对象、无 `appid`、无 `games[]`。
- 兜底告警保留：响应里没有该字段时打 Warning 并记录有界长度的响应正文（用于私密资料等情形）。
