# Network Protocol (v0)

## Transport + Serialization

- Transport: UDP with two delivery modes (reliable ordered; unreliable sequenced).
- Implementation target: LiteNetLib (maps cleanly to reliable ordered vs sequenced delivery).
- Serialization: Protobuf (proto3).
- Framing: one protobuf packet per UDP datagram; `ClientPacket` and `ServerPacket` use `oneof` payloads.

## Messages (Minimal Set)

Client -> Server

| Message | Delivery | Used when | Fields |
| --- | --- | --- | --- |
| `C_Hello` | Reliable | First message after connect | `protocol_version`, `display_name`, `reconnect_token?` |
| `C_MoveInput` | Unreliable | Sent at input cadence | `seq`, `move_x`, `move_y` |
| `C_ChatSend` | Reliable | Send a chat line | `text` |
| `C_Ping` | Unreliable | RTT / keepalive | `ping_id`, `client_time_ms` |
| `C_Attack` | Reliable | Attempt melee hit | `target_entity_id` |
| `C_Pickup` | Reliable | Attempt to loot a drop | `loot_entity_id` |

Server -> Client

| Message | Delivery | Used when | Fields |
| --- | --- | --- | --- |
| `S_Welcome` | Reliable | Successful handshake | `protocol_version`, `your_entity_id`, `reconnect_token`, `tick_rate_hz`, `snapshot_rate_hz` |
| `S_Reject` | Reliable | Handshake failure | `reason` |
| `S_EntitySpawn` | Reliable | An entity becomes visible | `entity_id`, `display_name`, `x`, `y`, `entity_kind` |
| `S_EntityDespawn` | Reliable | A player entity is removed | `entity_id` |
| `S_Snapshot` | Unreliable | Periodic world state | `server_tick`, `last_processed_input_seq`, `your_coins`, `entities[]` (`entity_id`, `x`, `y`, `vx`, `vy`, `entity_kind`, `current_health`, `max_health`) |
| `S_ChatBroadcast` | Reliable | Global chat broadcast | `from_entity_id`, `from_name`, `text` |
| `S_Pong` | Unreliable | Ping response | `ping_id`, `server_time_ms` |
| `S_Kick` | Reliable | Server-initiated disconnect | `reason` |

## Versioning Strategy

- A single `protocol_version` integer (bumped manually for breaking changes).
- Protobuf evolution rules: never reuse field numbers; only add optional fields for non-breaking changes; deprecate instead of reusing.
- v0 policy: keep client/server in lockstep during development; reject mismatched versions early via `S_Reject`.

## Reliability Notes

- Must be reliable ordered: `C_Hello`, `S_Welcome`, `S_Reject`, `S_EntitySpawn`, `S_EntityDespawn`, `C_ChatSend`, `S_ChatBroadcast`, `C_Attack`, `C_Pickup`, `S_Kick`.
- May be unreliable (drop/replace ok): `C_MoveInput`, `S_Snapshot`, `C_Ping`, `S_Pong`.

## Rates + Payload Size

Starting rates (tunable):
- Server tick: 20 Hz
- Snapshot: 10 Hz
- Client move input: up to 20 Hz

Datagram sizing:
- Aim for < 1200 bytes per UDP packet to avoid fragmentation.
- v0 approach: send all players to all players; cap v0 concurrency (e.g., <= 32) to keep snapshots small.
- If snapshots grow: reduce snapshot rate, quantize positions, or add interest management later (not v0).
