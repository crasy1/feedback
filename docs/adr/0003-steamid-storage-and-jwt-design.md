# ADR-0003: SteamID Storage and Player JWT Design

- Status: Accepted
- Date: 2026-09-07

## Context

ADR-0002 establishes Steam ticket verification as the player authentication mechanism. The trusted SteamID64 needs a stable database representation, and subsequent API calls need a token format.

## Decision

- Store SteamID64 as an opaque string (`varchar(20)`) with a unique index. One Steam account maps to exactly one Player; email is not required.
- Issue short-lived local access tokens with `sub = SteamID64`. Validate signature, issuer, audience, and expiration. Do not put sensitive information in JWT payloads.
- Do not add refresh tokens until there is a real requirement.

## Consequences

- Treating SteamID64 as an opaque string avoids numeric typing pitfalls across application, database, and token layers.
- The unique index on `Player.SteamId` enforces the one-account-one-player rule at the database level.
- Signing keys are read from secure configuration/environment variables, never committed.
- Without refresh tokens, players re-authenticate with a new Steam ticket when the access token expires; revisit this via a new ADR if session length becomes a real problem.
