# Milestones (v1 - Gameplay Loop)

These milestones build on the v0 "world seed" (connect/move/chat/persist) and aim for a first playable PvE loop:
explore a small map, fight simple NPCs, get loot, and persist basic progression.

Each milestone is still sized to 1-2 days, but this set is intentionally a larger slice (multiple milestones) so the project
starts feeling like a game instead of a networking demo.

## M12 - Shared Map + Server Collision (1-2 days)

Goal:
- Introduce a small "starter zone" map that both server and client load.
- Add server-authoritative collision against world bounds + simple obstacles.

Acceptance criteria:
- Server prevents players from leaving the world bounds.
- Server prevents players from moving through obstacles (axis-aligned rectangles is fine).
- Client renders the map bounds/obstacles (simple lines/rects are fine) so the collision is visible/understandable.

Quick test plan:
- Start server + one client; run into walls/bounds; confirm you cannot pass through.
- Start server + two clients; both see consistent positions when one tries to push through an obstacle.
- Add 1 server test that attempts to move through an obstacle and asserts the final position is clamped outside it.

## M13 - Entity Types + NPCs (1-2 days)

Goal:
- Add non-player entities (NPCs) simulated by the server and replicated to clients.

Acceptance criteria:
- Server spawns a small set of NPCs on startup (hard-coded or from map config).
- NPCs have a trivial server-side behavior (idle + random wander is enough).
- Clients render NPCs distinctly from players (color/size) and see them move from snapshots.
- Protocol stays explicit: `EntityState` / `S_EntitySpawn` includes an `entity_kind` (player/npc) or equivalent.

Quick test plan:
- Start server + client; confirm NPCs exist and move.
- Add a server test asserting NPCs appear in snapshots and are labeled as NPC kind.

## M14 - Combat v1 (Melee + HP + Death) (1-2 days)

Goal:
- Implement a minimal, server-authoritative combat loop against NPCs.

Acceptance criteria:
- Client can send an attack intent (targeted entity id is fine).
- Server validates: target exists, is in range, and attacker cooldown is respected.
- Server applies damage; NPC health reaches zero -> despawn; NPC respawns after a short delay.
- Snapshots (or explicit events) replicate health so clients can show HP (numbers or a small bar).

Quick test plan:
- Start server + client; attack an NPC until it dies; confirm despawn + respawn.
- Add server tests: out-of-range attack rejected, cooldown enforced, HP decreases, death triggers despawn.

## M15 - Loot Drops + Inventory v1 (Coins) (1-2 days)

Goal:
- Add a tiny progression hook: loot drops and a persisted currency counter.

Acceptance criteria:
- NPC death spawns a loot entity ("coin") at the death location.
- Player can pick up the loot (walk-over pickup or an explicit `C_Pickup` is fine; server authoritative either way).
- Player coin count is persisted in SQLite and restored on reconnect.
- Client HUD shows coin count.

Quick test plan:
- Kill NPC -> pick up coin -> disconnect -> reconnect -> confirm coin count persisted.
- Add server tests for pickup (only once, within range) and persistence restore.

## M16 - Interest Management (AOI) v1 (1-2 days)

Goal:
- Stop broadcasting everything to everyone; send only nearby entities per client.

Acceptance criteria:
- Server defines a view radius (tunable constant).
- For each client, snapshots include only entities within AOI (+ always include the local player).
- Spawn/despawn becomes AOI-aware: entities appear when entering AOI and despawn when leaving AOI.
- Snapshot packets remain under `MaxMessageBytes` in typical scenarios (log snapshot byte size when near the limit).

Quick test plan:
- Spawn lots of NPCs spread out; client should only see nearby ones.
- Walk across the map; entities pop in/out at the AOI boundary (no ghost entities).
- Add a server test for AOI filtering (two players far apart do not receive each other's spawns/snapshots).

## M17 - Gameplay UX + Debug Tools (1 day)

Goal:
- Make the new loop easy to test and iterate on.

Acceptance criteria:
- Client HUD shows: HP, coins, and selected target (minimal text overlay is fine).
- Server has lightweight debug hooks (console commands or compile-time toggles) to:
  - spawn N NPCs
  - clear NPCs
  - print current connected sessions/entities
- README includes a short "combat loop smoke test" section.

Quick test plan:
- Fresh run: start server + client; verify you can find an NPC, kill it, loot coins, and see HUD update.

