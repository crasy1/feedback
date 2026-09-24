# ADR-0007: Multi-Game Support via a Per-Game Path Prefix and Per-Game Scoping

- Status: Accepted
- Date: 2026-09-24
- Revised: 2026-09-24 (addressing — see [Revision](#revision-same-day-after-the-first-deploy-attempt) at the end of this record)

> **The addressing decision below was reversed on the day it first shipped.** The path segment is no longer a self-invented `Slug`; it is the Game's **Steam AppID** (`/g/{appId}/api/...`), and `games.Slug` no longer exists. The Context, Decision, Considered Options and Consequences text is the original record and is deliberately left unedited; the revision at the end states what changed, why the original reasoning no longer applies, and what was rejected. Where the two disagree, the revision is what the code does.

## Context

Until now this instance served exactly one Steam application: a single deployment-wide `Steam:AppId` scalar decided whose tickets were accepted, whose playtime was looked up, and which rows in `players` and `feedbacks` belonged together. Serving a second game meant standing up a second instance with its own database, its own admin account, and its own deployment.

That is no longer acceptable. The same operator ships several games, and a second copy of the service is a second thing to patch, back up, sign in to, and provision a key for. The Steam publisher key is already shared across those games and the admin is one person. What genuinely differs per game is the AppID, the identity string, and the URL a shipped build points at.

Two facts shape the design:

- **The Game cannot be recovered from the Steam ticket.** `ISteamUserAuth/AuthenticateUserTicket/v1` takes `appid` as an *input* and does not echo it back — the same is true of the playtime lookup's `IPlayerService/GetSingleGamePlaytime/v1` (confirmed by probe, see `.scratch/env-info-playtime-admin-ui/issues/01-probe-get-single-game-playtime.md`). A ticket response says "this account is real and this ticket was issued for the AppID I asked about"; it never says which AppID that was. The AppID must therefore be known *before* verification, not derived from it.
- **The Feedback Client already takes a base URL.** It normalizes a trailing slash before use (`addons/gd_feedback/FeedbackRuntime.cs:341-343`), so a base URL carrying a path segment works with no addon change at all.

## Decision

One instance, one database, N Games.

- **Terminology.** A **Game** is one Steam application this instance serves, identified by its Steam AppID (canonical glossary: `CONTEXT.md`). A **Player** is a person authenticated through Steam *within one Game*: one Steam account maps to exactly one Player **per Game**. The old "one Steam account maps to exactly one Player" rule is gone — the same Steam account playing two of our games is two Players with two independent Feedback histories.
- **Addressing.** The player API lives only under `/g/{slug}/api/...` (e.g. `POST /g/neon-drift/api/auth/steam`). The root paths (`/api/auth/steam`, `/api/feedback`, `/api/feedback/mine`, `/api/feedback/{id}`, `/api/feedback/{id}/comments`) are **removed permanently** and answer `404` with a `ProblemDetails` extension member `code = "game_required"`. There is no "default game" and no fallback to one. `/health` and `/admin` stay at the root.
- **Resolution errors.** Unknown slug → `404 game_not_found`; a Game that exists but is disabled → `403 game_disabled`; a Game whose `SteamAppId` or credential is not yet configured → `401 steam_unavailable` (the existing "Steam could not be consulted" semantics, see ADR-0008); a credential that exists but cannot be decrypted → `401 credential_unreadable`, so a lost Data Protection key ring is never diagnosed as Steam being down. A slug that disagrees with the Game in the caller's token is `401 game_mismatch`.
- **The token is bound to a Game.** The Access Token gains a `game` claim carrying the Game's numeric database id. A token minted for one Game is rejected on another Game's path (`401 game_mismatch`), so neither a slug rename nor a copied client base URL can silently cross games. Tokens issued before this upgrade carry no claim and are rejected the same way. Players are unaffected: on a `401` from a non-login endpoint the Feedback Client clears its cached token, re-authenticates with a fresh Steam Ticket, and retries exactly once (`addons/gd_feedback/FeedbackRuntime.cs:374-381`).
- **Per-Game scoping in the data model.** `players` gains a required `GameId`, and the global UNIQUE index on `SteamId` becomes a composite UNIQUE index on `(GameId, SteamId)`. `feedbacks` gains a required `GameId` foreign key to `games` configured with `Restrict` — deliberately, **not** cascade, so that deleting a Game can never silently delete player Feedback. A new `games` table carries `Id`, `Slug` (unique; the URL segment), `SteamAppId` (unique, nullable), `Name`, `Identity`, `IsActive`, `CredentialId` (nullable, `Restrict`) and timestamps; where its Steam configuration lives is ADR-0008.
- **Rate limiting stays per-Player, now per (Game, SteamID).** Five feedbacks per ten minutes and twenty comments per ten minutes are counted per Player *within one Game*, so two games cannot spend each other's quota. The pre-authentication IP-based partition is unchanged.
- **Playtime is unchanged in meaning** (ADR-0006 still governs it): a per-Feedback snapshot of the Player's accumulated minutes *in that Game*, fetched server-side at submission time, `null` when Steam cannot supply it, never blocking a submission. Only the lookup's inputs change — the resolved Game's AppId and credential instead of a global scalar.
- **The Feedback Client ships unmodified.** Each Game's build sets its `BaseUrl` to `https://<host>/g/<slug>`.
- **Deliberately out of scope: per-tenant physical isolation and per-game deployments.** No separate database, schema, or container per Game, and no one-deployment-per-game topology. Every Game shares one schema, one admin identity system, and one process.

## Considered Options

- **One deployment per game, with a configurable AppId.** The status quo made explicit. Rejected: N deployments means N databases to migrate, back up, and restore, N admin accounts, and N upgrade windows for a single operator; a change that spans games (a new Feedback field, a security fix) has to be repeated N times and verified N times. It solves nothing that per-Game rows do not solve, at a far higher operational cost.
- **Per-Game database or schema isolation.** Rejected: its only real benefit is blast-radius separation, which this system does not need — the data is one operator's own game feedback, not mutually hostile tenants. It would make cross-game admin views and the shared credential table materially harder, and would push connection-string selection into runtime request code.
- **Identifying the Game from the Steam ticket response.** Impossible, not merely inconvenient: `AuthenticateUserTicket` takes `appid` as an input parameter and does not echo it, and `GetSingleGamePlaytime` likewise does not echo `appid`. There is no response field to read, so no amount of response parsing recovers the AppID.
- **Letting the client declare its AppId as a trusted value.** Rejected on the standing invariant that client-supplied identity is never trusted. The client runs on the player's own machine; an AppId from a request body or query string is attacker-chosen, and would let anyone authenticate against any registered AppId — including one that is not shipped — while the server believed it had resolved a Game.
- **Server-side trial verification against every registered AppId.** Ask Steam to verify each ticket once per registered Game, O(N) Steam round trips per login, first success wins. It works without the client naming anything, and was still rejected: login latency and Steam API consumption would grow with the number of games, an unauthenticated endpoint would become a denial-of-service amplifier, and it does not help the playtime lookup, which takes a single AppId and needs an answer the ticket cannot supply.
- **Keeping a "default game" so the root path keeps working.** Rejected: a default makes the Game an implicit, deployment-wide fact again, and would mean `/api/feedback` means different things on two identically configured instances. It also re-opens the "which AppId is this ticket for" question that the path prefix exists to answer. The root paths are removed outright so that a stale shipped build fails loudly with `game_required` instead of quietly writing into the wrong Game.

## Consequences

- The slug is now part of every shipped build's base URL. Renaming a Game in the admin UI is allowed, but it is a **breaking change for builds already in players' hands**: the admin UI warns about this, and no alias or history table is kept.
- A Game with a `null` `SteamAppId` or no credential fails logins closed with `401 steam_unavailable` — the same code a Steam outage produces. The difference is visible to the Admin (a credential can be verified in the UI) and in logs; it is deliberately not distinguishable to the Player.
- Cross-game reads are impossible at the query level rather than rejected by a check: ownership queries are scoped by the Player's `GameId`.
- Deleting a Game is refused while it has any Feedback or Player, and a credential referenced by any Game is refused; the Admin disables a Game instead, which stops new logins without losing history.
- One database dump now contains every Game's Feedback, and one restore has the blast radius of all of them.
- Pre-upgrade rows must be attached to some Game (see the implementation spec's migration decision): the schema is per-Game from here on, and there is no global Player or Feedback left.

## Revision (same day, after the first deploy attempt)

**This section is accepted, and it is what the code does.** The original record above is kept verbatim as the history of what was first decided. Where the two disagree, this section wins.

### What changed

- **The player API is addressed by Steam AppID, not by a slug.** The route is `/g/{appId}/api/...`, where `{appId}` is the Steam application id — digits only, at most 10 characters — e.g. `POST /g/1910980/api/auth/steam`. Slugs are gone from the system entirely.
- **`games.Slug` and its unique index `IX_games_Slug` are dropped** by migration `20260924124609_AddressGamesBySteamAppId`. It is deliberately a *separate* migration rather than a rewrite of `20260924114358_MultiGameSupport`: migration 4 may already have run in a live database, and redefining an applied migration leaves that database unable to reach the same schema by the documented path.
- **`games.SteamAppId` is the single addressing key**: unique, `varchar(10)`, and still nullable for exactly one reason — the row the upgrade migration creates for pre-existing data cannot know its AppID. `GameResolver` normalizes the path segment (`GameValidation.NormalizeAppId`) and refuses anything that is not all digits *before* it queries, so a Game with a `NULL` `SteamAppId` is **not addressable at all**, by any path.
- **The admin UI requires the AppID on both create and edit.** `GameValidation.ValidateSteamAppId` returns "Steam AppID 不能为空：它同时是玩家 API 的路径段 /g/{appId}/api/..." for a blank value, and rejects non-digits, more than 10 characters, and all-zeros. `GameValidation` has no slug rule any more; the add/edit form has no slug field. The form does warn when an *existing* Game's AppID is changed, because that is still a breaking change for clients running under the old one (`GameEdit.razor`), but there is no acknowledgement checkbox.
- **`404 game_not_found` changed meaning and detail.** It now means "no Game has this Steam AppID". Its `detail` names the requested AppID and tells the operator to check that the Game exists in the admin UI with that AppID; it no longer lists the configured games. A non-numeric path segment resolves to nothing and answers the same `404`.
- **The admin feedback list's `game` query-filter value is the AppID, not the slug.** `AdminFeedbackQuery.Game` is compared against `f.Game!.SteamAppId`, and the dropdown submits `@game.SteamAppId`. The slug-rename warning, the acknowledgement checkbox, and the "no alias is kept" reasoning are all gone — there is no slug to rename.
- **The "unconfigured Game" story changed.** Previously `/g/default` resolved the placeholder Game and the login endpoint answered `401 steam_unavailable` ("configure me"). The placeholder Game has a `NULL` `SteamAppId`, so there is now **no path to it at all**, and the only half of `steam_unavailable` still reachable at the login endpoint is the **missing credential** (or a Steam call that concluded nothing). The placeholder is reached only through the admin UI, and an Admin must supply the AppID before any client can reach it. It is no longer `Slug = 'default'` at all — the column is gone; it is just a row named `默认游戏`.
- **The Feedback Client no longer ships unmodified.** The addon is version 1.3.0 and *derives* the path segment instead of being configured with it (details below).

### Why the original reasoning no longer applies

ADR-0007 rejected AppID addressing on a real risk: a Steam AppID can change (a playtest build becoming the retail app), and a shipped build whose base URL hard-coded `/g/<old-appid>` would be stranded. **That objection only holds while a human copies an identifier into configuration.** The revised client does not copy anything. `FeedbackConfig.BaseUrl` is now just the feedback service address (`https://feedback.example.com`), and the addon appends `/g/{appId}` from a new optional host-injected interface, `IGameAppIdProvider` (null-object default `UnavailableGameAppIdProvider`, both in `addons/gd_feedback/FeedbackAbstractions.cs`). The host returns the AppID the game is running as — the same value it already passes to `SteamClient.Init`. A build running under the new AppID therefore requests the new AppID automatically: the AppID-change scenario that motivated the original decision is exactly the scenario the revised client absorbs for free.

That the addon stays 100% Steam-free is not incidental — ADR-0004 is untouched and its verify gate still enforces an engine-free, dependency-free core — and it is precisely **why** the value has to be injected rather than read by the addon itself.

The slug scheme's own failure mode, by contrast, was real and was hit in practice: an operator created a Game with one slug while the client asked for another, and the resulting `404 game_not_found` was indistinguishable from "no such Game" — two rounds of debugging for a string mismatch. An AppID has no such class of error: nothing is copied by hand, it is the number the process is already running under, and Steam verification itself proves the ticket was issued for it. The trade flipped.

The invariants the original decision protected are intact. An AppID in the path is a client-supplied **address**, not client-supplied identity: the server resolves it against `games.SteamAppId` and decides everything else from its own database; the token's `game` claim (still the numeric database id) must agree with the resolved Game; and `AuthenticateUserTicket` is still called with the resolved Game's AppID, so a ticket cannot be replayed against another Game. The rejected alternative "letting the client declare its AppId as a trusted value" therefore still stands rejected — the AppID names which Game to resolve, it does not authorize anything by itself.

### What the revised client does, exactly

- `FeedbackConfig.BaseUrl` is the feedback service address. When the host implements `IGameAppIdProvider` and it returns a numeric AppID (digits only, at most 10 characters), the addon appends `/g/{appId}`. The path completion and the fail-closed check are `addons/gd_feedback/FeedbackRuntime.cs:358-376`, and the trailing-slash normalization the original Context cited at `341-343` and the self-heal the Decision cited at `374-381` now live at `378-380` and `413-429` respectively — the citations in the original text above are historical.
- When the host does not implement the provider, or returns `null`/blank, the addon uses `BaseUrl` verbatim. A base URL that already contains a path keeps working unchanged.
- When the host returns something that is **not** a numeric AppID (digits only, at most 10 characters) — a slug, by mistake — the addon fails closed with `invalid_configuration` and sends no request. That is the direct answer to the failure mode above: a mis-derived identifier becomes a local configuration error instead of a server-side `404` that has to be debugged.
- `game_not_found` (login endpoint `404`) and `game_disabled` (login endpoint `403`) remain mapped to non-retryable configuration errors.

### What the revision rejected

- **Keeping `Slug` alongside `SteamAppId`**, whether as the path segment or as a second unique key. Rejected: once the client derives the identifier, a second human-maintained identifier exists only to be got wrong — which is the failure that prompted the reversal. The column and its index are dropped outright, not deprecated.
- **Folding the drop into migration 4.** Rejected: migration 4 may already have run, and a database that applied it must keep working. Hence `20260924124609_AddressGamesBySteamAppId` as its own migration, separate from `20260924114358_MultiGameSupport`.
- **Accepting a slug in the path and translating it to a Game.** Rejected: `ResolveGameFilter` would then answer `404` for two unrelated reasons under one code, which is the indistinguishable failure this revision exists to remove.
- **Deriving the AppID on the server instead of in the client.** Still impossible, for the original reason: `AuthenticateUserTicket` and `GetSingleGamePlaytime` both take `appid` as an input and do not echo it.

### Consequences of the revision

- `games.SteamAppId` uniqueness is unchanged, and the `Restrict` foreign keys (`feedbacks.GameId`, `players.GameId`, `games.CredentialId`) are unchanged.
- The upgrade placeholder Game is unreachable by every client until an Admin fills in its AppID, and the admin UI forces that on edit.
- [ADR-0008](0008-steam-configuration-in-the-database.md) is unchanged except where it said a Game with a `NULL` `SteamAppId` answers `401 steam_unavailable`; it now cannot be reached at all.
