# Spec: Steam Game Feedback System — MVP

Status: ready-for-agent

## Problem Statement

Players of a Steam game have no lightweight way to report bugs or give feedback, and the developer has no place to review, reply, and track that feedback. Existing tools (CRMs, survey platforms, public roadmaps, forums) are all too heavy and all require players to register yet another account. Players must not be required to register an email address or create a separate feedback-system account.

## Solution

A small, self-hosted web application: players authenticate through Steam from the game client and submit feedback; a Blazor admin UI lets the developer review feedback, reply, and change status. Identity is inherited from Steam (Players) and a password login (Admins); no email collection for Players. One ASP.NET Core application serves both the game-facing API and the admin UI, backed by PostgreSQL, deployed with Docker Compose.

## User Stories

1. As a Player, I want to authenticate with my Steam account without registering an email or a new account, so that giving feedback is friction-free.
2. As a Player, I want the game client to send a Steam Ticket and receive an Access Token, so that subsequent requests identify me without contacting Steam again.
3. As a Player, I want my Steam display name and avatar shown to the Admin, so that the developer knows who reported what without me typing it.
4. As a Player, I want to submit a Feedback with a type (Bug / Suggestion / Other), a title, and content, so that I can report my issue or idea.
5. As a Player, I want to optionally attach environment metadata (game version, build number, OS, GPU, locale, map, character), so that the developer can reproduce my bug without asking me.
6. As a Player, I want to see the Feedback I have submitted, so that I can check what I reported and its current status.
7. As a Player, I want to read a single piece of my Feedback with its comments, so that I can follow the developer's response.
8. As a Player, I want to comment on my own Feedback, so that I can add details after submitting.
9. As a Player, I want a request for someone else's Feedback to behave as if it does not exist (404), so that I cannot even confirm other Players' submissions exist.
10. As a Player, I want oversized or malformed input rejected with a clear error, so that I know immediately when my text is too long.
11. As a Player, I want my Access Token to expire after a day, so that a stolen token has limited value; I can re-authenticate with a fresh Steam Ticket.
12. As an Admin, I want to sign in with a password and a secure cookie, so that only I can access the admin UI.
13. As an Admin, I want an initial admin account created automatically from deployment configuration the first time the app starts, so that I don't need a registration flow or manual SQL.
14. As an Admin, I want a paginated Feedback list (50 per page, newest first) filterable by status, type, and game version, so that I can triage reports quickly.
15. As an Admin, I want a Feedback detail page showing content, Steam name, SteamID64, profile link, environment metadata, comments, and status, so that I have full context in one place.
16. As an Admin, I want to reply to a Feedback, so that the Player can see my response.
17. As an Admin, I want to change a Feedback's status (Open / InProgress / Resolved / Closed), so that I can track triage progress.
18. As an Admin, I want players and admins to be separate identity systems, so that a Player token can never reach admin functionality.
19. As an operator, I want the database schema migrated automatically on startup, so that deployment is just `docker compose up`.
20. As an operator, I want abuse protection on the unauthenticated login endpoint (IP-based) and on authenticated write endpoints (per-Player), so that the service survives spam.
21. As an operator, I want an unauthenticated health endpoint, so that my tunnel/reverse proxy can probe the service.
22. As an operator, I want Steam verification to fail closed, so that a Steam outage or a malformed response never results in an authenticated session.
23. As an operator, I want secrets (Steam Publisher Web API Key, JWT signing key, database password, admin password) to come from environment configuration and never appear in logs, so that the deployment is safe to publish.
24. As a developer, I want Steam responses stubbed at the HTTP transport in tests, so that the test suite runs without real Steam credentials.
25. As a developer, I want the domain model, security design, and decisions documented in the repo, so that future agents and humans implement consistently with the glossary and ADRs.

## Implementation Decisions

