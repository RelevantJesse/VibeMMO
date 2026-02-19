# Client Setup (Godot 4 C#)

## Prereqs

- Godot 4.x with .NET support
- .NET SDK 10.x installed

## Open Project

1. Open Godot Project Manager.
2. Import `client/VibeMMO.Client/project.godot`.
3. Open and run.

The default scene is `Main.tscn` and contains the current v0 client UI:
- Server address (`127.0.0.1:7777` by default)
- Display name
- Connect/Disconnect button
- Status label
- Chat log + input

After connecting, the client:
- Sends `C_MoveInput` at 20 Hz from WASD/arrow key input.
- Sends `C_Ping` periodically and tracks RTT from `S_Pong`.
- Renders local and remote entities from `S_Snapshot`.
- Handles `S_EntitySpawn` / `S_EntityDespawn`.
- Sends `C_Attack` to attack the nearest in-range NPC when `Space` is held.
- Sends `C_Pickup` to pick up the nearest in-range loot when `E` is held.
- Sends chat via `C_ChatSend` and displays `S_ChatBroadcast`.

The top-left debug overlay shows:
- local entity id
- local HP
- coins
- connected player count
- nearby NPC count
- nearby loot count
- current target entity id + HP
- server tick
- rolling ping (ms)
- local position
