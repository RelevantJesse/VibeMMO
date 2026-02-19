# Agent Prompts (Early Milestones)

These are copy/paste prompts intended for the first 2-4 weeks (M0-M8). Keep them small and adjust as the repo evolves.

## Assumptions

- Source of truth: `docs/01-scope.md`, `docs/02-architecture.md`, `docs/03-network-protocol.md`.
- Stack choices are locked for v0 unless explicitly changed:
  - Transport: LiteNetLib over UDP
  - Serialization: Protobuf (proto3)
  - Server: .NET 10
  - Data: SQLite (later milestone)
- No external services (no cloud, no hosted auth, no Kubernetes).
- Solo developer: prefer clarity and easy local debugging over perfection.

## Prompt: M0 Repo Skeleton (No Gameplay)

```text
Create the minimal repo skeleton for VibeMMO v0.

Constraints:
- Do not implement gameplay or networking.
- Keep changes small and conventional; no overengineering.
- .NET SDK target: net10.0.
- Follow the repo layout in README.md: client/, server/, shared/, docs/.

Deliverables:
- A .NET solution at repo root (name: VibeMMO.slnx).
- shared/ as a class library project (name: VibeMMO.Shared).
- server/ as a console/headless project (name: VibeMMO.Server) that references VibeMMO.Shared.
- client/ folder exists; do not attempt to generate a Godot project automatically. Add a short client/README.md with manual steps to create/open a Godot 4 C# project.
- A minimal build check in the root README.md (commands only; no CI required yet).

Acceptance criteria:
- `dotnet build` succeeds from repo root.
- Folder structure matches README.md.

Quick test plan:
1) Clean build from a fresh checkout.
2) Run `dotnet build` and confirm it passes.
```

## Prompt: M1 Protobuf Schemas + C# Codegen + Serialization Tests

```text
Set up the v0 network message schemas using Protobuf (proto3) and generate C# types in shared/.

Constraints:
- Use the minimal message set from docs/03-network-protocol.md (no new messages).
- Keep fields minimal; no "future-proofing" fields unless required.
- Use Protobuf code generation from .proto files (not hand-written DTOs).
- Do not implement server/client networking yet; just schemas + tests.

Deliverables:
- Add .proto file(s) under shared/ (e.g., shared/proto/v0.proto).
- Configure shared/ project to generate C# types at build (via Grpc.Tools or equivalent).
- Add a small test project that roundtrips 2-3 messages (Hello, Snapshot, Chat) using the generated types.
- Update docs/03-network-protocol.md only if you discover a mismatch between the doc and the schema.

Acceptance criteria:
- `dotnet build` and `dotnet test` pass from repo root.
- Protobuf field numbers are stable (documented in the .proto).

Quick test plan:
1) Run `dotnet test` and confirm roundtrip tests pass.
2) Inspect generated code is not committed if the repo pattern prefers generation at build; otherwise document the choice.
```

## Prompt: M2 Server Handshake (LiteNetLib + Protobuf)

```text
Implement only the server-side networking handshake for v0 using LiteNetLib and the existing Protobuf messages.

Constraints:
- No movement, no snapshots, no persistence.
- Server is authoritative; treat all client input as untrusted.
- Keep the networking layer simple; prioritize debuggability.

Deliverables:
- A headless server that listens on a configurable UDP port.
- Connection/session tracking (per NetPeer).
- Handle `C_Hello` and respond with:
  - `S_Welcome` if protocol_version matches.
  - `S_Reject` if protocol_version mismatches or name is invalid.
- Generate a reconnect_token (random bytes) and include it in S_Welcome.
- Basic input validation and caps:
  - max message size
  - max display name length (choose a small number, e.g. 16-24)
  - rate limit hello attempts per peer
- Minimal logging (connect/disconnect/hello result) without log spam.

Acceptance criteria:
- Starting the server prints a clear "listening on host:port" line.
- A client can connect and receive S_Welcome (can be verified with a tiny console client or a minimal harness).
- Protocol mismatch returns S_Reject and does not crash the server.

Quick test plan:
1) Run server.
2) Connect with a minimal client, send C_Hello, verify S_Welcome.
3) Send invalid protocol_version, verify S_Reject.
```

## Prompt: M3 Godot Client Connect UI (Handshake Only)

```text
Implement a minimal Godot 4 C# client that can connect to the server and complete the handshake.

Constraints:
- No movement, no snapshots, no chat yet.
- Keep UI minimal: address, name, connect/disconnect, status text.
- Use LiteNetLib for transport and the shared Protobuf messages.
- Do not add complex scene architecture; keep it easy to change.

Deliverables:
- A simple connection screen:
  - Server address input (default 127.0.0.1:7777)
  - Display name input
  - Connect/Disconnect button
  - Status label showing: Disconnected/Connecting/Connected + your_entity_id
- On connect: send C_Hello.
- On welcome: store reconnect_token and display entity id.
- On reject: show reason and return to disconnected state.

Acceptance criteria:
- With server running locally, the client can connect and display its entity id.
- Client can disconnect and reconnect without restarting either process.
- Mismatched protocol_version shows a readable error.

Quick test plan:
1) Start server.
2) Run client; connect; verify status changes to Connected and shows entity id.
3) Disconnect/reconnect 10 times; ensure no crashes or stuck states.
```

## Prompt: M6 Remote Players + Basic Chat (Vertical Slice)

