# Architecture (v0)

## Concrete Choices

- Client: Godot 4 (.NET/C#)
- Server: .NET 10 headless authoritative server
- Shared: C# shared library for protocol + shared types/constants
- Data: SQLite (single file)
- Transport: UDP via LiteNetLib (reliable + unreliable delivery modes)
- Serialization: Protobuf (proto3)

Why Protobuf:
- Strong, explicit schemas with safe evolution rules (add fields without breaking old clients).
- Widely supported and well-understood; easy to keep client/server in lockstep.
- Works with .NET via NuGet tooling (no external services required).

Why LiteNetLib:
- Pure C# and lightweight (easy to use from both Godot C# and .NET server).
- Supports reliable and unreliable delivery on UDP without building a custom protocol stack.

## High-Level Diagram

```
            +-------------------+
            |   Godot Client    |
            | - Input/UI        |
            | - Render          |
            | - Interpolation   |
            +---------+---------+
                      |
                      | UDP (reliable + unreliable)
                      |
            +---------v---------+         +------------------+
            | Authoritative     |         | SQLite           |
            | Server (.NET)     +---------> player records   |
            | - Sessions        |         | (v0)             |
            | - Tick simulation |         +------------------+
            | - Snapshots       |
            +---------+---------+
                      |
                      v
            +-------------------+
            | Shared (.NET)     |
            | - .proto schemas  |
            | - generated C#    |
            | - common types    |
            +-------------------+
```

## Responsibilities (Authority Model)

Client responsibilities:
- Gather player input and send intent to server (move direction/buttons).
- Render local + remote players using server snapshots (interpolate remote motion).
- UI: connect, name entry, chat box, debug overlay (ping, tick, id).

Server responsibilities:
- Own the truth: entity creation, positions, movement simulation.
- Validate and rate-limit: ignore invalid/too-frequent inputs, clamp speed.
- Send periodic snapshots with the current world state needed to render players.
- Persist minimal player record to SQLite on disconnect / periodic save.

Trust boundary:
- Everything from the client is untrusted.
- Only server writes to the authoritative world state and database.

## Server Tick Model

Recommended starting values (tunable):
- Fixed simulation tick: 20 Hz (50 ms)
- Snapshot rate: 10 Hz (100 ms)
- Client input send rate: up to 20 Hz (match server tick)

Loop shape (conceptual):
- Drain network events into per-connection input buffers.
- On each tick: apply latest input per player, step movement, resolve bounds.
- On snapshot interval: broadcast world snapshot to all connected clients.

## Movement + Snapshot Data Flow

1. Client captures input each frame (WASD/analog).
2. At input-send cadence, client sends `MoveInput` (direction + sequence number).
3. Server receives input and stores "latest intent" for that player.
4. Server tick applies intent to update velocity/position (server-controlled speed).
5. Server broadcasts `Snapshot` containing all player entity states.
6. Client renders local player from server position (optional smoothing) and remote players by interpolating between recent snapshots.

## Failure / Reconnect Assumptions

- Server detects disconnect (timeout) and despawns the entity.
- Server persists the player's last known position on disconnect (and/or periodic save).
- Server issues a reconnect token; if client reconnects with the token within a grace window, reclaim the same player record.
- If no valid token: treat as a new player record.

## Security Basics (v0 Posture)

- Input validation: clamp movement speed, ignore client positions, drop malformed/oversized messages, enforce per-connection rate limits.
- Abuse handling: chat length limits and spam throttling; best-effort connection limits per IP (NAT caveats).
- Persistence safety: parameterized SQL only; never interpolate strings into SQL.
- Non-goals: no anti-tamper/encryption/account security; assume clients can be modified and packets can be crafted.
