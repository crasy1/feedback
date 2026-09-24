# Game Feedback

Server-side feedback system for a Steam game: players submit feedback authenticated through Steam; admins review, reply, and track status. This glossary defines the canonical terms used across code, specs, and ADRs.

## Language

**Player**:
A person authenticated through their Steam account. One Steam account maps to exactly one Player; no email or separate registration exists.
_Avoid_: User, account, customer

**Admin**:
A developer operator who signs in with a password (cookie session) to review feedback and reply. Distinct identity system from Players; a Player token never grants Admin capabilities.
_Avoid_: Moderator, staff, manager

**Feedback**:
A report or suggestion a Player submits about the game, with a type (Bug / Suggestion / Other) and a status.
_Avoid_: Ticket, issue, post, report

**Feedback Comment**:
A message attached to a Feedback, written either by the Player who owns the Feedback or by an Admin.
_Avoid_: Reply, message, note

**Author Type**:
Which side wrote a Feedback Comment: the owning Player or an Admin.
_Avoid_: Role, poster

**Playtime**:
The time a Player has accumulated in the game, as reported by Steam and captured by the server at the moment a Feedback is submitted. A snapshot attached to one Feedback, not a live value; when Steam cannot supply it the value is left empty rather than estimated.
_Avoid_: play hours, session time, 在线时长 (the length of a single game session is a different measurement)

**Hardware Info**:
Client-reported details of the machine a Feedback was submitted from: CPU, GPU, and total memory. Advisory context only — it comes from the Feedback Client, is never trusted, is never used for authorization, and never identifies a Player on its own.
_Avoid_: system info, machine spec, 配置信息

**Steam Ticket**:
A short-lived token the game client obtains from Steam and sends to the server; the server verifies it with Steam to authenticate a Player.
_Avoid_: Login session, auth code

**Access Token**:
The server-issued token a Player's client includes on every API call after Steam verification.
_Avoid_: JWT (implementation detail), session token, API key

**Ownership**:
The invariant that a Player can only read and comment on their own Feedback, enforced by the server.
_Avoid_: Permission, visibility

**Feedback Client**:
The in-game Godot client that authenticates a Player and submits Feedback and Feedback Comments. It ships as an addon in this repository and is installed into a game project by copying it there.
_Avoid_: SDK, client library, plugin wrapper
