# Domain Model

Keep the initial domain small.

## Game

One Steam application this instance serves Feedback for. Everything else in this model hangs off a Game.

```text
Id
SteamAppId      varchar(10), nullable, UNIQUE   -- the addressing key: the /g/{appId} path segment
Name            varchar(100)
Identity        varchar(64), default "feedback-api", column steam_identity
IsActive        bool, default true
CredentialId    int?, FK -> SteamCredential (Restrict)
CreatedAt
UpdatedAt
```

Rules:

- `SteamAppId` is unique, stored as digits (not a number), at most 10 characters, and is **the** addressing key: it is the `/g/{appId}` path segment of the player API. The admin UI requires it on both create and edit (`GameValidation.ValidateSteamAppId` rejects a blank value, non-digits, more than 10 characters, and all zeros). The column is nullable for exactly one reason — the placeholder Game the upgrade migration creates for pre-existing data does not know its AppID — and a Game with a `null` `SteamAppId` is **not addressable at all**: `GameResolver` refuses a non-numeric path segment before it queries, so no request can resolve to that Game. There is no `Slug` column; slugs were removed from the system (see the revision in ADR-0007).
- `Identity` is the Steam web-api ticket identity string the client passes to `GetAuthTicketForWebApi(...)` and must match the game build's own configuration.
- Configuration completeness is answered by `Game.IsFullyConfigured` (AppID and credential both present) and shown in the admin UI as `GameAdminView.IsConfigured`. A Game that has an AppID but no `CredentialId` **is** addressable and fails its logins closed with `401 steam_unavailable`, the same code a Steam outage produces. A Game with no AppID never gets that far.
- `IsActive` is the soft stop. A disabled Game stops authenticating and answers its player API with `403 game_disabled`; its Feedback and Players stay exactly where they are.
- A Game can only be deleted when it has no Feedback and no Players. The foreign keys are `Restrict` (see below), and `GameAdminService.DeleteAsync` reports `GameDeleteOutcome.HasData` so the admin UI refuses the delete and points at disabling instead. (There is no admin HTTP API: the admin UI calls the service in-process, so this is a domain outcome, not a status code.)
- **A fresh database has no Game at all**, which is what makes the admin UI's first-run guard meaningful: an authenticated Admin is forced to create the first Game before anything else works.
- **The placeholder Game is an upgrade artifact.** The `MultiGameSupport` migration creates one placeholder Game (`Name = '默认游戏'`, `SteamAppId = NULL`, no credential) to own the pre-existing Feedback and Players, because the new `GameId` columns are required and `0` is not a Game. The insert is conditional — it runs only when the database already holds Players or Feedback, so a fresh database ends up with an empty `games` table. Because its `SteamAppId` is `NULL` it is **unreachable by every client** — there is no path that resolves to it — so the Admin reaches it only through `/admin/games`, and filling in the AppID there is what makes it addressable again. (It has no slug: the column was dropped by `AddressGamesBySteamAppId`; the migration that created it once gave it `Slug = 'default'`, and that value no longer exists anywhere.)

### The two migrations that built this shape

- `20260924114358_MultiGameSupport` — creates `games` and `steam_credentials`, adds the required `GameId` columns to `players` and `feedbacks` with their `Restrict` foreign keys, replaces the global `IX_players_SteamId` unique index with the composite `IX_players_GameId_SteamId`, and inserts the conditional placeholder Game. It was hand-written in the order *create tables → insert the placeholder if there is anything to backfill → add nullable columns → backfill → tighten to `NOT NULL` → add the foreign keys*, because EF's generated `defaultValue: 0` is not a Game and the foreign key would fail.
- `20260924124609_AddressGamesBySteamAppId` — drops `games.Slug` and its unique index `IX_games_Slug`, making `games.SteamAppId` the single addressing key. It is a **separate** migration rather than an edit to the previous one precisely so that a database which already ran `MultiGameSupport` keeps working: a migration that has been applied must never be redefined. Its `Down` re-adds `Slug` with synthesized per-row placeholders (`'game-' || "Id"`), which are unique and valid but are not history — a rollback still needs each Game's real slug re-entered for any client that still sends one.

