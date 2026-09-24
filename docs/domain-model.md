# Domain Model

Keep the initial domain small.

## Player

Recommended fields:

```text
Id
SteamId
SteamName
AvatarUrl
CreatedAt
UpdatedAt
LastLoginAt
```

Rules:

- `SteamId` is unique.
- email is not required.
- one Steam account maps to one Player.
- a Player row is created on the **first successful Steam login** (`PlayerService.UpsertFromSteamLoginAsync`), not on the first Feedback; `LastLoginAt` records the most recent login. Submitting Feedback requires a login, so the row always exists by then.
- `SteamName` / `AvatarUrl` are refreshed on every login; when Steam cannot supply a new value the previous one is kept.

## Feedback

Recommended fields:

```text
Id
PlayerId
Type
Title
Content
Status
GameVersion
BuildNumber
OperatingSystem
Gpu
Cpu
MemoryTotalMb
PlaytimeMinutes
Locale
Map
Character
CreatedAt
UpdatedAt
```

Notes on the three newest fields:

- `Cpu` / `MemoryTotalMb` are **Hardware Info**: client-collected, advisory, untrusted, and bounded (see ADR-0006). Out-of-range values are discarded rather than rejected.
- `PlaytimeMinutes` is **Playtime**: the Player's accumulated minutes in this game, fetched by the server from Steam when the Feedback is submitted and snapshotted here. It is not a live value; when Steam cannot supply it the column is `null` (`0` means "owns it, never played").

Initial types:

```text
Bug
Suggestion
Other
```

Initial statuses:

```text
Open
InProgress
Resolved
Closed
```

Do not add a complicated workflow unless requested.

## FeedbackComment

Recommended fields:

```text
Id
FeedbackId
AuthorType
PlayerId?
AdminUserId?
Content
CreatedAt
UpdatedAt?
```

`AuthorType`:

```text
Player
Admin
```

A player may only view or comment on feedback owned by the authenticated Steam account.
