# ADR-0006: Server-Side Playtime Lookup and Untrusted Hardware Info

- Status: Accepted
- Date: 2026-09-24

## Context

Admins triaging Feedback could not tell a first-hour crash from a veteran's edge case, and hardware-correlated crashes had to be chased by asking the Player. Two pieces of data were missing: how long the reporting Player has played this game, and the machine's CPU and total memory (GPU already existed as a free-text field).

Two facts constrain the design:

- The Feedback Client ships a Steam-free core; the Steam binding belongs to the host. The host's pinned binding (Facepunch.Steamworks 2.5.2, pulled in transitively via `godottools`) exposes **no total-playtime API at all** — only workshop-item playtime tracking and parental duration-control figures. The client therefore cannot supply playtime even if we wanted it to.
- Anything the client asserts is untrusted (ADR-0002). Playtime is only useful for triage if it is accurate: a spoofable playtime column would be worse than no column.

## Decision

- **Playtime is server-derived.** On `POST /api/feedback` the server calls Steam's `IPlayerService/GetSingleGamePlaytime/v1` with the configured Publisher Web API Key, the configured AppId, and the SteamID taken from the authenticated principal (never from request data), then snapshots `playtime_forever` (minutes) onto that one Feedback.
- The lookup is **best effort under a 3-second budget**. Timeout, non-2xx, unparseable body, or a missing field all leave the column `null`. A playtime lookup never fails a submission and never surfaces an error to the Player — they would otherwise lose the Feedback text they wrote. The shared `HttpClient("Steam")` timeout is 10 seconds; the shorter budget is deliberate.
- `0` is a legitimate value ("owns it, never played") and is stored as `0`. Only an absent field is `null`.
- When the expected field is absent, the service logs a warning **including the bounded, credential-free response body**. The response contains no secrets, and without that line a Steam-side shape change would look exactly like a private profile: a silent `null`, which is this feature's normal failure mode.
- `GetOwnedGames` + `appids_filter` was implemented and **rejected by measurement**: for the same account at the same moment it returned `game_count: 0` while `GetSingleGamePlaytime` returned a non-zero playtime, because playtime in unreleased / Playtest apps is not part of the retail "owned games" list. Using it would have produced silent `null`s.
- **Hardware Info is client-reported and advisory.** `Cpu` and `MemoryTotalMb` are collected by the Feedback Client's Godot adapter (a host-supplied value always wins), stored as bounded nullable columns, and treated as untrusted context. They are never used for authorization and never identify a Player on their own. The addon's Godot-free core stays Godot-free: collection lives in the adapter.
- Out-of-range values of either client-collected field are **discarded, never rejected**. The Player neither typed them nor can fix them, so a `400` would only cost them the Feedback they wrote. Hand-typed metadata (title, content, game version, …) keeps its existing "too long → 400" behaviour.

## Consequences

- Submitting a Feedback costs one extra Steam round trip, bounded by the 3-second budget. It sits inside the existing per-Player write rate limit and requires an authenticated Player, so it cannot be driven anonymously.
- Playtime is unavailable for Players whose game details are private, for Players who do not own the app in the retail sense, and for Development debug-login SteamIDs. The admin UI shows `—` and the failure is logged with context.
- A Steam outage degrades one column, never the Feedback flow.
- Hardware Info remains spoofable by design. It is length/range bounded, displayed as context, and no decision is automated on it.
- The client-SDK limitation is the load-bearing reason for the server-side lookup: if a future host binding exposes total playtime for the app, this decision is worth revisiting.
