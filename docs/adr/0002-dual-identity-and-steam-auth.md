# ADR-0002: Dual Identity Systems and Steam Ticket Authentication

- Status: Accepted
- Date: 2026-09-07

## Context

Players must give feedback without registering an email address or creating a separate feedback-system account. The API is internet-facing, so any identity assertion coming from the game client cannot be trusted. Administrators, by contrast, are a small number of developers using a normal browser.

## Decision

Maintain two separate identity systems:

- **Players** authenticate with a Steam auth ticket. The Godot client calls `GetAuthTicketForWebApi("feedback-api")` and sends the ticket to `POST /api/auth/steam`. The server verifies it with Steam's `ISteamUserAuth/AuthenticateUserTicket/v1` using server-side configuration (Publisher Web API Key, Steam AppID, identity = `feedback-api`). On success it issues a local short-lived JWT. Steam is not called again for every feedback request.
- **Administrators** authenticate with ASP.NET Core Identity and a secure HTTP-only cookie, using the Blazor admin UI under `/admin`.

A Steam player JWT must never authorize administrator operations.

## Consequences

- No email collection and no password storage for players; identity strength is inherited from Steam.
- The SteamID can never be taken from request data (query/body/headers); it derives from the server-verified ticket, and afterwards from the authenticated principal.
- Verification fails closed: a successful HTTP response from Steam without a valid SteamID64 in the payload is not authentication, and unavailable Steam verification means no authentication.
- Subsequent feedback requests skip the Steam round trip, at the cost of trusting our own signed JWTs for ownership checks.
