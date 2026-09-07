# 07: Admin list and detail UI

**What to build:** The developer reviews Feedback in the admin UI: a paginated, filterable list; a detail page with everything needed for triage; replying and changing status. A replied Feedback is visible to the owning Player with the Admin's comment and the new status — the full loop works end to end.

**Blocked by:** 05, 06.

**Status:** resolved

- [x] The list shows the required columns, filters by status/type/game version, paginated 50 per page newest first
- [x] The detail page shows content, Steam name, SteamID64, profile link, environment metadata, comments, and status
- [x] Replying creates a comment with Author Type Admin; status changes persist; both operations are reachable only by an authenticated Admin
- [x] Service-level tests cover filtering, pagination, replying, and status change (auxiliary seam); admin pages still require authentication at the HTTP seam
- [x] The owning Player sees the Admin reply and updated status on their Feedback detail (full loop)
- [x] No edit or delete capability exists anywhere in v1

Parent spec: `.scratch/mvp-feedback-system/spec.md`
