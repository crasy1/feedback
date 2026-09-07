# AGENTS.md

Agent operating manual for the Steam Game Feedback System.

Product overview and scope: `docs/overview.md`. Architecture and deployment: `docs/architecture.md`. Security design: `docs/security.md`. Domain model: `docs/domain-model.md`. Testing: `docs/testing.md`. Feature specs: `docs/specs/`. Decision records: `docs/adr/`.

## Hard Security Invariants

These rules are mandatory. Violating any of them fails the task.

- Never trust client-supplied identity. The SteamID comes from the validated JWT / authenticated principal, never from query, body, or headers.
- Steam verification failures fail closed. A successful HTTP response from Steam alone is not enough; validate the response payload and require a valid SteamID64. If Steam verification is unavailable, do not authenticate.
- A player JWT must never authorize administrator operations. Players and admins are two separate identity systems.
- Enforce ownership at the query level. A player may only read or comment on feedback owned by the authenticated Steam account; prefer `404` when the resource is missing or belongs to another player.
- Treat all player text as untrusted. Use plain text for the MVP; if rich text is added later, sanitize before rendering.
- Keep JWTs short-lived. Validate signature, issuer, audience, and expiration. Do not put sensitive information in JWT payloads.
- Never expose or commit secrets: Steam Publisher Web API Key, JWT signing key, database password, admin password.
- Never log secrets or authentication material: Steam auth tickets, JWTs, Steam API keys, passwords, Authorization headers.

## Stack Constraints

Required stack:

- .NET 10, ASP.NET Core, Minimal APIs
- Blazor Web App with Interactive Server for the admin UI
- Entity Framework Core + Npgsql / PostgreSQL
- ASP.NET Core Identity or equivalent cookie authentication for admins
- JWT bearer authentication for the game client
- Docker / Docker Compose

Do not introduce without a demonstrated need:

- Node.js backend, React / Vue SPA
- Redis, Elasticsearch, RabbitMQ / Kafka
- MediatR, CQRS framework
- microservices, Kubernetes

Before adding a dependency or subsystem, ask: can this be implemented cleanly with ASP.NET Core, EF Core, PostgreSQL, or Blazor already in the project? If yes, use the existing stack. Prefer built-in ASP.NET Core capabilities and the simplest implementation that meets the requirement.

## Coding Standards

Thin Minimal API handlers:

```text
HTTP
  ↓
validation/authentication
  ↓
service
  ↓
EF Core / Steam integration
  ↓
response DTO
```

- nullable reference types enabled
- async/await for I/O, propagate `CancellationToken`
- dependency injection, structured `ILogger` logging
- public API uses DTOs; never serialize EF entities directly
- standard HTTP status codes; `ProblemDetails` where appropriate
- never return stack traces or internal exceptions to clients
- for player-owned resources, prefer `404` when missing or not owned
- structured logging with useful context (`FeedbackId`, `PlayerId`, `SteamId`, `Status`, `RequestPath`); avoid logging full player feedback text at Information level unless there is a concrete operational need
- prefer readability over abstraction

Avoid: generic repository wrappers over EF Core, unnecessary interfaces, reflection-heavy frameworks, speculative event buses, premature architecture layers. Do not put substantial business logic directly in Minimal API handlers or Razor components.

## Data Rules

- Use EF Core + Npgsql. Use migrations for all persisted schema changes.
- When changing database models: update model/configuration → create migration → inspect generated migration → apply in development → build/test.
- Do not edit production schema manually, drop/recreate databases without explicit instruction, or silently delete migrations.
- Store timestamps in UTC.

## Testing

Security behavior must be tested. Use mocks/fakes; normal tests must not require real Steam credentials. Coverage checklist: `docs/testing.md`.

## Development Commands

```bash
dotnet restore
dotnet build
dotnet test
```

For schema changes:

```bash
dotnet ef migrations add <MigrationName>
dotnet ef database update
```

For Docker changes:

```bash
docker compose config
docker compose build
docker compose up -d
docker compose ps
```

Do not claim validation succeeded unless the command was actually run. If the environment prevents a command from running, state that explicitly.

## Agent Workflow

For every task:

1. inspect existing code first
2. preserve existing architecture unless change is justified
3. make the smallest coherent change
4. do not rewrite unrelated code
5. do not add speculative features
6. add/update tests for behavior changes
7. add EF migration when persisted schema changes
8. run build/tests
9. report changed files and validation

## Definition of Done

A feature is complete only when:

- architecture rules are respected
- SteamID cannot be spoofed through request data
- ownership/authorization is enforced
- public API uses DTOs
- database changes have migrations
- validation is present
- relevant tests pass
- `dotnet build` succeeds
- secrets are not committed
- no unrelated infrastructure/dependencies were introduced

Finish implementation tasks with:

```text
Changed
- ...

Validation
- dotnet build
- dotnet test

Notes
- migrations/config/manual steps, if any
```

Keep the system lightweight, secure, and focused on Steam game feedback.

## Agent skills

### Issue tracker

Issues live as local markdown files under `.scratch/<feature-slug>/` in this repo (no remote tracker). See `docs/agents/issue-tracker.md`.

### Triage labels

Uses the five canonical triage roles as label strings (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context layout: one `CONTEXT.md` at the repo root plus `docs/adr/`. See `docs/agents/domain.md`.
