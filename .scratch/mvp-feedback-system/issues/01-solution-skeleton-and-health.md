# 01: Solution skeleton and health endpoint

**What to build:** The repository gains a working application skeleton: one ASP.NET Core application project and one test project under a solution at the repo root, with example configuration files and a compose file providing the PostgreSQL service. Running the app locally serves an unauthenticated `GET /health` returning 200. The HTTP test seam is established here: the whole application hosted in a `WebApplicationFactory` against a real PostgreSQL (Testcontainers), proven by a smoke test.

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

- [ ] `dotnet build` and `dotnet test` succeed on a fresh checkout (integration tests require Docker Desktop running)
- [ ] `GET /health` returns 200 with no authentication
- [ ] The integration harness boots the full application against a real PostgreSQL container and the smoke test passes
- [ ] Example configuration files (appsettings example, .env example) document every setting the app reads so far, with safe placeholders
- [ ] The compose file provides the PostgreSQL 17 service, exposed to localhost only
- [ ] Console structured logging configured; no secrets logged

Parent spec: `.scratch/mvp-feedback-system/spec.md`
