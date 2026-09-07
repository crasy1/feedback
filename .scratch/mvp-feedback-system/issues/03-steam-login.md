# 03: Steam login

**What to build:** A Player can authenticate from the game client: the client sends a Steam Ticket to the login endpoint, the server verifies it with Steam, upserts the Player (refreshing Steam name and avatar best effort), and returns a local Access Token valid for 24 hours. Invalid or unverifiable tickets never yield a token — verification fails closed. Authenticated requests use bearer auth.

**Blocked by:** 02.

**Status:** resolved

- [x] Valid canned Steam response → 200 with an Access Token whose subject is the SteamID64; a Player row is created with Steam name and avatar
- [x] Invalid ticket / response missing SteamID / Steam API failure / malformed Steam payload → 401, no token, no Player row (fail closed on every branch)
- [x] Profile-summary fetch failure does not fail the login; previously stored profile values are kept
- [x] Second login from the same Steam account updates the existing Player — no duplicate (unique index), LastLoginAt refreshed
- [x] A subsequent authenticated request validates the token (signature, issuer, audience, expiry) — bearer pipeline configured here; proven end-to-end by ticket 04's protected endpoints
- [x] IP-based rate limiting protects the login endpoint
- [x] Steam transport stubbed at the HTTP seam; no test requires real Steam credentials; secrets and tickets never logged

Parent spec: `.scratch/mvp-feedback-system/spec.md`
