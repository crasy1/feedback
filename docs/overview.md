# Overview

Steam Game Feedback System — a lightweight, self-hosted feedback system for a Steam game.

## Primary flow

```text
Godot Game
  ↓
Steam Auth Ticket
  ↓
ASP.NET Core API
  ↓
Steam Web API verifies ticket
  ↓
Trusted SteamID64
  ↓
Local JWT
  ↓
Feedback / Comments
  ↓
PostgreSQL

Developer Browser
  ↓
Blazor Admin
  ↓
Review / Reply / Change Status
```

Players must not be required to register an email address or create a separate feedback-system account.

This project is intentionally small. It is not a CRM, survey platform, public roadmap, community forum, or general-purpose help desk.

## Out of scope unless requested

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
- multi-tenancy
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
- Admin UI spec: [specs/admin-ui.md](specs/admin-ui.md)
- Attachments (future): [specs/attachments.md](specs/attachments.md)
- Decision records: [adr/](adr/)
