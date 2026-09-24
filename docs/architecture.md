# Architecture & Deployment

## Application shape

Prefer one ASP.NET Core application:

```text
GameFeedback/
├── Api/
│   ├── AuthEndpoints.cs
│   ├── FeedbackEndpoints.cs
│   ├── CommentEndpoints.cs
│   ├── GameResolution.cs
│   └── ApiProblems.cs
├── Components/
│   └── Pages/
│       └── Admin/
├── Contracts/
│   ├── Requests/
│   └── Responses/
├── Data/
│   ├── AppDbContext.cs
│   ├── Configurations/
│   └── Migrations/
├── Domain/
│   ├── Game.cs
│   ├── SteamCredential.cs
│   ├── Player.cs
│   ├── Feedback.cs
│   ├── FeedbackComment.cs
│   └── FeedbackStatus.cs
├── Services/
│   ├── SteamAuthService.cs
│   ├── SteamPlaytimeService.cs
│   ├── SteamCredentialService.cs
│   ├── ApiKeyProtector.cs
│   ├── TokenService.cs
│   ├── GameResolver.cs
│   ├── ResolvedGame.cs
│   ├── GameValidation.cs
│   ├── PlayerService.cs
│   ├── FeedbackService.cs
│   ├── GameAdminService.cs
│   ├── AdminFeedbackService.cs
│   └── AdminSeeder.cs
├── Program.cs
└── appsettings.json
```

## Module boundaries

- `Api`: HTTP endpoint definitions. The player endpoints are mapped into one route group, `app.MapGroup("/g/{appId}")`, whose only added filter is `ResolveGameFilter`.
- `Contracts`: public request/response DTOs. Admin-facing view records for Games and credentials live next to the services that produce them (`GameAdminView`, `CredentialAdminView`) so a credential ciphertext never reaches a DTO.
- `Domain`: domain and persisted models.
- `Services`: application/integration logic.
- `Data`: EF Core/database code.
- `Components`: Blazor admin UI.

Do not put substantial business logic directly in Minimal API handlers or Razor components.

### Game resolution

`Api/GameResolution.cs` holds the two endpoint filters that make the Game a server-side fact:

- `ResolveGameFilter` turns the route's `{appId}` into a Game through `GameResolver`: `GameResolver` normalizes the segment (`GameValidation.NormalizeAppId`) and refuses anything that is not all digits *before* querying, so a non-numeric segment and an AppID no Game carries both answer `404 game_not_found`; disabled → `403 game_disabled`. It does **not** reject an unconfigured Game: a Game with no `SteamAppId` is never resolved in the first place (there is no path to it), and a Game with an AppID but no credential is the login endpoint's business (`401 steam_unavailable`) — the Development debug login is allowed to work on a Game with no credential. The resolved Game is stashed on `HttpContext` (`ApiProblems.GameContextExtensions`) so every handler in the request reads the same one.
- `GameTokenFilter` compares the token's `game` claim with the resolved Game and returns `401 game_mismatch` when they disagree — the enforcement point for "an access token is bound to one Game".

`ApiProblems.cs` owns the stable codes: `game_required`, `game_not_found`, `game_disabled`, `game_mismatch`, `credential_unreadable`, `steam_ticket_rejected`, `steam_unavailable`. The root `/api` and `/api/{**rest}` routes are mapped explicitly to `game_required` so a stale client gets the documented `404` rather than a framework default.

`GameResolver` does a per-request database read with no in-process cache: credential and activation changes must take effect immediately. `ResolvedGame` carries the Game plus its decrypted key — deliberately a class with a `ToString` that omits the key, because a record's generated `ToString` would print the publisher key the first time anyone logs the object. `GameValidation` owns the AppID/name/identity rules and the default identity `feedback-api`; there is no slug rule, because there is no slug.

`SteamAuthService` and `SteamPlaytimeService` no longer read Steam configuration from options: every call takes what it needs from the resolved Game (`SteamAppId`, `Identity`, decrypted key). `SteamOptions` keeps only `DebugSkipTicketValidation`. `ApiKeyProtector` wraps `IDataProtector` so encryption never leaks into domain or UI code.

`GameAdminService` owns admin CRUD for Games, the AppID uniqueness check, the audit log lines, and the delete guard (a Game with any Feedback or Player is not deletable). `SteamCredentialService` owns credential CRUD, the delete guard for referenced credentials, and the read-only verify probe against Steam. `GameAdminService.HasAnyAsync` is the single answer to "does this instance have a Game yet?", used by the first-run guard.

`SteamPlaytimeService` remains best-effort by design (ADR-0006) and the endpoint still passes its result into `FeedbackService.CreateAsync`, so the persistence service keeps depending only on `AppDbContext`.

## Database

Use EF Core + Npgsql. Migrations for all persisted schema changes. Migrations apply automatically on application startup (`Database__AutoMigrate`, default on). Store timestamps in UTC.

Every Feedback and every Player belongs to exactly one Game; Steam configuration is data, not configuration (ADR-0007, ADR-0008).

Minimum useful indexes:

