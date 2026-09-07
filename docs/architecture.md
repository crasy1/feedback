# Architecture & Deployment

## Application shape

Prefer one ASP.NET Core application:

```text
GameFeedback/
├── Api/
│   ├── AuthEndpoints.cs
│   ├── FeedbackEndpoints.cs
│   └── CommentEndpoints.cs
├── Components/
│   └── Pages/
│       └── Admin/
├── Contracts/
│   ├── Requests/
│   └── Responses/
├── Data/
│   ├── AppDbContext.cs
│   ├── Configurations/
│   └── Migrations/
├── Domain/
│   ├── Player.cs
│   ├── Feedback.cs
│   ├── FeedbackComment.cs
│   └── FeedbackStatus.cs
├── Services/
│   ├── SteamAuthService.cs
│   ├── TokenService.cs
│   └── FeedbackService.cs
├── Security/
├── Program.cs
└── appsettings.json
```

## Module boundaries

- `Api`: HTTP endpoint definitions.
- `Contracts`: public request/response DTOs.
- `Domain`: domain and persisted models.
- `Services`: application/integration logic.
- `Data`: EF Core/database code.
- `Components`: Blazor admin UI.

Do not put substantial business logic directly in Minimal API handlers or Razor components.

## Database

Use EF Core + Npgsql. Migrations for all persisted schema changes. Migrations apply automatically on application startup (`Database__AutoMigrate`, default on). Store timestamps in UTC.

Minimum useful indexes:

```text
Player.SteamId UNIQUE
Feedback.PlayerId
Feedback.Status
Feedback.CreatedAt
Feedback.GameVersion
FeedbackComment.FeedbackId
```

## Deployment

Target deployment:

```text
docker compose
├── app
└── postgres
```

- Keep PostgreSQL internal to the Docker network; the `postgres:17` image is exposed to localhost only for development.
- The ASP.NET container listens on a predictable port such as `8080`.
- The public edge may be Cloudflare Tunnel, Nginx, or Caddy.
- Support forwarded headers correctly when behind a trusted reverse proxy.
- Do not add Redis, object storage, workers, or other containers until a feature actually requires them.
