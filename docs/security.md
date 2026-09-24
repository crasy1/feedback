# Security Design

Hard invariants (never trust client SteamID, fail closed, ownership enforcement, secrets handling) live in `AGENTS.md`. This document describes the design in detail.

Related decisions: [ADR-0002](adr/0002-dual-identity-and-steam-auth.md), [ADR-0003](adr/0003-steamid-storage-and-jwt-design.md), [ADR-0007](adr/0007-multi-game-support.md), [ADR-0008](adr/0008-steam-configuration-in-the-database.md).

## Two identity systems

### Players (Steam)

Client flow:

```text
GetAuthTicketForWebApi("<Game identity>")
  ↓
POST /g/{appId}/api/auth/steam
  ↓
server resolves the Game from the Steam AppID in the path
  ↓
server calls Steam AuthenticateUserTicket with that Game's AppID + credential
  ↓
trusted SteamID64, within that Game
  ↓
server issues local JWT carrying the `game` claim
```

After authentication, normal game API calls use:

```http
Authorization: Bearer <jwt>
```

Do not call Steam again for every feedback request.

On each login the server also fetches `ISteamUser/GetPlayerSummaries` to refresh `SteamName` / `AvatarUrl`. This is best effort: a failure to fetch the profile does not fail the login; the stored values are kept (or left empty on first login).

Check the JSON type at every boundary before reading objects or strings. Syntactically valid but incorrectly shaped ticket responses are rejected with 401; incorrectly shaped profile responses use the same best-effort fallback as other profile failures.

### Client IP and proxy trust

The login rate limit is partitioned by the effective remote IP, with IPv4 and IPv4-mapped IPv6 addresses sharing a partition. Forwarded IP and scheme headers are honored only from loopback or explicitly configured `ReverseProxy:KnownProxies` addresses. Each trusted proxy must replace or correctly append the incoming forwarding headers; do not expose an unrestricted path around the trusted edge.

### Administrators

Administrators use normal web authentication:

- ASP.NET Core Identity
- secure HTTP-only cookie
- admin UI under `/admin`

On startup, if no admin account exists, one is seeded from `Admin__SeedEmail` / `Admin__SeedPassword` configuration. Existing installs are never modified; there is no registration page.

Administrators are a single global identity system with **no roles**: an Admin sees every Game. A Steam player JWT must never authorize administrator operations.

## Game resolution

Which Game a request belongs to is decided by the server, from the URL path, before any player endpoint runs.

- A Steam AppID in the path is resolved to a Game by `GameResolver`. `GameResolver` normalizes the segment (`GameValidation.NormalizeAppId`) and refuses anything that is not all digits *before* querying, so a non-numeric segment resolves to no Game; a request to a root player path (`/api/...`) is **not** mapped to any player endpoint and answers `404` with `ProblemDetails` extension member `code = "game_required"`. There is no default Game.
- The AppID used for Steam verification and for the playtime lookup comes from the resolved Game row. A client-supplied AppID in a request body is never read, never trusted, and never a request field.
- No Game has that AppID (including the non-numeric case) → `404 game_not_found`; disabled Game → `403 game_disabled`. Both are decided by `ResolveGameFilter` before any handler runs; no Game means no authentication and no data.
- A Game that has an AppID but whose credential is not configured fails closed **at the login endpoint** with `401 steam_unavailable`, and a credential that exists but cannot be decrypted fails with `401 credential_unreadable`. Both are deliberately reported with different codes, so a lost key ring is not diagnosed as Steam being down. A Game with **no** `SteamAppId` is not addressable at all — no path resolves to it — so it never reaches the login endpoint and never produces `steam_unavailable`; only the placeholder Game the upgrade migration creates is in that state. (In Development, `Steam:DebugSkipTicketValidation` may still log in on a Game with no credential, because that path never calls Steam.)
- The Game is carried into handlers on `HttpContext` by the resolution filter, not re-derived per handler, so every query in a request is scoped to the same Game the URL named.
- Ownership and existence checks are scoped by the Player's `GameId`, so a Feedback id from another Game reads as `404`, exactly like another Player's Feedback.

## Token binding to a Game

