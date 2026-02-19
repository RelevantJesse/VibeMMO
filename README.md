# VibeMMO (v0 World Seed)

VibeMMO is a tiny, server-authoritative multiplayer "world seed" built to prove the core loop of an MMORPG: connect, spawn into a shared space, move around, see other players, chat, and persist a minimal player record. Graphics are intentionally primitive; the goal is a clean, expandable foundation that is feasible for a solo developer in 2-4 weeks.

## What "Done" Means (v0 Prototype)

- Run a headless .NET server locally.
- Run 2+ Godot clients locally and connect to the same server.
- On connect: player spawns (fresh default or last saved position) with a chosen display name.
- Movement is server-authoritative (client sends intent; server simulates and snapshots).
- Clients can see other connected players moving in real time.
- Global chat works (send/receive, basic spam limits).
- Basic persistence via SQLite (at minimum: player id/token, name, last position, last seen).
- Basic reconnect works (drop and reconnect to reclaim the same player record).

## Repo Layout

- `client/` Godot 4 (.NET) client project (C#)
- `server/` .NET 10 headless authoritative server (C#)
- `shared/` Shared .NET library (protocol schemas, shared types/constants)
- `docs/` Planning docs for v0 and beyond

## Local Run

Prereqs:
- Godot 4.x (.NET / C#)
- .NET SDK 10.x

1. Run the server:
- `dotnet run --project server/VibeMMO.Server/VibeMMO.Server.csproj`
  - Optional DB path override: `dotnet run --project server/VibeMMO.Server/VibeMMO.Server.csproj -- --db .\\_tmp\\vibemmo.db`

2. Run one or more clients:
- Open `client/VibeMMO.Client/project.godot` in Godot and press Play.
- For multiplayer smoke, run two client instances and connect both to `127.0.0.1:7777`.

3. In client:
- Enter server address + display name, then connect.
- Move with WASD/arrows.
- Attack nearest in-range NPC with `Space`.
- Pick up nearest in-range loot with `E`.
- Send chat with Enter or Send button.
- Disconnect/reconnect to verify token-based restore.

4. Debug overlay:
- While connected, the top-left overlay shows `Entity`, `HP`, `Coins`, `Players`, `NPCs`, `Loot`, `Target`, `Tick`, `Ping`, and local `Pos`.

## Build/Test Check

- `dotnet build VibeMMO.slnx`
- `dotnet test VibeMMO.slnx`

Default config (TBD):
- Server bind: `0.0.0.0:7777`
- Client target: `127.0.0.1:7777`

## Non-Goals (v0)

- No quests, crafting, economy, parties/guilds, or progression systems beyond basic coin pickup.
- No zones/interest management; v0 can broadcast all players to all players.
- No production auth/security (beyond basic validation/rate limiting).
- No cloud infrastructure, orchestration, or distributed systems.
- No serious performance optimization; correctness and iteration speed first.
