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
Locale
Map
Character
CreatedAt
UpdatedAt
```

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
