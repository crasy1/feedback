# 02: Domain model and persistence

**What to build:** The three core entities (Player, Feedback, Feedback Comment) persist in PostgreSQL with their spec'd fields, string-converted enums, and required indexes. Migrations apply automatically on application startup with an opt-out flag — starting the app against a fresh database produces the schema with no manual commands.

**Blocked by:** 01.

**Status:** ready-for-agent

- [ ] Player, Feedback, Feedback Comment persist with all fields from the domain model doc; types/statuses stored as strings; timestamps in UTC
- [ ] Unique index on Player SteamID; specified indexes on Feedback and Feedback Comment exist
- [ ] Initial migration generated and inspected; auto-apply on startup controlled by a configuration flag (default on)
- [ ] Integration test verifies the schema exists after startup on a fresh Testcontainers database (an entity round-trips through the real provider)
- [ ] One Steam account maps to exactly one Player — the unique constraint holds at the database level

Parent spec: `.scratch/mvp-feedback-system/spec.md`
