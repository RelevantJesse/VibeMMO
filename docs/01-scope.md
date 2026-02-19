# Scope (v0 World Seed)

## Summary

Build the smallest useful server-authoritative multiplayer slice: connect, spawn, move, see other players, chat, and persist basic player state in SQLite.

## In Scope (Must Have)

- Connect: client connects to server, negotiates a protocol version, and receives a welcome.
- Spawn: server assigns an `EntityId` and initial position (default spawn or loaded from persistence).
- Move: client sends movement intent (direction/buttons), and server simulates movement at a fixed tick with snapshots.
- Remote player visibility: server sends enough state to render other connected players (v0 broadcasts all players to all clients).
- Chat: global chat channel with basic validation (max length, rate limiting).
- Server authority: server is the source of truth for positions/entity existence and validates input rates/clamps movement speed.
- Basic persistence (SQLite): persist token, display name, last position, last seen; restore on reconnect (token-based).

## Out of Scope (Explicitly Not v0)

- Accounts/passwords, OAuth, email verification, 2FA.
- Encryption/TLS and full production security posture.
- Map tools, content pipeline, asset streaming.
- Physics-heavy gameplay, complex collision/skills.
- Party/guilds, trading, friends list.
- Server sharding, multi-region, scaling, orchestration.
- Admin dashboards (beyond basic console logs/commands).

## Later (After v0, No Detail Yet)

- Interest management (AOI), spatial partitioning.
- Zones/instances/region servers.
- Combat, stats, abilities, status effects.
- Inventory/equipment, loot, crafting.
- NPCs, AI behaviors, quests.
- Economy/trading, auction house.
- Persistence expansion (world objects, inventories, progression).
- Authentication, moderation tools, bans.

## Assumptions

- 2D top-down movement (simplifies implementation and debugging).
- Small concurrent player target for v0 (e.g., <= 32).
- Single "zone" / single server process.
- LAN/local testing is acceptable for v0; internet/NAT traversal can be later.
