# Security Design

Hard invariants (never trust client SteamID, fail closed, ownership enforcement, secrets handling) live in `AGENTS.md`. This document describes the design in detail.

Related decisions: [ADR-0002](adr/0002-dual-identity-and-steam-auth.md), [ADR-0003](adr/0003-steamid-storage-and-jwt-design.md).

## Two identity systems

### Players (Steam)

Client flow:

```text
GetAuthTicketForWebApi("feedback-api")
  ↓
POST /api/auth/steam
  ↓
server calls Steam AuthenticateUserTicket
  ↓
trusted SteamID64
  ↓
server issues local JWT
```

After authentication, normal game API calls use:

```http
Authorization: Bearer <jwt>
```

Do not call Steam again for every feedback request.

### Administrators

Administrators use normal web authentication:

- ASP.NET Core Identity
- secure HTTP-only cookie
- admin UI under `/admin`

A Steam player JWT must never authorize administrator operations.

## Ticket verification

The client obtains:

```text
GetAuthTicketForWebApi("feedback-api")
```

The server verifies it with Steam:

```text
ISteamUserAuth/AuthenticateUserTicket/v1
```

using server-side configuration:

- Publisher Web API Key
- Steam AppID
- ticket
- identity = `feedback-api`

A successful HTTP response alone is not enough. Validate the Steam response and require a valid SteamID64. Steam verification failures must fail closed.

## SteamID storage

Store SteamID64 as an opaque string, `varchar(20)`, with a unique index. See [ADR-0003](adr/0003-steamid-storage-and-jwt-design.md).

## SteamAuthService

Put Steam Web API logic in a dedicated service. Responsibilities:

```text
ticket
  ↓
Steam Web API
  ↓
validate response
  ↓
trusted Steam authentication result
```

`SteamAuthService` must not:

- issue JWTs
- write HTTP responses
- trust caller-provided SteamID

Use `IHttpClientFactory`. Configure timeout, API key, AppID and identity through strongly typed options. Do not create a new `HttpClient` for each request.

## JWT design

Use short-lived local player access tokens. Recommended subject:

```text
sub = SteamID64
```

Validate: signature, issuer, audience, expiration. Do not put sensitive information in JWT payloads. Read signing keys from secure configuration/environment variables. Do not add refresh tokens until there is a real requirement.

## Configuration

Prefer strongly typed options:

```text
SteamOptions
JwtOptions
```

Expected configuration includes values equivalent to:

```text
ConnectionStrings__DefaultConnection
Steam__ApiKey
Steam__AppId
Steam__Identity=feedback-api
Jwt__Issuer
Jwt__Audience
Jwt__SigningKey
```

Use safe placeholders in repository files. Provide `.env.example` when useful. Never put real production secrets into tracked configuration.

## Secrets

Never expose or commit:

- Steam Publisher Web API Key
- JWT signing key
- database password
- admin password

Never log:

- Steam auth tickets
- JWTs
- Steam API keys
- passwords
- Authorization headers
