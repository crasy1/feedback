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
run `POST /api/auth/steam` first, paste the returned `accessToken` into **Authorize**, then call the
feedback endpoints. With `Steam:DebugSkipTicketValidation=true` (Development only) the login body can
be `{ "debugSteamId": "76561197960265729" }` instead of a real ticket — the SteamID64 must be 17 digits.
Start the service with `python scripts/run_local.py` (dev-only settings from `.env.local`, see the README).

**3. Smoke script** for a full end-to-end pass over every endpoint plus the security negatives:

```bash
docker compose up -d postgres
dotnet run --project src/GameFeedback          # port 5087, auto-migrates, seeds the admin
python scripts/smoke_player_api.py              # --base-url, --ticket, --include-rate-limit-probe
```

Against the local compose stack instead (app on `127.0.0.1:3000`, dev settings from `.env.local`), start it
with `docker compose --env-file .env.local up -d --build` and pass `--base-url http://127.0.0.1:3000`.

It covers, and asserts on: `/health`; anonymous 401 on all four player routes; debug or real-ticket
login; JWT `sub` equals the SteamID64 and carries no extra claims; create feedback (201 + `Location`);
`steamId`/`playerId` in the request body cannot change the owner (player B gets 404); type/enum bypass
and length validation (400, nothing persisted); `GET /api/feedback/mine` and detail; cross-player read
and comment 404; owner comment 201; `/admin` unreachable without an Identity cookie and with a player
JWT; the OpenAPI document; and (with `--include-rate-limit-probe`) 429 after 5 creates per 10 minutes.
Exit codes: `0` all passed, `1` a check failed, `2` the server is unreachable. Each run uses fresh
random SteamID64 values, so repeated runs do not exhaust the per-player rate-limit budget; pass fixed
`-SteamIdA`/`-SteamIdB` only if you accept that limit.

**4. The Godot client addon** has its own offline gate, independent of the server and of any game project:

```bash
python addons/gd_feedback/tests/verify.py       # identity/allowlist, engine-free core, clean-host fixtures
python addons/gd_feedback/tests/verify.py --godot-path <godot-mono.exe>   # + in-engine probe
```

It asserts, among other things, that the client's core contains no `Godot` reference, that local validation
runs before any request, that a missing Steam ticket fails closed, that the access token is reused and only
re-issued once after a 401, and that no log line ever contains the ticket or the access token. It needs no
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
signals, and that `System.Net.Http` performs requests inside the Godot runtime. It is not part of
`GameFeedback.slnx`; the addon's own `verify.py` remains the release gate.

## Steam authentication

Cover:

- valid Steam response returns trusted SteamID
- invalid ticket rejected
- missing SteamID rejected
- Steam API failure rejected
- secrets/tickets never returned to client

## Ownership

Cover:

- player lists own feedback
- player reads own feedback
- player cannot read another player's feedback
- player cannot comment on another player's feedback

## Admin

Cover:

- unauthenticated visitor cannot use admin
- admin can view feedback
- admin can reply
- admin can change status

## Validation

Cover:

- oversized title/content rejected
- invalid enum/status rejected
- rate-limited endpoints behave correctly
- login limits isolate distinct client IPs and normalize IPv4-mapped addresses
- forwarded client IP/protocol are accepted only from trusted proxies; forged headers cannot bypass login limits
- numeric and composite feedback type strings are rejected without saving feedback
- valid JSON with incorrect Steam ticket/profile shapes follows rejection or profile fallback semantics