```text
Implement milestone M6: two clients can connect at the same time, see each other move, and exchange global chat.

Constraints:
- Keep the server authoritative: clients never send positions, only intent (C_MoveInput) and chat text (C_ChatSend).
- Use the existing v0 Protobuf messages; do not change shared/proto unless required.
- Keep changes focused: this milestone is "prove the loop", not harden everything (hardening is M7).
- Keep UDP payload sizes sane; do not add large per-snapshot strings.

Deliverables (server):
- Implement spawn/despawn events:
  - When a player finishes handshake, broadcast S_EntitySpawn to all connected/handshaken clients.
  - When a new player connects, also send S_EntitySpawn for all existing players to the new client.
  - When a player disconnects (or times out), broadcast S_EntityDespawn to remaining clients.
- Implement basic chat path:
  - Handle C_ChatSend (text) and broadcast S_ChatBroadcast to all handshaken clients (including sender).
  - Basic validation only (non-empty, max length, reject control characters); detailed hardening comes next milestone.

Deliverables (client):
- Track remote entities:
  - Handle S_EntitySpawn / S_EntityDespawn to maintain a name map and entity liveness.
  - Render all entities seen in S_Snapshot, not just the local player (local can stay highlighted).
- Add a minimal chat UI:
  - A scrollable chat log (append-only).
  - A text input + send (Enter to send is ideal).
  - Display incoming S_ChatBroadcast as "[name]: text".

Optional carryover (recommended because M3 acceptance mentions ping):
- Implement periodic C_Ping and handle S_Pong to show a rolling ping estimate in the status label.

Acceptance criteria:
- Start server, run two clients, connect both: each client shows both players (local + remote).
- Move one client with WASD/arrows: the other client sees it move (and vice versa).
- Send chat from either client: both clients display it.
- Disconnect one client: the other client removes the remote player (despawn reflected).

Quick test plan:
1) `dotnet test VibeMMO.slnx`.
2) Manual smoke: server + two clients; connect; move; chat both directions; disconnect one; reconnect it.
3) If you add spawn/despawn logic, add a minimal server-side test with 2 clients that asserts:
   - Second client receives spawn of first.
   - First client receives spawn of second.
   - On disconnect, remaining client receives despawn.
```

## Prompt: M7 Chat Hardening

```text
Harden chat handling on the server (milestone M7) without changing core gameplay behavior.

Constraints:
- Do not change the protocol unless you have a strong reason.
- Keep the server resilient: malformed or spammy clients must not crash it or cause unbounded allocations.
- Prefer simple per-session state over global complex systems.

Deliverables (server):
- Enforce chat limits:
  - Max length (pick a small number, e.g. 120-200 chars).
  - Reject empty/whitespace-only messages.
  - Reject control characters; optionally normalize whitespace.
- Rate limit chat per session:
  - Example: allow N messages per 5 seconds with a small burst.
  - If rate limited, drop or kick (dropping is fine for v0; kicking is optional).
- Logging:
  - Log a warning when rejecting/throttling, but avoid log spam (e.g. sampled or per-window).

Deliverables (tests):
- Add server-side tests that send:
  - Oversized chat payloads (should be rejected without broadcasting).
  - Rapid-fire chat (should throttle/drop, server stays stable).

Acceptance criteria:
- Server enforces max length and rate limit for chat.
- Server rejects invalid chat payloads without crashing.

Quick test plan:
1) `dotnet test VibeMMO.slnx`.
2) Manual smoke: with two clients, try sending very long messages and spamming Enter; server stays responsive.
```

## Prompt: M8 SQLite Persistence (Minimal Player Record)

```text
Implement milestone M8: SQLite persistence of a minimal player record, restored via reconnect_token.

Constraints:
- SQLite only. No external services.
- Use parameterized SQL (no string interpolation for values).
- Keep the schema minimal and migration-free for v0 (create table if missing at startup).
- Do not persist chat history or inventories; only the minimal player record.

Deliverables (server):
- Add a small persistence layer (single class/file is fine) that:
  - Creates/opens a SQLite database at a configurable path (default: server working dir, e.g. vibemmo.db).
  - Stores: reconnect_token (BLOB, primary key), display_name, x, y, last_seen_utc.
  - On successful handshake:
    - If C_Hello includes a reconnect_token that exists, load the record and spawn at stored x/y.
    - Otherwise, generate a new reconnect_token and create a new record with default position.
  - On disconnect/timeout cleanup, save the latest x/y and last_seen_utc.
- Keep in-memory sessions authoritative; persistence is only a snapshot on disconnect and a restore on connect.

Deliverables (client):
- Already sends reconnect_token in C_Hello after the first welcome; keep this behavior.
- No UI changes required, but keep logs/status clear when reconnecting.

Deliverables (tests):
- Add a persistence test that:
  - Starts a server with a temp SQLite path.
  - Connects, moves to a distinct position, disconnects.
  - Reconnects using the previously issued reconnect_token.
  - Asserts the spawn position matches the saved position.

Acceptance criteria:
- On disconnect, server saves name + position + last seen.
- On reconnect with a valid token, player spawns at last saved position (token remains stable).

Quick test plan:
1) `dotnet test VibeMMO.slnx`.
2) Manual smoke: connect, move far, disconnect, reconnect; verify you reappear at the saved position.
3) Verify the SQLite file is created and updated (timestamp changes) after disconnect.
```
