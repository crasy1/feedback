# Attachments (Future)

> **Status: out of the initial MVP.** Do not implement until requested. Do not introduce S3/MinIO/RustFS before they are requested.

When attachment support is added:

- whitelist allowed file types
- validate MIME type and extension
- generate server-side storage names
- prevent path traversal
- reject executable uploads
- limit per-file and total size

Suggested future limits:

```text
Screenshot <= 10 MB
Log        <= 5 MB
Total      <= 30 MB per feedback
```
