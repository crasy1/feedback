# Testing

Security behavior must be tested. Use mocks/fakes; normal tests must not require real Steam credentials.

## How to test the API

Three levels, cheapest first.

**1. Automated tests** (Steam stubbed at the HTTP transport layer, real PostgreSQL via Testcontainers):

```bash
dotnet test                                    # everything (~50 cases)
dotnet test --filter "FullyQualifiedName~SteamLoginTests"   # one suite
```

Requires Docker Desktop running. No real Steam credentials needed.

**2. Swagger UI** for one-off manual calls (`http://localhost:5087/swagger`, Development only):
every player route is under `/g/{appId}` (see `docs/specs/player-api.md`), so run
`POST /g/{appId}/api/auth/steam` first, paste the returned `accessToken` into **Authorize**, then call the
feedback endpoints. The Development database must already contain a Game whose `SteamAppId` is the value you
put in that path segment; create it in `/admin/games` (login with the seeded admin). With
`Steam:DebugSkipTicketValidation=true` (Development only) the login body can
be `{ "debugSteamId": "76561197960265729" }` instead of a real ticket — the SteamID64 must be 17 digits.
Start the service with `python scripts/run_local.py` (dev-only settings from `.env.local`, see the README).

**3. Smoke script** for a full end-to-end pass over every endpoint plus the security negatives:

```bash
docker compose up -d postgres
dotnet run --project src/GameFeedback          # port 5087, auto-migrates, seeds the admin
python scripts/smoke_player_api.py --app-id <appId>   # --base-url, --ticket, --include-rate-limit-probe
```

The script needs an AppID because there is no root player API and no default Game: it prepends
`/g/<appId>` to every player route, and its first-run check is that the root paths answer
`404 game_required`.

Against the local compose stack instead (app on `127.0.0.1:3000`, dev settings from `.env.local`), start it
with `docker compose --env-file .env.local up -d --build` and pass `--base-url http://127.0.0.1:3000`.

It covers, and asserts on: `/health`; anonymous 401 on all four player routes; the removed root paths
answering `404 game_required`; an AppID that names no Game answering `404 game_not_found` (a non-numeric
path segment included) and a disabled Game `403 game_disabled`; debug or real-ticket login; JWT `sub` equals
the SteamID64 and the token carries a
`game` claim naming the Game that served it (and no other extra claims); create feedback (201 + a `Location`
of `/g/{appId}/api/feedback/{id}`);
`steamId`/`playerId` in the request body cannot change the owner (player B gets 404); a token minted for one
Game rejected on another Game's path (401); type/enum bypass and length validation (400, nothing persisted);
`GET /g/{appId}/api/feedback/mine` and detail; cross-player read and comment 404; owner comment 201; `/admin`
unreachable without an Identity cookie and with a player JWT; the OpenAPI document; and (with
`--include-rate-limit-probe`) 429 after 5 creates per 10 minutes **within one Game**. Exit codes: `0` all
passed, `1` a check failed, `2` the server is unreachable (the script also exits `2` with a clear message
when `--app-id` is missing or is not all digits). Each run uses fresh random SteamID64 values, so
repeated runs do not exhaust the per-(Game, Player) rate-limit budget; pass fixed `-SteamIdA`/`-SteamIdB`
only if you accept that limit.

**4. The Godot client addon** has its own offline gate, independent of the server and of any game project:

```bash
python addons/gd_feedback/tests/verify.py       # identity/allowlist, engine-free core, clean-host fixtures
python addons/gd_feedback/tests/verify.py --godot-path <godot-mono.exe>   # + in-engine probe
```

It asserts, among other things, that the client's core contains no `Godot` reference, that local validation
runs before any request, that a missing Steam ticket fails closed, that the access token is reused and only
re-issued once after a 401, that a host-provided AppID completes the `/g/{appId}` path
(`/g/1910980/api/auth/steam`), that a host value which is not a numeric AppID (digits only, at most 10
characters — a slug, by mistake) fails closed with `invalid_configuration` and sends no request, and that no
log line ever contains the ticket or the access token. It also pins the **AppID source order**: a
`SteamAppId` supplied by the host's config resource (`feedback_config.tres`, or its short alias
`feedback.tres`) wins over the injected `IGameAppIdProvider`,
and a blank configured value falls back to the provider rather than meaning "empty AppID". The in-engine
probe additionally asserts both sources in the engine: a bad configured value fails locally with
`invalid_configuration` (where an unread field would have produced `transport_failed`), and so does the same
bad value arriving only from the provider. It needs no
network: fixtures are restored from the local NuGet cache only. See
[../addons/gd_feedback/README.md](../addons/gd_feedback/README.md).

**5. The committed Godot host project** ([../tests/godot-feedback-host/](../tests/godot-feedback-host/README.md))
is a complete Godot 4.7.2 .NET project with the addon installed the way a game installs it. Open it in Godot
to drive the client by hand (debug login, a real Steam ticket issued through the vendored `addons/steamworks`,
or a pasted ticket — then submit/list/detail/comment and watch the
signals and error codes), or run it headless — no network, no Steam, no server required:

