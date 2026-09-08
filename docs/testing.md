# Testing

Security behavior must be tested. Use mocks/fakes; normal tests must not require real Steam credentials.

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
