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
/admin/games
/admin/credentials
```

Administrators are a single global identity system with **no roles**: an Admin sees every Game. There is no per-Game admin, and no page filters by who the Admin is.

## First run: no Games yet

Steam configuration lives in the database (ADR-0008), so a fresh deployment has nothing to authenticate against until an Admin creates a Game. While the `games` table is empty, an authenticated Admin is forced to the "add a game" page and cannot reach any other admin page.

- The allowed set is exactly: `/admin/login`, `/admin/logout`, the add-game page, and that page's save action.
- The guard must live in **both** the post-login redirect (the Identity page handler under `Pages/Admin/`) and the admin layout's initialization (`Components/Layout/AdminLayout.razor`). Blazor's interactive routing does not pass through ASP.NET Core middleware, so a middleware-only guard can be walked around by in-app navigation — and a layout-only guard leaves the initial navigation unguarded.
- Static assets must not be caught by the guard. It matches admin page routes, not the assets the pages need.
- Removing the last Game re-triggers the state.

## Games

`/admin/games` lists every Game and supports create, edit, activate/deactivate, and delete. Fields:

- name (display only; it does not participate in addressing)
- Steam AppID (**required**, unique, digits, at most 10 characters, not all zeros — it is the `/g/{appId}` path segment the player API is addressed by)
- identity (the Steam web-api ticket identity; default `feedback-api`)
- active flag
- credential selection (optionally none; a Game with no credential cannot log players in yet)

Rules:

- The AppID is validated by `GameValidation.ValidateSteamAppId` on create **and** on edit: a blank value is refused with "Steam AppID 不能为空：它同时是玩家 API 的路径段 /g/{appId}/api/...".
- A Game shown as **not fully configured** (`GameAdminView.IsConfigured == false`) is missing either the AppID or a credential, and the two are not the same failure. The list labels a blank AppID explicitly as **未填 AppID（不可寻址）**: no path resolves to it, so no client can reach it at all. A Game that has an AppID but no credential *is* addressable, and its player logins fail closed with `401 steam_unavailable`. A blank AppID can only occur on the placeholder Game the upgrade migration creates for pre-existing data (see `docs/domain-model.md`); the next save forces an Admin to supply it.
- **Changing an existing Game's AppID is allowed and warns**: clients only reach that Game when they run under the new AppID, and a typo here surfaces to players as `404 game_not_found`. There is no acknowledgement checkbox, and no alias or history table is kept — there is no slug to rename in the first place.
- Deleting a Game is refused when it has any Feedback or Players (`GameAdminService.DeleteAsync` returns `GameDeleteOutcome.HasData`). The UI tells the Admin to disable it instead, which stops new logins and keeps the history. There is no admin HTTP API here: the Blazor UI calls the service in-process, so this is a domain outcome rather than a status code.

## Credentials

`/admin/credentials` lists Steam credentials and supports create, edit, verify, and delete.

- `name` for the operator's own reference.
- The API key is **write-only**: it is never rendered back into the page — not in an input `value`, not in a hidden field, and not after a failed save that re-renders the form. Editing a credential without entering a new key keeps the stored one. There is no "show key" control of any kind.
- The key is never returned in any DTO and never logged. Credential-change log lines record the admin user id and the fact of the change, never the value.
- **Verify** is read-only: it asks Steam (`ISteamUser/GetPlayerSummaries`) and reports `200` = usable, `403` = invalid key. It never echoes the key, and it stores nothing.
- Deleting a credential that any Game references is refused (`CredentialDeleteOutcome.InUse`; the `Restrict` foreign key guarantees it); the UI points at repointing or disabling those Games instead.
- One credential can serve many Games, so rotating the shared publisher key is one edit here rather than N edits.

## Feedback list

Show at least:

- ID
- **game** (which Game the Feedback belongs to; disabled Games are still shown and are labelled as disabled)
- type
- title
- Steam display name
- Steam avatar when available
- status
- game version
- playtime
- created time

Support basic filtering by:

- **game** — URL query key `game`, carrying the Game's **Steam AppID** (the same identifier the `/g/{appId}` player path uses, and the value the filter dropdown submits). Empty or absent means **all games**, which is the default. A value that names no Game filters to nothing rather than erroring. The dropdown lists only Games that have an AppID (a Game without one is unaddressable and nothing can be filtered to it); a value from a hand-edited URL that matches no Game is kept as a "does not exist" option so the control does not claim to be showing "all games" while a filter is active.
- status
- type
- game version
- player — digits are matched as a SteamID64 **prefix** (so a half-remembered id still works); anything else is matched as a **case-insensitive nickname substring**. Both are combined with the other filters using AND.

Paginated via `page` / `pageSize` query parameters, default 50 per page, newest first. Missing or nonpositive `pageSize` uses 50; the maximum is 200. Filtering and page navigation preserve the normalized page size **and every active filter** — paging must never silently widen the query. All of these parameters flow through the single URL-building exit point (`AdminFeedbackQuery.ToQueryParameters`), including the new `game` key; there is no second place that assembles a query string.

The list also allows changing a Feedback's status inline. After a change the current query is re-run rather than the single row being patched locally, because with a status filter active that row is expected to leave (or enter) the list.

The SteamID64 in each row can be copied to the clipboard in one click. `navigator.clipboard` is only available in a secure context, so there must be a fallback that tells the admin to select the text manually instead of failing silently.

## Feedback detail

Show:

- **game** (name and AppID, linking to the list filtered to that game)
- title/content
- Steam name
- Steam avatar when available
- SteamID64
- Steam profile link
- a link to every Feedback from that Player (`?player={SteamId}`, which clears the other filters) — within that game, because a Player exists only inside a Game
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