```bash
godot-mono.console.exe --headless --path tests/godot-feedback-host res://Main.tscn -- --lab-selfcheck
python tests/godot-feedback-host/tools/sync_addon.py -Check   # the installed copy still matches the addon
```

Use it to check the things a compile cannot: that the main-thread hop through `CallDeferred` really emits the
signals, that `System.Net.Http` performs requests inside the Godot runtime, and that the host's
`feedback.tres` is really loaded and its `SteamAppId` really drives the `/g/{appId}` segment. It is
not part of `GameFeedback.slnx`; the addon's own `verify.py` remains the release gate.

## Steam authentication

Cover:

- valid Steam response returns trusted SteamID
- invalid ticket rejected
- missing SteamID rejected
- Steam API failure rejected
- secrets/tickets never returned to client
- ticket verification is attempted with the **resolved Game's** `SteamAppId` and identity, never with a value from the request

The integration fixture no longer sets `Steam:AppId` through configuration (there is no such setting any
more). It seeds a **Game row and a credential row** per test scope, and `FakeSteamHandler` — which throws on
any unstubbed Steam call — must be able to answer per Game, so a two-Game test can prove that each login
reaches Steam with the right AppId.

## Multi-game scoping

Cover:

- addressing by Steam AppID end to end: the same AppID in `/g/{appId}` resolves to the Game whose
  `games.SteamAppId` matches, and the token's `game` claim is that Game's numeric id
- a token minted for one Game is rejected (`401 game_mismatch`) on another Game's path
- a token issued before the upgrade (no `game` claim) is rejected, and the same client recovers by
  re-authenticating once
- the removed root paths answer `404` with `code = "game_required"` — including `/api/auth/steam`,
  `/api/feedback`, `/api/feedback/mine`, `/api/feedback/{id}`, `/api/feedback/{id}/comments`
- an AppID that names no Game answers `404 game_not_found`, and so does a path segment that is not all
  digits (the resolver refuses it before querying)
- a Game row with a `NULL` `SteamAppId` — the placeholder the upgrade migration creates — is
  **unreachable**: no path resolves to it, so the client gets `404 game_not_found` and never a login
- a disabled Game answers `403 game_disabled` while its Feedback rows remain untouched
- a Game that has an AppID but no credential answers `401 steam_unavailable` on login (the missing-AppID
  half of that condition is unreachable and must not be covered as if it were)
- a Feedback id from another Game reads as `404`, and the same Steam account in two Games is two Players
  with independent Feedback lists
- playtime is fetched with the resolved Game's AppId and the same SteamID from the authenticated principal
- the write and comment rate limits are partitioned per `{appId}:{steamId}`, so exhausting one Game's
  budget does not limit the same account in another Game

## Steam configuration and credentials

Cover:

- credential encryption round-trip: a key stored through the admin path is usable for a Steam call after a
  fresh service scope reads it back, and the plaintext is not present in the database
- the API key never appears in any admin response body, any rendered admin page, any DTO, or any log line —
  including after a failed save that re-renders the form
- a credential that fails to decrypt produces its own error and log line, and the affected Game answers
  `401 credential_unreadable` rather than `steam_unavailable`
- deleting a Game that has any Feedback or Player reports `GameDeleteOutcome.HasData`, and deleting a credential
  that any Game references reports `CredentialDeleteOutcome.InUse` — in both cases the rows survive, and the
  database-level `Restrict` foreign keys are what actually guarantee it (there is no admin HTTP API, so these are
  domain outcomes the Blazor admin UI renders, not status codes)
- changing an existing Game's AppID changes the path that works immediately, and the old AppID answers
  `404 game_not_found`
- with the `games` table empty, an authenticated admin is redirected to the add-game page and cannot reach
  any other admin page; `/admin/login`, `/admin/logout`, the add-game page and its save action stay
  reachable, and static assets are not caught by the guard

## Ownership

Cover:

- player lists own feedback
- player reads own feedback
- player cannot read another player's feedback
- player cannot comment on another player's feedback
- the same Steam account in a different Game cannot read this Game's feedback

## Admin

Cover:

- unauthenticated visitor cannot use admin
- admin can view feedback
- admin can reply
- admin can change status
- admin can create, edit, activate/deactivate and (when empty) delete a Game
- admin can create, edit, verify and (when unreferenced) delete a credential
- the feedback list shows a game column and filters by game, the `game` query value being the Game's
  **AppID** (and the filter dropdown carrying AppIDs), and paging preserves the game filter
- the empty-games guard is enforced in both the post-login redirect and the admin layout

## Validation

Cover:

- oversized title/content rejected
- invalid enum/status rejected
- rate-limited endpoints behave correctly
- login limits isolate distinct client IPs and normalize IPv4-mapped addresses
- forwarded client IP/protocol are accepted only from trusted proxies; forged headers cannot bypass login limits
- numeric and composite feedback type strings are rejected without saving feedback
- valid JSON with incorrect Steam ticket/profile shapes follows rejection or profile fallback semantics
- a blank, non-numeric, all-zero or over-long Steam AppID is rejected when creating a Game, and a duplicate
  `SteamAppId` is refused