- **Stack (ADR-0001)**: .NET 10, ASP.NET Core Minimal APIs, one application hosting both the player API and a Blazor Web App (global Interactive Server render mode) admin UI, EF Core + Npgsql/PostgreSQL, Docker Compose. The rejected-technologies list in ADR-0001 (Node.js, SPA frameworks, Redis, MediatR, CQRS, microservices, Kubernetes…) is a standing constraint.
- **Layout**: a single ASP.NET Core application project plus a separate test project; solution file at the repo root. Module boundaries per `docs/architecture.md` (Api / Contracts / Domain / Services / Data / Components); no substantial business logic in endpoint handlers or Razor components.
- **Dual identity (ADR-0002)**: Players authenticate with a Steam Ticket verified via `ISteamUserAuth/AuthenticateUserTicket/v1` using server-side Publisher Web API Key + AppID + identity `feedback-api`; on success the server issues a local Access Token. Admins authenticate via ASP.NET Core Identity with a secure HTTP-only cookie. A Player token never authorizes Admin operations.
- **Admin seeding**: on startup, if no admin user exists, one is created from `Admin__SeedEmail` / `Admin__SeedPassword` configuration; existing installs are never touched.
- **Token design (ADR-0003)**: 24-hour expiry, HS256 with a 256-bit key from configuration, subject = SteamID64, no refresh tokens.
- **SteamID storage**: opaque string, `varchar(20)`, unique index; one Steam account maps to exactly one Player.
- **Profile sync**: on each login the server calls `ISteamUser/GetPlayerSummaries` to refresh the Player's Steam name and avatar; best effort — failure does not fail the login.
- **Domain model**: Player, Feedback, Feedback Comment with fields per `docs/domain-model.md`; Feedback types (Bug/Suggestion/Other) and statuses (Open/InProgress/Resolved/Closed) stored as strings; timestamps in UTC. No workflow state machine.
- **Player API v1**: `POST /api/auth/steam`; `POST /api/feedback` (type, title, content + optional client-supplied metadata: gameVersion, buildNumber, operatingSystem, gpu, locale, map, character); `GET /api/feedback/mine` (latest 100, no pagination); `GET /api/feedback/{id}`; `POST /api/feedback/{id}/comments`. The SteamID always comes from the authenticated principal, never the request body. Ownership enforced at query level; a foreign Feedback reads as 404.
- **Validation**: title 1–200, content 1–10,000, comment 1–5,000, version/build/metadata fields bounded; plain text only; all player text untrusted.
- **Rate limiting**: ASP.NET Core rate limiting, fixed window — auth 10/min per IP (before authentication), create-feedback 5/10min per Player, comments 20/10min per Player (after authentication, by SteamID); configurable.
- **Errors**: standard status codes, `ProblemDetails` where appropriate, no stack traces to clients.
- **Persistence**: EF Core + Npgsql, migrations apply automatically on startup (`Database__AutoMigrate`, default on); indexes on Player.SteamId (unique), Feedback.PlayerId/Status/CreatedAt/GameVersion, FeedbackComment.FeedbackId.
- **Admin UI**: Simplified Chinese text (hardcoded), pages for the Feedback list (page/pageSize, default 50, newest first, filters by status/type/game version) and Feedback detail (reply, change status). The admin UI calls application services directly, not HTTP. Admins cannot edit or delete anything in v1.
- **Deployment**: Docker Compose with app + `postgres:17` (localhost-only exposure for development); forwarded headers supported behind a trusted reverse proxy; unauthenticated `GET /health`; console structured logging with secret-free output. Example configuration files provided (`appsettings.example.json`, `.env.example`); real secrets only via environment.

## Testing Decisions

- A good test asserts external behavior only — HTTP request/response pairs, status codes, response bodies, and application-service results — never internal implementation details; internal refactors must not break the suite.
- **Primary seam: the HTTP boundary.** The whole application is hosted in a `WebApplicationFactory`; tests drive real routing, JWT bearer auth, Identity cookies, rate limiting, and a real PostgreSQL (Testcontainers container). The only stub is the Steam HTTP transport (`HttpMessageHandler` beneath the Steam client), returning canned responses (valid ticket, invalid ticket, missing SteamID, Steam API failure, malformed payload). This covers the fail-closed branches, token subject correctness, Ownership 404s, validation limits, and rate limiting end to end.
- **Auxiliary seam: application services, admin-only operations.** Admin reply and status-change logic is exercised by direct service calls (the Blazor layer over them is a thin view). No bUnit component tests.
- Not tested separately: `SteamAuthService` validation in isolation and Access Token generation in isolation — both are covered through the HTTP seam.
- Coverage areas follow `docs/testing.md`: Steam authentication, Ownership, Admin access, Validation/rate limiting. Steam is always mocked; no test requires real Steam credentials.
- Prior art: none (greenfield); the test project establishes the pattern for future features.
- Integration tests require Docker Desktop running (it is installed but its daemon was not running when this spec was written).

## Out of Scope

- Player edit/delete of own Feedback; any Admin edit/delete/moderation capability
- Refresh tokens, email/OAuth login for Players, complex roles, multi-tenancy
- Attachments (see `docs/specs/attachments.md` — future, including any S3/MinIO storage)
- Public feedback board, voting, roadmap, surveys, notifications, real-time chat, analytics
- The Godot game client itself (separate project; this repo is server + admin only)
- CI/CD pipelines; localization framework for the admin UI
- Anything on the out-of-scope list in `docs/overview.md`

## Further Notes

- Domain vocabulary is canonical in `CONTEXT.md` (Player, Admin, Feedback, Feedback Comment, Author Type, Steam Ticket, Access Token, Ownership); use those terms in code identifiers, test names, and future ADRs.
- Architecture/security/spec references: `docs/overview.md`, `docs/architecture.md`, `docs/security.md`, `docs/domain-model.md`, `docs/testing.md`, `docs/specs/*`.
- Development workflow: `docker compose up -d postgres` + `dotnet run`; full stack via `docker compose up`. Integration tests need Docker Desktop started.
- `AGENTS.md` governs agent workflow, hard security invariants, and the Definition of Done for any implementation ticket broken out of this spec.
