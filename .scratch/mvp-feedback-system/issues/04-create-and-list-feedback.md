# 04: Create and list own Feedback

**What to build:** An authenticated Player can submit Feedback (type, title, content, optional environment metadata supplied by the client) and list their own most recent Feedback. Input limits are enforced; abuse is rate-limited per Player after authentication.

**Blocked by:** 03.

**Status:** resolved

- [x] Creating Feedback assigns ownership from the authenticated principal; a SteamID in the request body has no effect
- [x] Optional metadata fields accept client-supplied values bounded to spec lengths; oversized title/content → 400 with ProblemDetails
- [x] Invalid type → 400
- [x] Listing own Feedback returns only the caller's items, newest first, capped at the latest 100
- [x] Per-Player rate limit on creation → 429 when exceeded
- [x] Responses use DTOs, never serialized entities

Parent spec: `.scratch/mvp-feedback-system/spec.md`
