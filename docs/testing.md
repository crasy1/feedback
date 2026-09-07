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
