# 08: Deploy full stack

**What to build:** `docker compose up` brings up the whole system — application plus PostgreSQL 17 — with migrations applied on startup and forwarded headers handled correctly, plus a README quickstart. From zero to submitting and reviewing feedback requires no manual steps beyond configuration.

**Blocked by:** 07.

**Status:** ready-for-agent

- [ ] A multi-stage Dockerfile builds the application; compose runs app + postgres with the database internal to the Docker network
- [ ] `docker compose up` on a clean host: health OK, migrations applied, admin login works, a scripted API round-trip (login + submit Feedback) succeeds
- [ ] Forwarded headers honored behind a trusted reverse proxy
- [ ] Example configuration files finalized covering all settings, including Admin seeding and Steam credentials
- [ ] README documents the dev workflow (postgres service + `dotnet run`) and full-stack deployment, and notes that integration tests need Docker
- [ ] `dotnet build` and `dotnet test` green; no secrets tracked in git

Parent spec: `.scratch/mvp-feedback-system/spec.md`