```text
games.SteamAppId UNIQUE (nullable; the addressing key)
games.CredentialId
players (GameId, SteamId) UNIQUE
feedbacks (GameId, CreatedAt)
feedbacks (GameId, Status)
feedbacks (GameId, GameVersion)
FeedbackComment.FeedbackId
```

The previous global `IX_players_SteamId` unique index is replaced by the composite `IX_players_GameId_SteamId`; the single-column `feedbacks.GameVersion` index is replaced by the game-scoped `(GameId, GameVersion)`, because every admin list query is scoped to a Game before it filters or sorts.

Foreign keys that protect history are `Restrict`, never cascade: `feedbacks.GameId → games.Id` (deleting a Game must never delete player Feedback), `players.GameId → games.Id` (the delete guard and the database agree), and `games.CredentialId → steam_credentials.Id` (deleting a key must never silently orphan a live Game).

The repository has five migrations, in order:

```text
20260907112342_InitialCreate
20260907121222_AddIdentitySchema
20260924072540_AddFeedbackEnvInfoAndPlaytime
20260924114358_MultiGameSupport              creates games / steam_credentials, adds the GameId columns
20260924124609_AddressGamesBySteamAppId      drops games.Slug: the Steam AppID is the addressing key
```

`MultiGameSupport` is hand-written rather than scaffolded verbatim: EF generates a `defaultValue: 0` for a new required column, and `0` is not a Game, so the foreign key would fail. The order is *create tables → insert the placeholder Game if there is anything to backfill → add nullable columns → backfill → tighten to NOT NULL → add the foreign keys*.

`AddressGamesBySteamAppId` is deliberately a **separate** migration rather than a folded-in edit of `MultiGameSupport`: a database that already applied migration 4 must keep working, and an applied migration must never be redefined. It drops the `Slug` column and `IX_games_Slug`; its `Down` re-adds `Slug` with synthesized unique placeholders (`'game-' || "Id"`), which is enough to rebuild the index but not to restore history.

The placeholder Game is inserted **only when the database already holds Players or Feedback** (`WHERE EXISTS (SELECT 1 FROM players) OR EXISTS (SELECT 1 FROM feedbacks)`); it is `Name = '默认游戏'`, with no AppID and no credential. On an upgrade it owns the pre-existing rows, and the Admin fills in its AppID and credential — until then it has no path at all and no client can reach it. On a **fresh database the insert is skipped**, so `games` stays empty and the admin UI's first-run guard sends the Admin to the add-game page. An unconditional insert would silently break that guard on every new install.

## Configuration

Configuration holds what is deployment-wide; the database holds what is per-Game.

```text
ConnectionStrings__DefaultConnection
Jwt__Issuer
Jwt__Audience
Jwt__SigningKey
Admin__SeedEmail
Admin__SeedPassword
DataProtection__KeysPath          the Data Protection key ring (defaults to <content root>/keys)
Steam__DebugSkipTicketValidation  development-only; nothing else lives under Steam:
Database__AutoMigrate
ReverseProxy__KnownProxies__0
```

`Steam__ApiKey`, `Steam__AppId` and `Steam__Identity` are gone, and no replacement for them is introduced: a Game's `SteamAppId` / `steam_identity` are columns on `games`, and API keys live encrypted in `steam_credentials` and are entered in the admin UI (ADR-0008). Startup therefore validates nothing about Steam credentials; the first Game and its credential are provisioned by an Admin, and until a Game exists there is no `/g/{appId}` path for any client to call at all.

`DataProtection:KeysPath` is not optional in production. `Program.cs` falls back to `<content root>/keys`, which is inside the image layer and disappears on rebuild — mount a persistent volume and put it on the backup list.

## Deployment

Target deployment:

```text
docker compose
├── app
└── postgres

volumes
├── postgres data
└── keys            mounted into app at DataProtection__KeysPath
```

- Keep PostgreSQL internal to the Docker network; the `postgres:17` image is exposed to localhost only for development.
- The ASP.NET container listens on a predictable port such as `8080`.
- The public edge may be Cloudflare Tunnel, Nginx, or Caddy. Every Game's client reaches the same host; only the `/g/{appId}` prefix differs.
- Support forwarded headers correctly when behind a trusted reverse proxy.
- Trust loopback proxies by default. Configure additional proxy source IPs through `ReverseProxy__KnownProxies__0` (then `__1`, etc.); Docker Compose maps `REVERSE_PROXY_IP` to the first entry. Use the source IP seen by the application, which may be a Docker bridge gateway. Forwarded headers from other sources are ignored; do not clear the framework trust lists.
- Mount the Data Protection key ring on a persistent volume and include it in backups. Losing it does not lose Feedback, but it makes every stored Steam credential undecryptable (the affected Games answer `401 credential_unreadable`) and forces the keys to be re-entered.
- First-run provisioning is an admin-UI step now: start the stack, sign in at `/admin`, get redirected to the add-game page, enter the AppID and the credential. Until a Game exists (or while the only Game has no AppID) there is no path for a client to reach, so nothing answers `steam_unavailable` — a Game with an AppID but no credential is the case that does.
- Do not add Redis, object storage, workers, or other containers until a feature actually requires them.
