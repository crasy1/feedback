# Player API Spec (v1)

Keep v1 small.

## Steam login

```http
POST /api/auth/steam
```

Input:

```json
{
  "ticket": "..."
}
```

Return a local access token plus only client-required player data.

A failed login answers `401` with `ProblemDetails` and a stable extension member `code`:

```text
steam_ticket_rejected   Steam rejected the ticket (invalid, expired, or appid/identity mismatch)
steam_unavailable       Steam could not be consulted (network, timeout, or server-side Steam configuration)
```

Clients must treat the first as "this ticket is not usable" and the second as retryable: the ticket itself may be fine, and only the server's ability to ask Steam failed. A malformed or missing ticket answers `400`.

## Create feedback

```http
POST /api/feedback
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

The SteamID comes from authentication, never the request body.

`cpu` and `memoryTotalMb` are normally collected automatically by the Feedback Client. They are **advisory context**: the server bounds them, and an out-of-range value is **discarded rather than rejected** — the Player neither typed them nor can fix them, so a `400` would only cost them the Feedback they wrote. Every other metadata field keeps the "too long → `400`" behaviour.

Every Feedback response (`POST` result, `/mine`, `/{id}`) additionally carries three read-only fields:

```text
cpu              string?   echoed back from the request (or null when discarded)
memoryTotalMb    int?      echoed back from the request (or null when discarded)
playtimeMinutes  int?      the Player's accumulated minutes in this game
```

`playtimeMinutes` is **filled in by the server**: it is looked up from Steam at submission time and snapshotted onto the Feedback. A client-supplied `playtimeMinutes` is ignored — do not send it, and do not treat it as an input field. It is `null` whenever Steam cannot supply it (private game details, an app not owned in the retail sense, a debug-login SteamID, or a Steam outage); `0` is a real value meaning "owns it, never played".

## List own feedback

```http
GET /api/feedback/mine
Authorization: Bearer <jwt>
```

Returns the most recent 100 items, newest first. No pagination parameters in v1.

## Read own feedback

```http
GET /api/feedback/{id}
Authorization: Bearer <jwt>
```

Prefer `404` when the requested feedback does not belong to the current player.

## Add player comment

```http
POST /api/feedback/{id}/comments
Authorization: Bearer <jwt>
```

Only the feedback owner may add a player comment.

Do not expose admin operations through player endpoints.

## Validation limits

`type` accepts only the names `Bug`, `Suggestion`, and `Other` (case-insensitive). Numeric enum strings and comma-separated combinations are rejected with 400.

```text
Ticket:       1–8192 chars (hex; Steam's web API ticket is up to 2560 bytes → 5120 hex chars)
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
POST /api/auth/steam
POST /api/feedback
POST /api/feedback/{id}/comments
```

Prefer IP-based protection before authentication and SteamID-based limits after authentication.

Initial limits (fixed window, configurable):

```text
POST /api/auth/steam               10 / minute  / IP
POST /api/feedback                 5  / 10 min  / player
POST /api/feedback/{id}/comments   20 / 10 min  / player
```
