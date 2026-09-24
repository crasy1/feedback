# ADR-0008: Steam Configuration and API Keys Live in the Database

- Status: Accepted
- Date: 2026-09-24

## Context

Steam configuration used to be deployment configuration: `Steam:ApiKey`, `Steam:AppId` and `Steam:Identity` in `appsettings*.json`, `docker-compose.yml`, `.env.example`, `scripts/run_local.py`, and the README. One instance, one AppId, one key.

ADR-0007 makes that shape impossible. The AppID is now a per-Game runtime fact, and an Admin must be able to add or fix a Game without editing files and redeploying the container. The API key is a shared publisher key that already covers every game we ship, so it must not be duplicated per Game either: rotating it has to be one edit.

Nothing about the key's secrecy changes. It is a Steam Publisher Web API Key, and the key plus an AppID is enough to call the Steam Web API.

## Decision

The database is the single source of truth for Steam configuration, and the admin UI is the way it is edited.

- **Per-Game values live on the `games` row**: `SteamAppId` (unique, nullable) and `Identity` (default `feedback-api`). `SteamAppId` is also the player API's path segment (`/g/{appId}/api/...`; see the revision in ADR-0007), so a Game with a `null` `SteamAppId` is **not addressable at all** — no request resolves to it and it never reaches the login endpoint. A Game that has an AppID but no credential is reachable and fails its logins closed with `401 steam_unavailable`.
- **Keys live in a separate `steam_credentials` table**: `Id`, `Name`, `EncryptedApiKey`, timestamps. One credential can serve many Games, because today's single shared publisher key covers every game we ship. Rotating a key is one edit, and every Game referencing that credential picks up the new value.
- **Keys are encrypted at rest with ASP.NET Core Data Protection** (`IDataProtector`, purpose `GameFeedback.SteamApiKey.v1`). The key ring is persisted to a mounted volume and included in backups. The Data Protection application name is pinned explicitly with `SetApplicationName("GameFeedback")`, because the default derives from the content-root path — which can change between image builds — and a changed application name (or purpose) would make every stored credential permanently undecryptable. A decryption failure produces its own distinguishable error and log line (`401 credential_unreadable` for the affected Game, an `Error` log carrying the credential id); it must never be reported as, or mistaken for, a Steam outage.
- **The API key is write-only in the admin UI.** It is never rendered back into any HTML (including a form re-render after a validation failure), never returned in any DTO, and never logged. Credential-change log lines record the admin user id and the fact of the change, never the value.
- **Environment variables no longer carry Steam configuration.** `Steam:ApiKey`, `Steam:AppId` and `Steam:Identity` are removed from `appsettings*.json`, `docker-compose.yml`, `.env.example`, `scripts/run_local.py`, and the README. The one Steam-related variable that remains is `Steam:DebugSkipTicketValidation`, a development-only switch gated to `ASPNETCORE_ENVIRONMENT=Development`: a "skip all ticket verification" toggle must not be reachable from a UI protected only by an admin password.
- **No seeding and no import from environment variables.** The first credential is entered by an Admin in the UI, and the first Game is created there too. The database stays the single source of truth precisely because nothing writes to it behind the operator's back.

## Consequences

- Startup no longer validates Steam credentials. The Steam `ValidateOnStart` checks and the `IsPlaceholderSteamValue` warning are removed, because there is nothing in configuration left to validate. Credential health becomes a runtime, per-Game question answered by the admin UI's verify probe.
- Every fresh deployment has a window in which the `games` table is empty, or holds Games with no credential, and player logins fail closed with `401 steam_unavailable`. (A Game with no AppID never gets that far: it has no path.) The Admin is forced to the "add a game" page until at least one Game exists, and the deployment notes must say that first-run provisioning is now an admin-UI step rather than an environment variable.
- A database dump is now secret material: it contains encrypted API keys, and together with the key ring it contains the means to decrypt them. The Data Protection key ring is a new entry on the backup checklist, alongside the database. Losing it does not lose Feedback, but it does make every stored credential undecryptable and force a re-entry.
- Rotating the shared publisher key is a single edit in `/admin/credentials`; no redeploy and no coordinated restart are involved.
- Deleting a credential that any Game references is refused (`CredentialDeleteOutcome.InUse`, backed by a `Restrict` foreign key), so a live Game cannot lose its key by accident — the Admin disables or repoints the Game instead.

## Considered Options

- **Keeping the API key in environment variables.** Rejected: it cannot be changed without a redeploy and a container restart, which is exactly the operation that must be cheap when a key is rotated or suspected compromised. And once the AppID is per-Game, half of the credential pair would be in the database and half in the deployment — the worst of both arrangements.
- **Storing the key in plaintext.** Rejected outright. A database dump, a replica, or a careless query log would leak a publisher key that can call the Steam Web API for every game we ship.
- **One credential column per Game instead of a credentials table.** Rejected: one shared publisher key covers every game today, so N columns would hold N copies of the same secret. Rotating it would mean editing every Game and getting all of them right, and forgetting one would surface as a single Game's logins failing — a bad failure mode bought for nothing.
- **Importing credentials from environment variables once at first boot.** Rejected: it re-creates two sources of truth (the environment and the database) and an implicit one-time migration whose only purpose is to avoid typing a key once. It also has to answer "what if the variable changes after first boot?", and every honest answer puts the environment back in charge as a live source.
