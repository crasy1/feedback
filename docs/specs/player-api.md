# Player API Spec (v1)

Keep v1 small.

## Steam login

```http
POST /api/auth/steam
```

Input:

```json
{
  "ticket": "..."
}
```

Return a local access token plus only client-required player data.

## Create feedback

```http
POST /api/feedback
Authorization: Bearer <jwt>
```

The SteamID comes from authentication, never the request body.

## List own feedback

```http
GET /api/feedback/mine
Authorization: Bearer <jwt>
```

## Read own feedback

```http
GET /api/feedback/{id}
Authorization: Bearer <jwt>
```

Prefer `404` when the requested feedback does not belong to the current player.

## Add player comment

```http
POST /api/feedback/{id}/comments
Authorization: Bearer <jwt>
```

Only the feedback owner may add a player comment.

Do not expose admin operations through player endpoints.

## Validation limits

```text
Title:        1–200 chars
Content:      1–10,000 chars
Comment:      1–5,000 chars
GameVersion:  <= 64 chars
BuildNumber:  <= 64 chars
Metadata:     bounded to sensible lengths
```

## Rate limiting

The API is internet-facing. Use ASP.NET Core rate limiting. At minimum protect:

```text
POST /api/auth/steam
POST /api/feedback
POST /api/feedback/{id}/comments
```

Prefer IP-based protection before authentication and SteamID-based limits after authentication.
