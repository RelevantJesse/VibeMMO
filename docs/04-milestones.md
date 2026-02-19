# Milestones (v0, 2-4 Week Prototype)

Milestones are sized to 1-2 days each. Each ends with something you can run locally.

## M0 - Repo Skeleton (1 day)

Goal:
- Create solution layout (`client/`, `server/`, `shared/`, `docs/`) and basic build scripts.

Acceptance criteria:
- `dotnet build` succeeds for `server/` and `shared/` (placeholders allowed).
- Godot project opens and runs an empty scene.

Quick test plan:
- Build from a clean checkout; run Godot scene.

## M1 - Shared Protocol Scaffolding (1 day)

Goal:
- Define `.proto` files for the minimal message set and generate C# types in `shared/`.

Acceptance criteria:
- A roundtrip serialization test passes for 2-3 messages (hello, snapshot, chat).

Quick test plan:
- `dotnet test` on `shared/` (or a small protocol test project).

## M2 - Server: Networking + Handshake (1-2 days)

Goal:
- Implement UDP server loop, connection/session tracking, and `C_Hello` -> `S_Welcome`.

Acceptance criteria:
- Server accepts a connection and assigns `EntityId`.
- Protocol version mismatch returns `S_Reject`.

Quick test plan:
- Use a tiny test client (or the Godot client later) to connect and get welcome; verify logs.

## M3 - Client: Connect UI + Session (1-2 days)

Goal:
- Godot client can enter server address + name and connect/disconnect cleanly.

Acceptance criteria:
- Client reaches "Connected" state and receives `S_Welcome`.
- Client displays its `EntityId` and ping (placeholder ok).

Quick test plan:
- Start server; run client; connect/disconnect 10 times; confirm no server crashes.

## M4 - Server Tick + Authoritative Movement (1-2 days)

Goal:
- Implement fixed tick simulation and apply `C_MoveInput` to update player position.

Acceptance criteria:
- Server moves the player based on input at a consistent speed.
- Server clamps speed and ignores invalid inputs.

Quick test plan:
- Connect one client; hold movement; verify stable motion and no teleporting.

## M5 - Snapshots + Local Render (1-2 days)

Goal:
- Server broadcasts `S_Snapshot`; client renders the local player from snapshots.

Acceptance criteria:
- Client position reflects server snapshots.
- Basic smoothing (optional) prevents visible jitter.

Quick test plan:
- Add artificial latency/jitter (if available) and verify client stays usable.

## M6 - Remote Players + Basic Chat (Vertical Slice) (1-2 days)

Goal:
- Two clients connect simultaneously, see each other, observe movement updates, and exchange chat.

Acceptance criteria:
- Client A can see Client B moving and vice versa.
- Spawn/despawn events are reflected correctly.
- A chat message from either client appears on both clients (basic path proven; hardening can follow).

Quick test plan:
- Run server + two clients; move both; send chat both directions; disconnect one; verify despawn.

## M7 - Chat Hardening (1 day)

Goal:
- Add validation and abuse resistance to chat (limits, throttling, logging).

Acceptance criteria:
- Server enforces max length and rate limit.
- Server rejects/sanitizes obviously invalid payloads without crashing.

Quick test plan:
- Send 50 messages rapidly; verify server drops/throttles but remains stable.

## M8 - SQLite Persistence (1-2 days)

Goal:
- Persist minimal player record and restore on reconnect token.

Acceptance criteria:
- On disconnect, server saves name + position + last seen.
- On reconnect with valid token, player spawns at last saved position.

Quick test plan:
- Move to a distinct location; disconnect; reconnect; confirm position restored.

## M9 - Reconnect/Timeout Hardening (1 day)

Goal:
- Handle transient disconnects and timeouts without corrupting state.

Acceptance criteria:
- Server cleans up dead sessions after a timeout.
- Optional: short grace window for reconnect to reclaim session.

Quick test plan:
- Kill client process; wait; reconnect; confirm expected behavior and no ghost entities.

## M10 - Validation + Abuse Resistance (1 day)

Goal:
- Make the server hard to crash with malformed/abusive clients.

Acceptance criteria:
- Message size caps, parse error handling, and per-connection rate limits in place.
- Server logs actionable warnings without flooding.

Quick test plan:
- Fuzz-ish: send random bytes / oversized payloads; verify server stays up.

## M11 - Polish + "One-Command" Local Run (1 day)

Goal:
- Make the prototype easy to run and demo.

Acceptance criteria:
- README run steps are accurate.
- Basic debug overlay exists (players connected, tick rate, ping).

Quick test plan:
- Fresh machine style: delete build outputs, run steps, confirm demo works in < 5 minutes.

---

Next (post-v0):
- See `docs/08-milestones-v1.md` for a larger gameplay-focused milestone slice.
