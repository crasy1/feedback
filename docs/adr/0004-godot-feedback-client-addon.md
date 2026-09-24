# ADR-0004: Ship the Player Client as a Godot Addon in This Repository

- Status: Accepted
- Date: 2026-09-24

## Context

Players reach this system from inside a game, but until now the repository held only the server side of the contract: the endpoints in [docs/specs/player-api.md](../specs/player-api.md) and the Steam ticket identity in ADR-0002. Every game that wants to submit feedback would otherwise re-implement ticket handling, token caching, validation limits and error mapping, and each re-implementation would drift from the server contract.

A client library can be shipped in more than one shape. It could live inside each game (fast to write, but the server contract and its client slowly diverge, and there is nowhere to run its tests), or it could be a separate repository (independent, but nothing in it would be verifiable against the API it targets), or it could live here, next to the contract it implements.

The engine also constrains how a client may be structured. Godot documents `CallDeferred`/`SetDeferred` and awaiting an engine signal as the only ways to get back to the main thread, and documents nothing about a C# `SynchronizationContext` or which thread an `async` continuation resumes on. GDScript can await a signal but cannot await a C# `Task`. Steam bindings for .NET are platform-specific and, in the projects that use them, optional — a client that hard-depends on one stops being portable.

## Decision

Ship the player client in this repository as a Godot .NET addon at `addons/gd_feedback/`, installed into a game by copying it to that project's `addons/` folder.

- **Engine-free core, thin adapter.** `FeedbackRuntime`, `FeedbackContracts` and `FeedbackAbstractions` contain no `Godot` reference at all; they own HTTP, JSON, local validation, token caching, retry and error mapping. `FeedbackClient` (a `Node`) only reads configuration, delegates to the core, and marshals results back to the main thread with `CallDeferred` before emitting signals. Nothing in the addon touches the scene tree after an `await`.
- **No Steam dependency.** The addon depends on no other addon and no Steam binding (`dependencies: {}`). The host injects an `ITicketProvider`; the shipped default cannot issue a ticket, so a missing implementation fails closed with `ticket_unavailable` instead of degrading.
- **C# plugin entry.** `plugin.cfg` points at `GdFeedbackPlugin.cs` (`[Tool]`, `#if TOOLS`), which registers a `FeedbackClient` custom type and registers/removes its own Autoload on enable/disable. No editor dock: the 4.7 dock API (`EditorDock`) is still Experimental and this addon has no editor UI to offer.
- **Secrets stay out of logs, and tokens stay off disk by default.** Ticket and access token never reach the logger; the default token store is in-memory, and persistence happens only through a host-injected store when `CacheAccessToken` is enabled.
- **Named `PlayerFeedback*`, not `Feedback*`.** Consuming games already use "feedback" for combat presentation (damage popups), so the client's own vocabulary is prefixed to keep the two apart.
- **429 is reported, not retried.** A transport failure is retried once, but a rate-limited response is returned immediately as `rate_limited` with `retryable = true`: the server's window is measured in minutes, so an automatic retry inside the same window would only spend another request against the same quota. This is deliberately narrower than "retry once on any transient failure".
- **The client does not mirror server policy.** The ticket length check in the client is a *sanity guard*, deliberately looser than the server's limit: the server owns the policy (8192 hex characters, because a Steam web API ticket is at most 2560 bytes and hex doubles it) while the client only rejects values that cannot be a ticket at all. The first version copied the server's 4096 and therefore rejected every real ticket before it left the machine — a real Steam ticket is 5120 characters, exactly the case a "helpful" client-side guard gets wrong while looking harmless.
- **Verification is offline and belongs to the addon.** `tests/verify.py` checks identity and the manifest allowlist, proves the core is engine-free, compiles and exercises the core in a plain .NET clean-host fixture (no Godot, no network), compiles the whole addon inside a `Godot.NET.Sdk` host fixture with `TreatWarningsAsErrors=true`, and optionally runs an in-engine probe. `tools/package.py` builds the archive from the same allowlist it validates.
- **A committed Godot host project carries the manual and in-engine checks.** `tests/godot-feedback-host/` is a complete Godot 4.7.2 .NET project with the addon installed under `res://addons/gd_feedback/` exactly as a game installs it, plus a lab scene that drives the client interactively and a headless self-check (`--lab-selfcheck`). It covers what a compile cannot: that `CallDeferred` really lands the signals on the main thread, and that `System.Net.Http` performs requests inside the Godot runtime. `tools/sync_addon.py --check` keeps its installed copy from drifting; the host project is deliberately not part of `GameFeedback.slnx`.

## Consequences

- The server contract and its client live together: a change to `docs/specs/player-api.md` can now update the client and its tests in the same change.
- The addon must stay free of host-specific code and of secrets; the price is an injected ticket provider and a copy-based install rather than a shared submodule.
- The manifest pins the verified combination (Godot 4.7.2, `net10.0`), while the code deliberately sticks to the .NET 8 BCL surface so it still compiles in older hosts; a game that needs a newer API must ask for it explicitly.
- The in-engine probe exists because a compile cannot catch main-thread or `[GlobalClass]` mistakes. It already earned its place: it caught an editor-singleton access that broke any run using an editor build outside the editor.
- Copying the addon into a game means two copies exist; the manifest, the matching version in `plugin.cfg`/`addon.manifest.json`/`README.md`, and the offline verification gate are what keep the copy honest.
