# Player API Spec (v1)

Keep v1 small.

## Addressing: every route carries a Game

The player API exists **only** under `/g/{appId}/api/...`, where `{appId}` is a Game's Steam AppID — digits only, at most 10 characters, e.g. `1910980` (the implementation validates it with `GameValidation.ValidateSteamAppId`). There is no root player API and no default Game.

```text
POST /g/{appId}/api/auth/steam
POST /g/{appId}/api/feedback
GET  /g/{appId}/api/feedback/mine
GET  /g/{appId}/api/feedback/{id}
POST /g/{appId}/api/feedback/{id}/comments
```

`/health` and the admin UI (`/admin`) stay at the root.

A path segment that is not all digits resolves to no Game at all (`GameResolver` refuses it before querying) and answers `404 game_not_found`, exactly like an AppID no Game carries.

## Client integration

The base URL **no longer carries a Game identifier**. `FeedbackConfig.BaseUrl` is just the feedback service address (`https://feedback.example.com`); the addon appends `/g/{appId}` itself (addon version 1.3.0).

The AppID comes from the host, through the optional interface `IGameAppIdProvider` (`addons/gd_feedback/FeedbackAbstractions.cs`), whose null-object default is `UnavailableGameAppIdProvider`. The host returns the AppID the game is running as — the same value it already passes to `SteamClient.Init`. The addon does not read Steam itself: ADR-0004 keeps its core engine-free and dependency-free, which is why the value must be injected.

- Host implements the provider and returns a numeric AppID (digits only, at most 10 characters) → the addon appends `/g/{appId}` to `BaseUrl`.
- Host does not implement it, or returns `null`/blank → the addon uses `BaseUrl` verbatim, so a base URL that already contains a path keeps working unchanged.
- Host returns something that is not a numeric AppID (digits only, at most 10 characters — a slug, by mistake) → the addon fails closed with its own `invalid_configuration` and sends **no** request.

The AppID is a client-supplied *address*, not a client-supplied *identity*: the server resolves it against `games.SteamAppId` and decides everything else from its own database. A client-supplied `gameId` or AppID in a request body is still ignored.

The removed root paths (`/api/auth/steam`, `/api/feedback`, `/api/feedback/mine`, `/api/feedback/{id}`, `/api/feedback/{id}/comments`) answer:

```text
404  code = "game_required"   the player API lives only under /g/{appId}/api/..., where {appId} is the Game's Steam AppID
```

This is permanent, not a deprecation window. A stale shipped build fails loudly here instead of writing into the wrong Game.

## Error codes

All player-API failures use `ProblemDetails` with a stable extension member `code`:

```text
game_required           404  root player path used; the API is under /g/{appId}/api/..., where {appId} is the Game's Steam AppID
game_not_found          404  no Game has this Steam AppID (also what a non-numeric path segment resolves to)
game_disabled           403  the Game exists but is disabled
game_mismatch           401  the access token was issued for another Game
credential_unreadable   401  the Game's credential exists but cannot be decrypted (Data Protection key ring trouble)
steam_unavailable       401  the Game's credential is not configured, or Steam could not be consulted
steam_ticket_rejected   401  Steam rejected the ticket for this Game (invalid, expired, or appid/identity mismatch)
```

A malformed or missing ticket still answers `400`.

`credential_unreadable` exists so that a broken key ring does not look like a Steam outage. Clients treat it exactly like `steam_unavailable` (retryable, not "your ticket is bad"); it is the server-side diagnosis that differs.

`game_not_found`'s `detail` names the requested AppID and points at the admin UI; it deliberately does **not** list the configured Games. The client-side counterpart is `invalid_configuration`: the addon catches a mis-derived path segment before any request leaves the machine, so "the identifier was wrong" is a local configuration error rather than a server `404` that has to be debugged.

A path that resolves to a Game other than the one named in the caller's token answers `401 game_mismatch`.

## Steam login

```http
POST /g/{appId}/api/auth/steam
```

Input:

```json
{
  "ticket": "..."
}
```

The server resolves the Game from the AppID first, then verifies the ticket with Steam using **that Game's** `SteamAppId`, identity, and credential. Return a local access token plus only client-required player data.

A Game whose credential is not configured answers `401 steam_unavailable` here — fail closed, and indistinguishable to the Player from a Steam outage. (A Game with no `SteamAppId` is not reachable at all, so it never gets this far; the missing-credential half is the only reachable configuration failure at this endpoint.) This is why a fresh deployment serves no logins until an Admin provisions the first Game in `/admin`. In Development, `Steam:DebugSkipTicketValidation` can still log a Player in on a Game with no credential, since that path never talks to Steam.

