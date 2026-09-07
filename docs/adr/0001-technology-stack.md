# ADR-0001: Technology Stack

- Status: Accepted
- Date: 2026-09-07

## Context

The Steam Game Feedback System is a small, self-hosted service run alongside a Godot game client. It must authenticate players through Steam, store feedback in a database, and provide an admin UI for reviewing and replying. Operational simplicity matters more than scale, and the project is intentionally small — not a CRM, survey platform, or general-purpose help desk.

## Decision

Use the .NET ecosystem end to end:

- .NET 10, ASP.NET Core with Minimal APIs
- one application hosting both the game-facing API and a Blazor Web App (Interactive Server) admin UI
- Entity Framework Core with Npgsql / PostgreSQL
- ASP.NET Core Identity (or equivalent cookie authentication) for admins; JWT bearer authentication for the game client
- Docker / Docker Compose for deployment (app + postgres)

Explicitly rejected unless a demonstrated need appears:

- Node.js backend, React / Vue SPA
- Redis, Elasticsearch, RabbitMQ / Kafka
- MediatR, CQRS framework
- microservices, Kubernetes

## Consequences

- One deployable unit and one language across API, admin UI, and data access keeps maintenance cheap.
- Blazor Interactive Server lets the admin UI call application services directly instead of round-tripping HTTP back into the same application.
- Prefer built-in ASP.NET Core capabilities over NuGet packages; the simplest implementation that meets the requirement wins.
- The rejected-technologies list is a standing constraint: introducing any of them requires revisiting this ADR first.
