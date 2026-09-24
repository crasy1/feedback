# Overview

Steam Game Feedback System — a lightweight, self-hosted feedback system for Steam games. One instance, one database, several Games.

## Primary flow

```text
Godot Game  (the addon appends /g/<AppID>, supplied by the host)
  ↓
Steam Auth Ticket
  ↓
ASP.NET Core API  /g/{appId}/api/...
  ↓
Game resolved from the Steam AppID in the path
  (no such AppID → 404 game_not_found, disabled → 403 game_disabled,
   credential not configured → 401 steam_unavailable)
  ↓
Steam Web API verifies the ticket with that Game's AppID + credential
  ↓
Trusted SteamID64 — a Player within that Game
  ↓
Local JWT carrying the `game` claim, plus a local access token
  ↓
Feedback / Comments — scoped to that Game
  ↓
PostgreSQL

Developer Browser
  ↓
Blazor Admin  /admin
  ↓
Review / Reply / Change Status
Manage Games and Steam credentials
```

Players must not be required to register an email address or create a separate feedback-system account.

A **Game** is one Steam application this instance serves, identified by its Steam AppID; a **Player** is a person authenticated through Steam *within one Game*. The same Steam account playing two of our games is two Players with two independent Feedback histories. Feedback, Playtime, Ownership, and administrative review are all scoped to exactly one Game.

The player side of the flow ships as a Godot addon in this repository (`addons/gd_feedback/`): it obtains the Steam ticket from the game's own Steam integration, exchanges it for an access token, and submits Feedback and Feedback Comments. The addon is game-agnostic and needs no per-Game code: its base URL is just the feedback service address, and the host injects the AppID the game runs as (`IGameAppIdProvider`), from which the addon builds the `/g/{appId}` path segment itself. The addon never reads Steam directly, so injecting the value is what keeps its core engine-free and dependency-free (ADR-0004).

The server never trusts anything the client asserts about identity — including which Game a request belongs to. The Game comes from the URL path the server itself resolved against `games.SteamAppId`, never from the request body.

This project is intentionally small. It is not a CRM, survey platform, public roadmap, community forum, or general-purpose help desk.

## Out of scope unless requested

**In scope:** a single instance and a single database serving several Games, with each Game's Steam AppID and credential managed in the admin UI.

Do not build these preemptively:

- public feedback board
- voting
- roadmap
- email login for players
- OAuth login for players
- surveys
- CRM
- real-time chat
- AI classification
- complex roles
- per-tenant physical isolation (a separate database, schema, or container per Game)
- per-game deployments (one deployment per Steam application)
- analytics warehouse
- microservices
- notification center
- mobile admin app

## Related documents

- Architecture & deployment: [architecture.md](architecture.md)
- Security design: [security.md](security.md)
- Domain model: [domain-model.md](domain-model.md)
- Testing: [testing.md](testing.md)
- Player API spec: [specs/player-api.md](specs/player-api.md)
- Godot feedback client addon: [../addons/gd_feedback/README.md](../addons/gd_feedback/README.md)
- Admin UI spec: [specs/admin-ui.md](specs/admin-ui.md)
- Attachments (future): [specs/attachments.md](specs/attachments.md)
- Decision records: [adr/](adr/)