The player Access Token carries a `game` claim holding the **numeric database id** of the Game it was minted for. The id is used rather than the path segment (the Steam AppID) so that changing a Game's addressing never invalidates or re-points a live token.

- A token presented on a path whose resolved Game differs from the claim is rejected with `401 game_mismatch`. A token minted for Game A must not read Game B's Feedback even if the two Games' records were somehow confused in the URL.
- Tokens issued before this upgrade carry no `game` claim, and a missing or non-numeric claim is treated as a mismatch and rejected. Players are unaffected: the Feedback Client treats a `401` from a non-login endpoint as "my token is no longer good", clears it, re-authenticates with a fresh Steam Ticket, and retries exactly once.
- The claim is not a secret and carries no authority of its own: it only has to agree with the Game the server already resolved.

## Ticket verification

The client obtains:

```text
GetAuthTicketForWebApi("<Game identity>")
```

The server verifies it with Steam:

```text
ISteamUserAuth/AuthenticateUserTicket/v1
```

using the resolved Game's configuration:

- the Game's credential (the shared Publisher Web API Key, decrypted for the call)
- the Game's `SteamAppId`
- the ticket
- identity = the Game's `Identity` (default `feedback-api`)

A successful HTTP response alone is not enough. Validate the Steam response and require a valid SteamID64. Steam verification failures must fail closed.

The Game cannot be inferred from the response: `AuthenticateUserTicket` takes `appid` as an input and does not echo it (ADR-0007). That is why the Game is resolved from the path *before* the call, and why a player endpoint never accepts an AppID from the client.

## SteamID storage

Store SteamID64 as an opaque string, `varchar(20)`, with a **composite** unique index on `(GameId, SteamId)`. One Steam account maps to exactly one Player per Game. See [ADR-0003](adr/0003-steamid-storage-and-jwt-design.md) and [ADR-0007](adr/0007-multi-game-support.md).

## SteamAuthService

Put Steam Web API logic in a dedicated service. Responsibilities:

```text
ticket + resolved Game
  ↓
Steam Web API
  ↓
validate response
  ↓
trusted Steam authentication result
```

`SteamAuthService` must not:

- issue JWTs
- write HTTP responses
- trust caller-provided SteamID
- read the AppID or the API key from ambient configuration: both are arguments supplied by the resolved Game

Use `IHttpClientFactory`. Configure the timeout through options and the credential pair per call. Do not create a new `HttpClient` for each request.

## Steam playtime lookup

`POST /g/{appId}/api/feedback` additionally asks Steam for the authenticated Player's accumulated playtime **in the resolved Game** (`IPlayerService/GetSingleGamePlaytime/v1`) and snapshots the result onto that Feedback. Rationale and rejected alternatives: ADR-0006.

- The `steamid` sent to Steam comes from the authenticated principal; it is never taken from request data. The `appid` comes from the resolved Game row, not from the request body or the token.
- The lookup is **best effort and authoritative for nothing**: it has a 3-second budget, and any failure simply leaves the column `null`. It can never fail a submission and never returns an error to the Player.
- The response body is logged (bounded, and free of credentials) only when the expected field is missing, so a Steam-side shape change is visible rather than indistinguishable from a private profile.
- Hardware Info (`cpu`, `memoryTotalMb`) is client-supplied and treated as untrusted advisory context: length/range bounded, out-of-range values discarded rather than rejected, never used for authorization, and never sufficient to identify a Player on its own. `playtimeMinutes` is not accepted from clients at all.

## Steam credentials at rest

Steam API keys are secret material that now lives in the database (ADR-0008), and the rules below are invariants, not preferences.