## Player

Recommended fields:

```text
Id
GameId
SteamId
SteamName
AvatarUrl
CreatedAt
UpdatedAt
LastLoginAt
```

Rules:

- `GameId` is required: a Player exists only inside a Game.
- `(GameId, SteamId)` is unique — **one Steam account maps to exactly one Player per Game**. The same Steam account playing two Games is two Players with two independent Feedback histories; the old global "one Steam account = one Player" rule is gone (ADR-0007).
- email is not required.
- a Player row is created on the **first successful Steam login** (`PlayerService.UpsertFromSteamLoginAsync`), not on the first Feedback; `LastLoginAt` records the most recent login. Submitting Feedback requires a login, so the row always exists by then.
- `SteamName` / `AvatarUrl` are refreshed on every login; when Steam cannot supply a new value the previous one is kept.
- Ownership is per Game: a Player can only read and comment on Feedback in the Game they authenticated for.

## SteamCredential

A Steam Publisher Web API Key, stored encrypted for use by one or more Games.

```text
Id
Name            varchar(100)
EncryptedApiKey text
CreatedAt
UpdatedAt
```

Rules:

- `EncryptedApiKey` holds ciphertext produced with ASP.NET Core Data Protection; the plaintext key is never stored, never returned in a DTO, and never rendered back into an admin page (ADR-0008).
- One credential can serve many Games: today a single shared publisher key covers every game we ship, so rotating a key is one edit.
- A credential referenced by any Game cannot be deleted (`Restrict`; `SteamCredentialService.DeleteAsync` reports `CredentialDeleteOutcome.InUse`).

## Feedback

Recommended fields:

```text
Id
GameId
PlayerId
Type
Title
Content
Status
GameVersion
BuildNumber
OperatingSystem
Gpu
Cpu
MemoryTotalMb
PlaytimeMinutes
Locale
Map
Character
CreatedAt
UpdatedAt
```

Rules:

- `GameId` is required and points at the Game the Feedback was submitted to. It is derived from the resolved URL path segment (the Game's Steam AppID), never from the request body.
- `GameId` is configured with `Restrict`, deliberately **not** cascade: deleting a Game must never silently delete player Feedback. Combined with the delete guard, a Game with any Feedback cannot be deleted at all — it gets disabled instead, which keeps the history.

Notes on the three newest fields:

- `Cpu` / `MemoryTotalMb` are **Hardware Info**: client-collected, advisory, untrusted, and bounded (see ADR-0006). Out-of-range values are discarded rather than rejected.
- `PlaytimeMinutes` is **Playtime**: the Player's accumulated minutes **in this Game**, fetched by the server from Steam when the Feedback is submitted and snapshotted here. It is not a live value; when Steam cannot supply it the column is `null` (`0` means "owns it, never played"). The lookup uses the resolved Game's AppID and credential.

Initial types:

```text
Bug
Suggestion
Other
```

Initial statuses:

```text
Open
InProgress
Resolved
Closed
```

Do not add a complicated workflow unless requested.

## FeedbackComment

Recommended fields:

```text
Id
FeedbackId
AuthorType
PlayerId?
AdminUserId?
Content
CreatedAt
UpdatedAt?
```

`AuthorType`:

```text
Player
Admin
```

A player may only view or comment on feedback owned by the authenticated Steam account, within the Game they authenticated for.

## Why the `Restrict` foreign keys

Every history-protecting link is `Restrict` on purpose: `feedbacks.GameId`, `players.GameId`, and `games.CredentialId`. Cascade would let an admin's "delete this game" click — or a future cleanup job — erase a game's entire Feedback history as a side effect, which is exactly the outcome the system must never have. `Restrict` turns that into a database-level refusal, so the service-level `HasData` / `InUse` outcome the admin UI surfaces is a courtesy on top of a guarantee rather than the guarantee itself. Deleting a Game is rare; losing a player's report is not recoverable.