The access token is an implementation detail of the server, but its contract is worth stating: it carries `sub = SteamID64` and a `game` claim naming the Game it was minted for. A token is only accepted on paths resolving to the same Game, so a token minted for one Game cannot be used against another.

Clients must treat `steam_ticket_rejected` as "this ticket is not usable" and `steam_unavailable` as retryable: the ticket itself may be fine, and only the server's ability to ask Steam (or its configuration for this Game) failed.

The Feedback Client self-heals across the upgrade: on a `401` from a non-login endpoint it clears its cached token, re-authenticates with a fresh Steam Ticket, and retries exactly once. Tokens issued before the upgrade carry no `game` claim and are rejected, so a player who was already logged in simply re-authenticates without noticing.

## Create feedback

```http
POST /g/{appId}/api/feedback
Authorization: Bearer <jwt>
```

Input (metadata fields are client-supplied and optional; the server only validates lengths):

```json
{
  "type": "Bug",
  "title": "...",
  "content": "...",
  "gameVersion": "1.2.3",
  "buildNumber": "123",
  "operatingSystem": "Windows 11",
  "gpu": "RTX 4070",
  "cpu": "Intel(R) Core(TM) i7-6700K CPU @ 4.00GHz",
  "memoryTotalMb": 16384,
  "locale": "zh-CN",
  "map": "arena_01",
  "character": "mage"
}
```

The SteamID comes from authentication, never the request body. The Game comes from the resolved path segment (its Steam AppID), never the request body — there is no request field for it, and a client-supplied AppID or `gameId` is ignored.

`cpu` and `memoryTotalMb` are normally collected automatically by the Feedback Client. They are **advisory context**: the server bounds them, and an out-of-range value is **discarded rather than rejected** — the Player neither typed them nor can fix them, so a `400` would only cost them the Feedback they wrote. Every other metadata field keeps the "too long → `400`" behaviour.

Every Feedback response (`POST` result, `/mine`, `/{id}`) additionally carries three read-only fields:

```text
cpu              string?   echoed back from the request (or null when discarded)
memoryTotalMb    int?      echoed back from the request (or null when discarded)
playtimeMinutes  int?      the Player's accumulated minutes in this Game
```

`playtimeMinutes` is **filled in by the server**: it is looked up from Steam at submission time using the resolved Game's AppId and credential, and snapshotted onto the Feedback. A client-supplied `playtimeMinutes` is ignored — do not send it, and do not treat it as an input field. It is `null` whenever Steam cannot supply it (private game details, an app not owned in the retail sense, a debug-login SteamID, or a Steam outage); `0` is a real value meaning "owns it, never played".

## List own feedback

```http
GET /g/{appId}/api/feedback/mine
Authorization: Bearer <jwt>
```

Returns the most recent 100 items, newest first, **within that Game only**. No pagination parameters in v1.

## Read own feedback

```http
GET /g/{appId}/api/feedback/{id}
Authorization: Bearer <jwt>
```

Prefer `404` when the requested feedback does not belong to the current player — and, equally, when it belongs to a Player of a different Game.

## Add player comment

```http
POST /g/{appId}/api/feedback/{id}/comments
Authorization: Bearer <jwt>
```

Only the feedback owner may add a player comment.

Do not expose admin operations through player endpoints.

## Validation limits

`type` accepts only the names `Bug`, `Suggestion`, and `Other` (case-insensitive). Numeric enum strings and comma-separated combinations are rejected with 400.

```text
Ticket:       1–8192 chars (hex; Steam's web API ticket is up to 2560 bytes → 5120 hex chars)
AppId:        1–10 digits, not all zeros (the path segment /g/{appId});
              resolved by the server, never accepted as request input
Title:        1–200 chars
Content:      1–10,000 chars
Comment:      1–5,000 chars
GameVersion:  <= 64 chars
BuildNumber:  <= 64 chars
Cpu:          <= 120 chars (auto-collected; over-long is discarded, not rejected)
MemoryTotalMb: 1 .. 4,194,304 (auto-collected; out of range is discarded, not rejected)
Metadata:     bounded to sensible lengths
```

## Rate limiting

The API is internet-facing. Use ASP.NET Core rate limiting. At minimum protect:

```text
POST /g/{appId}/api/auth/steam
POST /g/{appId}/api/feedback
POST /g/{appId}/api/feedback/{id}/comments
```

Prefer IP-based protection before authentication and per-Player limits after authentication; after authentication the partition key is the pair **(Game, SteamID)**, so the same Steam account in two Games has two independent budgets.

Initial limits (fixed window, configurable):

```text
POST /g/{appId}/api/auth/steam             10 / minute  / IP
POST /g/{appId}/api/feedback               5  / 10 min  / (Game, Player)
POST /g/{appId}/api/feedback/{id}/comments 20 / 10 min  / (Game, Player)
```
