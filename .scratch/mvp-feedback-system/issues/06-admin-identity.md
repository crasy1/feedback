# 06: Admin identity

**What to build:** The developer signs into the admin UI with a password and gets a secure cookie session. On first startup with no admins, an admin account is seeded from deployment configuration. `/admin` rejects everyone else — including a valid Player Access Token, keeping the two identity systems separate.

**Blocked by:** 02.

**Status:** resolved

- [x] A seeded admin can log in and out via the admin UI; the session uses a secure HTTP-only cookie
- [x] Seeding runs only when no admin account exists; existing installs are never modified
- [x] An unauthenticated request to `/admin` is redirected to the login page
- [x] A valid Player Access Token presented to `/admin` is rejected
- [x] The admin login page renders with Simplified Chinese text

Parent spec: `.scratch/mvp-feedback-system/spec.md`
