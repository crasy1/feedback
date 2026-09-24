# Admin UI Spec

Use Blazor Web App + Interactive Server.

Admin root:

```text
/admin
```

Minimum pages:

```text
/admin/feedback
/admin/feedback/{id}
```

## Feedback list

Show at least:

- ID
- type
- title
- Steam display name
- Steam avatar when available
- status
- game version
- playtime
- created time

Support basic filtering by:

- status
- type
- game version
- player — digits are matched as a SteamID64 **prefix** (so a half-remembered id still works); anything else is matched as a **case-insensitive nickname substring**. Both are combined with the other filters using AND.

Paginated via `page` / `pageSize` query parameters, default 50 per page, newest first. Missing or nonpositive `pageSize` uses 50; the maximum is 200. Filtering and page navigation preserve the normalized page size **and every active filter** — paging must never silently widen the query.

The list also allows changing a Feedback's status inline. After a change the current query is re-run rather than the single row being patched locally, because with a status filter active that row is expected to leave (or enter) the list.

The SteamID64 in each row can be copied to the clipboard in one click. `navigator.clipboard` is only available in a secure context, so there must be a fallback that tells the admin to select the text manually instead of failing silently.

## Feedback detail

Show:

- title/content
- Steam name
- Steam avatar when available
- SteamID64
- Steam profile link
- a link to every Feedback from that Player (`?player={SteamId}`, which clears the other filters)
- game version/build
- OS
- CPU
- GPU
- memory
- playtime
- locale
- map/character
- comments
- status

Missing environment or playtime values are shown as `—`. Playtime is stored in minutes and displayed in hours once it exceeds an hour; memory is stored in MB and displayed in GB once it exceeds a GB.

Admins must be able to:

- reply
- change status

Admins cannot edit or delete any content in v1; moderation is out of scope.

Because the admin UI runs server-side, prefer calling application services directly instead of making an unnecessary HTTP request back into the same application.
