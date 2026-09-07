# 05: Feedback detail and player comments

**What to build:** A Player can read one of their Feedback items together with all of its comments, and can add a comment to their own Feedback. Other Players' Feedback is indistinguishable from nonexistent — always 404.

**Blocked by:** 04.

**Status:** resolved

- [x] The owner gets full detail including comments; a foreign or missing id → 404
- [x] An owner comment persists with Author Type Player; commenting on another Player's Feedback → 404
- [x] Comment length limit enforced; per-Player rate limit protects the comment endpoint
- [x] Cross-ownership covered by tests: a Player cannot read or comment on another Player's Feedback

Parent spec: `.scratch/mvp-feedback-system/spec.md`
