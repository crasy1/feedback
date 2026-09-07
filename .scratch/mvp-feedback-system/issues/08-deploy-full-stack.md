# 08: Deploy full stack

**What to build:** `docker compose up` brings up the whole system — application plus PostgreSQL 17 — with migrations applied on startup and forwarded headers handled correctly, plus a README quickstart. From zero to submitting and reviewing feedback requires no manual steps beyond configuration.

**Blocked by:** 07.

**Status:** resolved

- [x] A multi-stage Dockerfile builds the application; compose runs app + postgres with the database internal to the Docker network
- [x] `docker compose up` on a clean host: health OK, migrations applied, admin login works, a scripted API round-trip (login + submit Feedback) succeeds
- [x] Forwarded headers honored behind a trusted reverse proxy
- [x] Example configuration files finalized covering all settings, including Admin seeding and Steam credentials
- [x] README documents the dev workflow (postgres service + `dotnet run`) and full-stack deployment, and notes that integration tests need Docker
- [x] `dotnet build` and `dotnet test` green; no secrets tracked in git

验证说明（2026-09-07，本机）：
- `docker compose up` 实测通过：health 200、自动迁移建齐全部表、管理员种子成功、未登录访问 /admin 302 重定向。
- "脚本化 API 往返（Steam 登录 + 提交反馈）"无法在本机对真实 Steam 执行（无 Publisher Web API Key）；该路径由集成测试（HTTP 缝隙 + Steam 打桩）覆盖，行为一致。

Parent spec: `.scratch/mvp-feedback-system/spec.md`