- **The API key is write-only in the admin UI.** It is never rendered back into any HTML — including a form re-render after a validation failure — never returned in any DTO, never placed in a `value` attribute, a hidden field, a flash message, or a log line. The admin view records (`GameAdminView`, `CredentialAdminView`) deliberately carry no key field and not even the ciphertext.
- **`ResolvedGame` is a class, not a record, with a `ToString` that omits the key.** A record's generated `ToString` prints every property, so a single `logger.LogInformation("{Game}", game)` would write the publisher key into the log.
- **Credential-change log lines contain only the admin user id and the fact of the change.** Before/after logging of a credential row records `Name`, the "key was replaced" flag, game references and timestamps; the key value and its ciphertext are never logged.
- **Credential health is verified by a read-only probe**, never by echoing the key: `ISteamUser/GetPlayerSummaries` answers `200` for a usable key and `403`/`401` for an invalid one. The probe sends a fixed, format-valid SteamID64 constant — it only cares about the status code, never about whose profile comes back — and stores nothing. A network failure, a `5xx`, or an unparseable body is reported as "could not verify", **not** as "invalid", so Steam being down never sends an Admin off to re-enter a good key.
- **Encryption at rest uses ASP.NET Core Data Protection** with the application name pinned to `GameFeedback` (`SetApplicationName`) and a fixed purpose string (`GameFeedback.SteamApiKey.v1`). The default application name derives from the content-root path, which can change between image builds, and either the name or the purpose changing would make every stored credential permanently undecryptable.
- **A decryption failure is its own error** — `401 credential_unreadable` to the client, and an `Error` log line carrying the credential id. It must never be reported as, or mistaken for, a Steam outage; otherwise a broken key ring looks like Steam being down and nobody goes looking at the key ring.
- **The Data Protection key ring must be backed up** with the database. It is new secret material: the database dump is now sensitive, and the key ring is what converts its ciphertext back into live Steam keys.
- **State the limit of this encryption honestly.** The key ring itself is written to the mounted volume as plaintext XML — the runtime logs `No XML encryptor configured. Key … may be persisted to storage in unencrypted form.` So the encryption at rest protects against a database-only leak (a dump, a backup, a replica, or SQL injection), and it does *not* protect against an attacker who reads the host filesystem: with both the database and the key ring, the API key is recoverable. The keys volume therefore sits in the same trust boundary as the server's `.env` did before this change, and it needs the same filesystem permissions. Configure an XML encryptor (`ProtectKeysWith*`) only if that trust boundary is genuinely unacceptable — it adds another key to manage and another way to lose the whole key ring.

## JWT design

Use short-lived local player access tokens: 24-hour expiry, HS256 signed with a 256-bit key from `Jwt__SigningKey`. Recommended claims:

```text
sub  = SteamID64
game = the numeric id of the Game this token was minted for
```

Validate: signature, issuer, audience, expiration, and that `game` matches the Game resolved from the request path. Do not put other sensitive information in JWT payloads. Read signing keys from secure configuration/environment variables. Do not add refresh tokens until there is a real requirement.

## Configuration

Prefer strongly typed options:

```text
JwtOptions
SteamOptions     (DebugSkipTicketValidation only)
```

Expected configuration includes values equivalent to:

```text
ConnectionStrings__DefaultConnection
Jwt__Issuer
Jwt__Audience
Jwt__SigningKey
Admin__SeedEmail
Admin__SeedPassword
DataProtection__KeysPath
Steam__DebugSkipTicketValidation
Database__AutoMigrate
ReverseProxy__KnownProxies__0
```

**No Steam credential is configuration any more.** `Steam__ApiKey`, `Steam__AppId` and `Steam__Identity` are removed from `appsettings*.json`, `docker-compose.yml`, `.env.example`, `scripts/run_local.py`, and the README, and nothing replaces them: a Game's `SteamAppId` / `Identity` are columns on `games`, and the API key is encrypted in `steam_credentials` and entered in the admin UI. Startup therefore does not validate Steam credentials, and there is no seeding or import from environment variables — the database is the single source of truth.

The one surviving Steam switch is `Steam__DebugSkipTicketValidation`, which skips all ticket verification. It is restricted to `ASPNETCORE_ENVIRONMENT=Development` and the application refuses to start with it set anywhere else. It stays a configuration switch precisely because it is too dangerous to expose in a UI protected only by an admin password.

Use safe placeholders in repository files. Provide `.env.example` when useful. Never put real production secrets into tracked configuration.

## Secrets

Never expose or commit:

- Steam Publisher Web API Key
- JWT signing key
- database password
- admin password

Back up, and treat as secret:

- the Data Protection key ring (`DataProtection__KeysPath`)
- any database dump, which now contains encrypted API keys

Never log:

- Steam auth tickets
- JWTs
- Steam API keys, or any ciphertext that decrypts to one
- passwords
- Authorization headers
