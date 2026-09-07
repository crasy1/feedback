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
- status
- game version
- created time

Support basic filtering by:

- status
- type
- game version

Paginated via `page` / `pageSize` query parameters, default 50 per page, newest first.

## Feedback detail

Show:

- title/content
- Steam name
- SteamID64
- Steam profile link
- game version/build
- OS/GPU
- locale
- map/character
- comments
- status

Admins must be able to:

- reply
- change status

Admins cannot edit or delete any content in v1; moderation is out of scope.

Because the admin UI runs server-side, prefer calling application services directly instead of making an unnecessary HTTP request back into the same application.
