using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Google.Protobuf;
using LiteNetLib;
using LiteNetLib.Utils;
using VibeMMO.Shared.Protocol.V0;

namespace VibeMMO.Server;

internal static class Program
{
    private static int Main(string[] args)
    {
        var port = ServerConfig.ParsePort(args);
        if (port is null)
        {
            Console.Error.WriteLine("Invalid port. Use --port <1-65535> or set VIBEMMO_PORT.");
            return 1;
        }

        var databasePath = ServerConfig.ParseDatabasePath(args);
        var app = new HandshakeServer(port.Value, databasePath);
        return app.Run();
    }
}

internal static class ServerConfig
{
    private const int DefaultPort = 7777;
    private const string DefaultDatabasePath = "vibemmo.db";

    public static int? ParsePort(string[] args)
    {
        var fromArgs = ParsePortFromArgs(args);
        if (fromArgs is not null)
        {
            return fromArgs;
        }

        var fromEnv = Environment.GetEnvironmentVariable("VIBEMMO_PORT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return TryParsePort(fromEnv);
        }

        return DefaultPort;
    }

    public static string ParseDatabasePath(string[] args)
    {
        var fromArgs = ParseDatabasePathFromArgs(args);
        if (!string.IsNullOrWhiteSpace(fromArgs))
        {
            return fromArgs;
        }

        var fromEnv = Environment.GetEnvironmentVariable("VIBEMMO_DB_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        return DefaultDatabasePath;
    }

    private static int? ParsePortFromArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--port", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    return null;
                }

                return TryParsePort(args[i + 1]);
            }

            const string Prefix = "--port=";
            if (arg.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return TryParsePort(arg[Prefix.Length..]);
            }
        }

        return null;
    }

    private static int? TryParsePort(string value)
    {
        return int.TryParse(value, out var parsed) && parsed is > 0 and <= 65535
            ? parsed
            : null;
    }

    private static string? ParseDatabasePathFromArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--db", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    return null;
                }

                return args[i + 1];
            }

            const string Prefix = "--db=";
            if (arg.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[Prefix.Length..];
            }
        }

        return null;
    }
}

public sealed class HandshakeServer : IDisposable
{
    private const uint ProtocolVersion = 1;
    private const uint TickRateHz = 20;
    private const uint SnapshotRateHz = 10;
    private const int MaxMessageBytes = 1200;
    private const int MaxDisplayNameLength = 24;
    private const int MaxChatLength = 160;
    private const int MaxHelloAttemptsPerWindow = 4;
    private const int MaxChatMessagesPerWindow = 8;
    private const int MaxPacketsPerWindow = 120;
    private const int MaxMalformedPacketsPerWindow = 8;
    private const int ReconnectTokenBytes = 16;
    private const int DefaultDisconnectTimeoutMs = 10000;
    private const float MoveSpeedUnitsPerSecond = 5.0f;
    private const float NpcMoveSpeedUnitsPerSecond = 1.5f;
    private const uint PlayerMaxHealth = 100;
    private const uint NpcMaxHealth = 100;
    private const uint AttackDamage = 25;
    private const float AttackRangeUnits = 2.0f;
    private const uint AttackCooldownTicks = 8;
    private const float PickupRangeUnits = 1.25f;
    private const uint NpcRespawnDelayTicks = 40;
    private const float ViewRadiusUnits = 8.0f;
    private const int InitialNpcCount = 4;
    private const int NpcDirectionChangeTicks = 40;
    private const double MaxSimulationCatchUpSeconds = 0.25;
    private static readonly TimeSpan ReconnectGraceWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HelloRateWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ChatRateWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PacketRateWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MalformedPacketWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ChatRejectLogCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TransportRejectLogCooldown = TimeSpan.FromSeconds(2);
    private static readonly double SecondsPerTick = 1.0 / TickRateHz;
    private static readonly double SecondsPerSnapshot = 1.0 / SnapshotRateHz;

    private readonly int _port;
    private readonly EventBasedNetListener _listener = new();
    private readonly Dictionary<int, PeerSession> _sessions = new();
    private readonly Dictionary<uint, NpcEntity> _npcs = new();
    private readonly Dictionary<uint, LootEntity> _lootDrops = new();
    private readonly Dictionary<string, PendingReconnectSession> _pendingReconnects = new();
    private readonly NetManager _netManager;
    private readonly SqlitePlayerStore _playerStore;
    private readonly TimeSpan _reconnectGraceWindow;

    private volatile bool _isRunning;
    private int _nextEntityId;
    private long _lastTickTimestamp;
    private double _tickAccumulatorSeconds;
    private double _snapshotAccumulatorSeconds;
    private uint _serverTick;

    public HandshakeServer(
        int port,
        string? databasePath = null,
        int disconnectTimeoutMs = DefaultDisconnectTimeoutMs,
        TimeSpan? reconnectGraceWindow = null)
    {
        _port = port;
        _playerStore = new SqlitePlayerStore(databasePath ?? "vibemmo.db");
        _reconnectGraceWindow = reconnectGraceWindow ?? ReconnectGraceWindow;
        _netManager = new NetManager(_listener)
        {
            AutoRecycle = false,
            DisconnectTimeout = disconnectTimeoutMs
        };

        WireEvents();
    }

    public int LocalPort => _netManager.LocalPort;

    public bool Start()
    {
        if (_isRunning)
        {
            return true;
        }

        if (!_netManager.Start(_port))
        {
            Console.Error.WriteLine($"Failed to bind UDP server on 0.0.0.0:{_port}");
            return false;
        }

        _isRunning = true;
        _lastTickTimestamp = Stopwatch.GetTimestamp();
        _tickAccumulatorSeconds = 0;
        _snapshotAccumulatorSeconds = 0;
        _serverTick = 0;
        InitializeNpcs();
        Console.WriteLine($"Listening on 0.0.0.0:{LocalPort}");
        return true;
    }

    public void PollEvents()
    {
        if (!_isRunning)
        {
            return;
        }

        _netManager.PollEvents();
        CleanupExpiredPendingReconnects();
        SimulateTicks();
    }

    public void Stop()
    {
        if (!_isRunning && !_netManager.IsRunning)
        {
            return;
        }

        _isRunning = false;
        PersistAllActiveSessions();
        if (_netManager.IsRunning)
        {
            _netManager.Stop();
        }

        Console.WriteLine("Server stopped.");
    }

    public int Run()
    {
        if (!Start())
        {
            return 1;
        }

        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            while (_isRunning)
            {
                PollEvents();
                Thread.Sleep(5);
            }
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            Stop();
        }

        return 0;
    }

    public void Dispose()
    {
        Stop();
        _playerStore.Dispose();
    }

    public bool TryGetPlayerState(uint entityId, out PlayerStateSnapshot playerState)
    {
        foreach (var session in _sessions.Values)
        {
            if (!session.HandshakeComplete || session.EntityId != entityId)
            {
                continue;
            }

            playerState = new PlayerStateSnapshot(
                session.EntityId,
                session.PositionX,
                session.PositionY,
                session.VelocityX,
                session.VelocityY,
                session.LastProcessedInputSeq);
            return true;
        }

        playerState = default;
        return false;
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _isRunning = false;
    }

    private void WireEvents()
    {
        _listener.ConnectionRequestEvent += request => request.Accept();

        _listener.PeerConnectedEvent += peer =>
        {
            _sessions[peer.Id] = new PeerSession();
            LogInfo($"Connected peer={peer.Id}");
        };

        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (_sessions.TryGetValue(peer.Id, out var existingSession) && existingSession.HandshakeComplete)
            {
                PersistSession(existingSession);
                AddPendingReconnect(existingSession);
                RemoveEntityFromVisibility(existingSession.EntityId);
            }

            _sessions.Remove(peer.Id);
            LogInfo($"Disconnected peer={peer.Id} reason={info.Reason}");
        };

        _listener.NetworkErrorEvent += (endpoint, socketError) =>
        {
            LogWarn($"Network error endpoint={FormatEndpoint(endpoint)} error={socketError}");
        };

        _listener.NetworkReceiveEvent += (peer, reader, _, _) =>
        {
            try
            {
                HandleReceive(peer, reader);
            }
            finally
            {
                reader.Recycle();
            }
        };
    }

    private void SimulateTicks()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = (now - _lastTickTimestamp) / (double)Stopwatch.Frequency;
        _lastTickTimestamp = now;

        if (elapsedSeconds <= 0)
        {
            return;
        }

        if (elapsedSeconds > MaxSimulationCatchUpSeconds)
        {
            elapsedSeconds = MaxSimulationCatchUpSeconds;
        }

        _tickAccumulatorSeconds += elapsedSeconds;
        while (_tickAccumulatorSeconds >= SecondsPerTick)
        {
            SimulateSingleTick((float)SecondsPerTick);
            _tickAccumulatorSeconds -= SecondsPerTick;
        }
    }

    private void SimulateSingleTick(float deltaSeconds)
    {
        foreach (var session in _sessions.Values)
        {
            if (!session.HandshakeComplete)
            {
                continue;
            }

            session.VelocityX = session.MoveInputX * MoveSpeedUnitsPerSecond;
            session.VelocityY = session.MoveInputY * MoveSpeedUnitsPerSecond;
            session.PositionX += session.VelocityX * deltaSeconds;
            session.PositionY += session.VelocityY * deltaSeconds;
        }
        SimulateNpcs(deltaSeconds);

        _serverTick++;
        _snapshotAccumulatorSeconds += deltaSeconds;
        while (_snapshotAccumulatorSeconds >= SecondsPerSnapshot)
        {
            _snapshotAccumulatorSeconds -= SecondsPerSnapshot;
            BroadcastSnapshots();
        }
    }

    private void HandleReceive(NetPeer peer, NetPacketReader reader)
    {
        var payloadSize = reader.AvailableBytes;
        if (payloadSize <= 0)
        {
            return;
        }

        if (!_sessions.TryGetValue(peer.Id, out var session))
        {
            session = new PeerSession();
            _sessions[peer.Id] = session;
        }

        var now = DateTimeOffset.UtcNow;
        if (!session.TryRegisterPacketAttempt(now, PacketRateWindow, MaxPacketsPerWindow))
        {
            if (session.ShouldLogTransportReject(now, TransportRejectLogCooldown))
            {
                LogWarn($"Dropped peer={peer.Id} reason=packet_rate_limited");
            }

            peer.Disconnect();
            return;
        }

        if (payloadSize > MaxMessageBytes)
        {
            if (session.ShouldLogTransportReject(now, TransportRejectLogCooldown))
            {
                LogWarn($"Rejected peer={peer.Id} reason=oversized_packet bytes={payloadSize}");
            }

            peer.Disconnect();
            return;
        }

        ClientPacket packet;
        try
        {
            packet = ClientPacket.Parser.ParseFrom(reader.GetRemainingBytes());
        }
        catch (InvalidProtocolBufferException)
        {
            if (!session.TryRegisterMalformedPacket(now, MalformedPacketWindow, MaxMalformedPacketsPerWindow))
            {
                if (session.ShouldLogTransportReject(now, TransportRejectLogCooldown))
                {
                    LogWarn($"Dropped peer={peer.Id} reason=malformed_rate_limited");
                }

                peer.Disconnect();
                return;
            }

            if (session.ShouldLogTransportReject(now, TransportRejectLogCooldown))
            {
                LogWarn($"Dropped malformed protobuf from peer={peer.Id}");
            }

            return;
        }

        switch (packet.PayloadCase)
        {
            case ClientPacket.PayloadOneofCase.CHello:
                HandleHello(peer, session, packet.CHello);
                break;

            case ClientPacket.PayloadOneofCase.CMoveInput:
                HandleMoveInput(session, packet.CMoveInput);
                break;

            case ClientPacket.PayloadOneofCase.CChatSend:
                HandleChatSend(session, packet.CChatSend);
                break;

            case ClientPacket.PayloadOneofCase.CPing:
                HandlePing(peer, session, packet.CPing);
                break;

            case ClientPacket.PayloadOneofCase.CAttack:
                HandleAttack(session, packet.CAttack);
                break;

            case ClientPacket.PayloadOneofCase.CPickup:
                HandlePickup(session, packet.CPickup);
                break;
        }
    }

    private void HandleHello(NetPeer peer, PeerSession session, C_Hello hello)
    {
        var now = DateTimeOffset.UtcNow;

        if (!session.TryRegisterHelloAttempt(now, HelloRateWindow, MaxHelloAttemptsPerWindow))
        {
            SendReject(peer, "Too many hello attempts.");
            LogWarn($"Rejected peer={peer.Id} reason=hello_rate_limited");
            peer.Disconnect();
            return;
        }

        if (session.HandshakeComplete)
        {
            SendReject(peer, "Hello already accepted.");
            LogWarn($"Rejected peer={peer.Id} reason=duplicate_hello");
            return;
        }

        if (hello.ProtocolVersion != ProtocolVersion)
        {
            SendReject(peer, $"Protocol mismatch. Server={ProtocolVersion}, Client={hello.ProtocolVersion}.");
            LogInfo($"Rejected peer={peer.Id} reason=protocol_mismatch client={hello.ProtocolVersion}");
            return;
        }

        if (!TryValidateDisplayName(hello.DisplayName, out var sanitizedName))
        {
            SendReject(peer, $"Display name must be 1-{MaxDisplayNameLength} chars and contain no control chars.");
            LogInfo($"Rejected peer={peer.Id} reason=invalid_display_name");
            return;
        }

        session.HandshakeComplete = true;
        var requestedToken = hello.ReconnectToken.ToByteArray();
        if (TryConsumePendingReconnect(requestedToken, out var pendingReconnect))
        {
            session.EntityId = pendingReconnect.EntityId;
            session.DisplayName = sanitizedName;
            session.ReconnectToken = requestedToken;
            session.PositionX = pendingReconnect.PositionX;
            session.PositionY = pendingReconnect.PositionY;
            session.VelocityX = 0;
            session.VelocityY = 0;
            session.MoveInputX = pendingReconnect.MoveInputX;
            session.MoveInputY = pendingReconnect.MoveInputY;
            session.LastMoveSeq = pendingReconnect.LastMoveSeq;
            session.HasLastMoveSeq = pendingReconnect.HasLastMoveSeq;
            session.LastProcessedInputSeq = pendingReconnect.LastProcessedInputSeq;
            session.Coins = pendingReconnect.Coins;
            session.CurrentHealth = PlayerMaxHealth;
            session.MaxHealth = PlayerMaxHealth;
            session.NextAttackAllowedTick = 0;
            LogInfo($"Reclaimed session entity_id={session.EntityId} peer={peer.Id}");
        }
        else if (requestedToken.Length > 0 && _playerStore.TryLoadByReconnectToken(requestedToken, out var persisted))
        {
            session.EntityId = (uint)Interlocked.Increment(ref _nextEntityId);
            session.DisplayName = sanitizedName;
            session.ReconnectToken = requestedToken;
            session.PositionX = persisted.PositionX;
            session.PositionY = persisted.PositionY;
            session.VelocityX = 0;
            session.VelocityY = 0;
            session.MoveInputX = 0;
            session.MoveInputY = 0;
            session.HasLastMoveSeq = false;
            session.LastProcessedInputSeq = 0;
            session.Coins = persisted.Coins;
            session.CurrentHealth = PlayerMaxHealth;
            session.MaxHealth = PlayerMaxHealth;
            session.NextAttackAllowedTick = 0;
        }
        else
        {
            session.EntityId = (uint)Interlocked.Increment(ref _nextEntityId);
            session.DisplayName = sanitizedName;
            session.ReconnectToken = RandomNumberGenerator.GetBytes(ReconnectTokenBytes);
            session.PositionX = 0;
            session.PositionY = 0;
            session.VelocityX = 0;
            session.VelocityY = 0;
            session.MoveInputX = 0;
            session.MoveInputY = 0;
            session.HasLastMoveSeq = false;
            session.LastProcessedInputSeq = 0;
            session.Coins = 0;
            session.CurrentHealth = PlayerMaxHealth;
            session.MaxHealth = PlayerMaxHealth;
            session.NextAttackAllowedTick = 0;
        }
        PersistSession(session);

        var welcome = new ServerPacket
        {
            SWelcome = new S_Welcome
            {
                ProtocolVersion = ProtocolVersion,
                YourEntityId = session.EntityId,
                ReconnectToken = ByteString.CopyFrom(session.ReconnectToken),
                TickRateHz = TickRateHz,
                SnapshotRateHz = SnapshotRateHz
            }
        };

        SendPacket(peer, welcome, DeliveryMethod.ReliableOrdered);
        session.VisibleEntityIds.Clear();
        RefreshVisibilityForAllSessions(BuildEntityCatalog());
        LogInfo($"Accepted peer={peer.Id} entity_id={session.EntityId} name=\"{session.DisplayName}\"");
    }

    private static void HandleMoveInput(PeerSession session, C_MoveInput moveInput)
    {
        if (!session.HandshakeComplete)
        {
            return;
        }

        if (!IsFinite(moveInput.MoveX) || !IsFinite(moveInput.MoveY))
        {
            return;
        }

        if (session.HasLastMoveSeq && moveInput.Seq <= session.LastMoveSeq)
        {
            return;
        }

        var moveX = moveInput.MoveX;
        var moveY = moveInput.MoveY;
        var magnitudeSquared = moveX * moveX + moveY * moveY;
        if (magnitudeSquared > 1.0f && magnitudeSquared > 0)
        {
            var inverseMagnitude = 1.0f / MathF.Sqrt(magnitudeSquared);
            moveX *= inverseMagnitude;
            moveY *= inverseMagnitude;
        }

        session.MoveInputX = moveX;
        session.MoveInputY = moveY;
        session.LastMoveSeq = moveInput.Seq;
        session.HasLastMoveSeq = true;
        session.LastProcessedInputSeq = moveInput.Seq;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private void HandleChatSend(PeerSession session, C_ChatSend chatSend)
    {
        if (!session.HandshakeComplete)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!session.TryRegisterChatAttempt(now, ChatRateWindow, MaxChatMessagesPerWindow))
        {
            if (session.ShouldLogChatReject(now, ChatRejectLogCooldown))
            {
                LogWarn($"Rejected chat entity_id={session.EntityId} reason=rate_limited");
            }

            return;
        }

        if (!TryValidateChatText(chatSend.Text, out var sanitizedText))
        {
            if (session.ShouldLogChatReject(now, ChatRejectLogCooldown))
            {
                LogWarn($"Rejected chat entity_id={session.EntityId} reason=invalid_text");
            }

            return;
        }

        var packet = new ServerPacket
        {
            SChatBroadcast = new S_ChatBroadcast
            {
                FromEntityId = session.EntityId,
                FromName = session.DisplayName,
                Text = sanitizedText
            }
        };

        BroadcastToHandshakenClients(packet, DeliveryMethod.ReliableOrdered);
    }

    private static void HandlePing(NetPeer peer, PeerSession session, C_Ping ping)
    {
        if (!session.HandshakeComplete)
        {
            return;
        }

        var packet = new ServerPacket
        {
            SPong = new S_Pong
            {
                PingId = ping.PingId,
                ServerTimeMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        };

        SendPacket(peer, packet, DeliveryMethod.Sequenced);
    }

    private void HandleAttack(PeerSession session, C_Attack attack)
    {
        if (!session.HandshakeComplete)
        {
            return;
        }

        if (_serverTick < session.NextAttackAllowedTick)
        {
            return;
        }

        if (!_npcs.TryGetValue(attack.TargetEntityId, out var npc) || !npc.IsAlive)
        {
            return;
        }

        if (!IsWithinRange(session.PositionX, session.PositionY, npc.PositionX, npc.PositionY, AttackRangeUnits))
        {
            return;
        }

        session.NextAttackAllowedTick = _serverTick + AttackCooldownTicks;
        if (npc.CurrentHealth <= AttackDamage)
        {
            npc.CurrentHealth = 0;
            npc.IsAlive = false;
            npc.RespawnAtTick = _serverTick + NpcRespawnDelayTicks;
            npc.VelocityX = 0;
            npc.VelocityY = 0;
            RemoveEntityFromVisibility(npc.EntityId);
            SpawnLootDrop(npc.PositionX, npc.PositionY, amount: 1);
            return;
        }

        npc.CurrentHealth -= AttackDamage;
    }

    private void HandlePickup(PeerSession session, C_Pickup pickup)
    {
        if (!session.HandshakeComplete)
        {
            return;
        }

        if (!_lootDrops.TryGetValue(pickup.LootEntityId, out var loot))
        {
            return;
        }

        if (!IsWithinRange(session.PositionX, session.PositionY, loot.PositionX, loot.PositionY, PickupRangeUnits))
        {
            return;
        }

        session.Coins += loot.Amount;
        _lootDrops.Remove(loot.EntityId);
        RemoveEntityFromVisibility(loot.EntityId);
    }

    private static bool IsWithinRange(float fromX, float fromY, float toX, float toY, float maxDistance)
    {
        var deltaX = toX - fromX;
        var deltaY = toY - fromY;
        return (deltaX * deltaX) + (deltaY * deltaY) <= (maxDistance * maxDistance);
    }

    private void PersistAllActiveSessions()
    {
        foreach (var session in _sessions.Values)
        {
            if (!session.HandshakeComplete)
            {
                continue;
            }

            PersistSession(session);
        }
    }

    private void PersistSession(PeerSession session)
    {
        try
        {
            _playerStore.Save(
                new PersistedPlayerRecord(
                    session.ReconnectToken,
                    session.DisplayName,
                    session.PositionX,
                    session.PositionY,
                    session.Coins,
                    DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            LogWarn($"Failed to persist entity_id={session.EntityId} error={ex.GetType().Name}");
        }
    }

    private void BroadcastSnapshots()
    {
        var catalog = BuildEntityCatalog();
        if (catalog.Count == 0)
        {
            return;
        }

        RefreshVisibilityForAllSessions(catalog);

        foreach (var kvp in _sessions)
        {
            var peerId = kvp.Key;
            var recipientSession = kvp.Value;
            if (!recipientSession.HandshakeComplete)
            {
                continue;
            }

            if (!_netManager.TryGetPeerById(peerId, out var peer) || peer.ConnectionState != ConnectionState.Connected)
            {
                continue;
            }

            if (!catalog.TryGetValue(recipientSession.EntityId, out var selfEntity))
            {
                continue;
            }

            var snapshot = new S_Snapshot
            {
                ServerTick = _serverTick,
                LastProcessedInputSeq = recipientSession.LastProcessedInputSeq,
                YourCoins = (uint)Math.Max(0, recipientSession.Coins)
            };
            snapshot.Entities.Add(BuildEntityState(selfEntity));
            foreach (var visibleEntityId in recipientSession.VisibleEntityIds)
            {
                if (catalog.TryGetValue(visibleEntityId, out var visibleEntity))
                {
                    snapshot.Entities.Add(BuildEntityState(visibleEntity));
                }
            }

            var packet = new ServerPacket
            {
                SSnapshot = snapshot
            };

            var payload = packet.ToByteArray();
            if (payload.Length >= MaxMessageBytes * 0.9)
            {
                LogWarn($"Snapshot near MTU peer={peer.Id} bytes={payload.Length}");
            }

            peer.Send(payload, DeliveryMethod.Sequenced);
        }
    }

    private static bool TryValidateDisplayName(string? input, out string sanitized)
    {
        sanitized = (input ?? string.Empty).Trim();
        if (sanitized.Length is < 1 or > MaxDisplayNameLength)
        {
            return false;
        }

        for (var i = 0; i < sanitized.Length; i++)
        {
            if (char.IsControl(sanitized[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateChatText(string? input, out string sanitized)
    {
        sanitized = (input ?? string.Empty).Trim();
        if (sanitized.Length is < 1 or > MaxChatLength)
        {
            return false;
        }

        for (var i = 0; i < sanitized.Length; i++)
        {
            if (char.IsControl(sanitized[i]))
            {
                return false;
            }
        }

        return true;
    }

    private Dictionary<uint, VisibleEntity> BuildEntityCatalog()
    {
        var catalog = new Dictionary<uint, VisibleEntity>();
        foreach (var session in _sessions.Values)
        {
            if (!session.HandshakeComplete)
            {
                continue;
            }

            catalog[session.EntityId] = new VisibleEntity(
                session.EntityId,
                session.DisplayName,
                session.PositionX,
                session.PositionY,
                session.VelocityX,
                session.VelocityY,
                EntityKind.Player,
                session.CurrentHealth,
                session.MaxHealth);
        }

        foreach (var npc in _npcs.Values)
        {
            if (!npc.IsAlive)
            {
                continue;
            }

            catalog[npc.EntityId] = new VisibleEntity(
                npc.EntityId,
                npc.DisplayName,
                npc.PositionX,
                npc.PositionY,
                npc.VelocityX,
                npc.VelocityY,
                EntityKind.Npc,
                npc.CurrentHealth,
                npc.MaxHealth);
        }

        foreach (var loot in _lootDrops.Values)
        {
            catalog[loot.EntityId] = new VisibleEntity(
                loot.EntityId,
                $"coin+{loot.Amount}",
                loot.PositionX,
                loot.PositionY,
                0,
                0,
                EntityKind.Loot,
                0,
                0);
        }

        return catalog;
    }

    private void RefreshVisibilityForAllSessions(Dictionary<uint, VisibleEntity> catalog)
    {
        foreach (var kvp in _sessions)
        {
            if (!kvp.Value.HandshakeComplete)
            {
                continue;
            }

            if (!_netManager.TryGetPeerById(kvp.Key, out var peer) || peer.ConnectionState != ConnectionState.Connected)
            {
                continue;
            }

            RefreshVisibilityForSession(peer, kvp.Value, catalog);
        }
    }

    private void RefreshVisibilityForSession(NetPeer peer, PeerSession session, Dictionary<uint, VisibleEntity> catalog)
    {
        var nearbyEntityIds = ComputeNearbyEntityIds(session, catalog);
        var spawns = new List<uint>();
        foreach (var nearbyEntityId in nearbyEntityIds)
        {
            if (!session.VisibleEntityIds.Contains(nearbyEntityId))
            {
                spawns.Add(nearbyEntityId);
            }
        }

        var despawns = new List<uint>();
        foreach (var visibleEntityId in session.VisibleEntityIds)
        {
            if (!nearbyEntityIds.Contains(visibleEntityId))
            {
                despawns.Add(visibleEntityId);
            }
        }

        foreach (var despawnEntityId in despawns)
        {
            session.VisibleEntityIds.Remove(despawnEntityId);
            SendDespawn(peer, despawnEntityId);
        }

        foreach (var spawnEntityId in spawns)
        {
            session.VisibleEntityIds.Add(spawnEntityId);
            if (catalog.TryGetValue(spawnEntityId, out var visibleEntity))
            {
                SendSpawn(peer, visibleEntity);
            }
        }
    }

    private HashSet<uint> ComputeNearbyEntityIds(PeerSession session, Dictionary<uint, VisibleEntity> catalog)
    {
        var nearby = new HashSet<uint>();
        foreach (var visibleEntity in catalog.Values)
        {
            if (visibleEntity.EntityId == session.EntityId)
            {
                continue;
            }

            if (IsWithinRange(session.PositionX, session.PositionY, visibleEntity.PositionX, visibleEntity.PositionY, ViewRadiusUnits))
            {
                nearby.Add(visibleEntity.EntityId);
            }
        }

        return nearby;
    }

    private void SendSpawn(NetPeer peer, VisibleEntity entity)
    {
        var packet = new ServerPacket
        {
            SEntitySpawn = new S_EntitySpawn
            {
                EntityId = entity.EntityId,
                DisplayName = entity.DisplayName,
                X = entity.PositionX,
                Y = entity.PositionY,
                EntityKind = entity.EntityKind
            }
        };

        SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
    }

    private static void SendDespawn(NetPeer peer, uint entityId)
    {
        var packet = new ServerPacket
        {
            SEntityDespawn = new S_EntityDespawn
            {
                EntityId = entityId
            }
        };

        SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
    }

    private void RemoveEntityFromVisibility(uint entityId)
    {
        foreach (var kvp in _sessions)
        {
            if (!kvp.Value.HandshakeComplete)
            {
                continue;
            }

            if (!kvp.Value.VisibleEntityIds.Remove(entityId))
            {
                continue;
            }

            if (_netManager.TryGetPeerById(kvp.Key, out var peer) && peer.ConnectionState == ConnectionState.Connected)
            {
                SendDespawn(peer, entityId);
            }
        }
    }

    private static EntityState BuildEntityState(VisibleEntity entity)
    {
        return new EntityState
        {
            EntityId = entity.EntityId,
            X = entity.PositionX,
            Y = entity.PositionY,
            Vx = entity.VelocityX,
            Vy = entity.VelocityY,
            EntityKind = entity.EntityKind,
            CurrentHealth = entity.CurrentHealth,
            MaxHealth = entity.MaxHealth
        };
    }

    private void InitializeNpcs()
    {
        if (_npcs.Count > 0)
        {
            return;
        }

        var templates = new (float X, float Y, float DirX, float DirY, float Speed)[]
        {
            (1.25f, 0.0f, 0.0f, 0.0f, 0.0f),
            (4.5f, 1.0f, -1.0f, 0.0f, NpcMoveSpeedUnitsPerSecond),
            (-4.5f, -1.0f, 1.0f, 0.0f, NpcMoveSpeedUnitsPerSecond),
            (0.0f, 4.0f, 0.0f, -1.0f, NpcMoveSpeedUnitsPerSecond)
        };

        for (var i = 0; i < InitialNpcCount; i++)
        {
            var template = templates[i % templates.Length];
            var npc = new NpcEntity
            {
                EntityId = (uint)Interlocked.Increment(ref _nextEntityId),
                DisplayName = $"npc-{i + 1}",
                SpawnX = template.X,
                SpawnY = template.Y,
                PositionX = template.X,
                PositionY = template.Y,
                DirectionX = template.DirX,
                DirectionY = template.DirY,
                MoveSpeed = template.Speed,
                CurrentHealth = NpcMaxHealth,
                MaxHealth = NpcMaxHealth,
                IsAlive = true,
                TicksUntilDirectionChange = NpcDirectionChangeTicks
            };
            _npcs[npc.EntityId] = npc;
        }
    }

    private void SimulateNpcs(float deltaSeconds)
    {
        foreach (var npc in _npcs.Values)
        {
            if (!npc.IsAlive)
            {
                if (_serverTick >= npc.RespawnAtTick)
                {
                    RespawnNpc(npc);
                }

                continue;
            }

            if (npc.TicksUntilDirectionChange <= 0)
            {
                var clockwise = (npc.EntityId & 1) == 0;
                RotateDirection(npc, clockwise ? 0.75f : -0.75f);
                npc.TicksUntilDirectionChange = NpcDirectionChangeTicks;
            }
            else
            {
                npc.TicksUntilDirectionChange--;
            }

            npc.VelocityX = npc.DirectionX * npc.MoveSpeed;
            npc.VelocityY = npc.DirectionY * npc.MoveSpeed;
            npc.PositionX += npc.VelocityX * deltaSeconds;
            npc.PositionY += npc.VelocityY * deltaSeconds;
        }
    }

    private void RespawnNpc(NpcEntity npc)
    {
        npc.IsAlive = true;
        npc.CurrentHealth = npc.MaxHealth;
        npc.PositionX = npc.SpawnX;
        npc.PositionY = npc.SpawnY;
        npc.VelocityX = 0;
        npc.VelocityY = 0;
        npc.TicksUntilDirectionChange = NpcDirectionChangeTicks;
        RefreshVisibilityForAllSessions(BuildEntityCatalog());
    }

    private static void RotateDirection(NpcEntity npc, float radians)
    {
        if (npc.MoveSpeed <= 0)
        {
            return;
        }

        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var x = npc.DirectionX;
        var y = npc.DirectionY;
        npc.DirectionX = (x * cos) - (y * sin);
        npc.DirectionY = (x * sin) + (y * cos);
    }

    private void SpawnLootDrop(float x, float y, int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        var loot = new LootEntity
        {
            EntityId = (uint)Interlocked.Increment(ref _nextEntityId),
            PositionX = x,
            PositionY = y,
            Amount = amount
        };
        _lootDrops[loot.EntityId] = loot;
        RefreshVisibilityForAllSessions(BuildEntityCatalog());
    }

    private void BroadcastToHandshakenClients(ServerPacket packet, DeliveryMethod deliveryMethod)
    {
        var payload = packet.ToByteArray();
        foreach (var kvp in _sessions)
        {
            if (!kvp.Value.HandshakeComplete)
            {
                continue;
            }

            if (_netManager.TryGetPeerById(kvp.Key, out var peer) && peer.ConnectionState == ConnectionState.Connected)
            {
                peer.Send(payload, deliveryMethod);
            }
        }
    }

    private static void SendReject(NetPeer peer, string reason)
    {
        var reject = new ServerPacket
        {
            SReject = new S_Reject
            {
                Reason = reason
            }
        };

        SendPacket(peer, reject, DeliveryMethod.ReliableOrdered);
    }

    private static void SendPacket(NetPeer peer, ServerPacket packet, DeliveryMethod deliveryMethod)
    {
        peer.Send(packet.ToByteArray(), deliveryMethod);
    }

    private static string FormatEndpoint(IPEndPoint? endpoint)
    {
        return endpoint is null ? "unknown" : endpoint.ToString();
    }

    private static void LogInfo(string message)
    {
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] INFO {message}");
    }

    private static void LogWarn(string message)
    {
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] WARN {message}");
    }

    private void AddPendingReconnect(PeerSession session)
    {
        if (session.ReconnectToken.Length == 0 || _reconnectGraceWindow <= TimeSpan.Zero)
        {
            return;
        }

        _pendingReconnects[Convert.ToBase64String(session.ReconnectToken)] = new PendingReconnectSession(
            session.EntityId,
            session.ReconnectToken,
            session.PositionX,
            session.PositionY,
            session.MoveInputX,
            session.MoveInputY,
            session.LastMoveSeq,
            session.HasLastMoveSeq,
            session.LastProcessedInputSeq,
            session.Coins,
            DateTimeOffset.UtcNow + _reconnectGraceWindow);
    }

    private bool TryConsumePendingReconnect(byte[] reconnectToken, out PendingReconnectSession pending)
    {
        pending = default;
        if (reconnectToken.Length == 0 || _pendingReconnects.Count == 0)
        {
            return false;
        }

        var key = Convert.ToBase64String(reconnectToken);
        if (!_pendingReconnects.TryGetValue(key, out var candidate))
        {
            return false;
        }

        if (candidate.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            _pendingReconnects.Remove(key);
            return false;
        }

        _pendingReconnects.Remove(key);
        pending = candidate;
        return true;
    }

    private void CleanupExpiredPendingReconnects()
    {
        if (_pendingReconnects.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var expiredKeys = new List<string>();
        foreach (var kvp in _pendingReconnects)
        {
            if (kvp.Value.ExpiresAtUtc < now)
            {
                expiredKeys.Add(kvp.Key);
            }
        }

        foreach (var key in expiredKeys)
        {
            _pendingReconnects.Remove(key);
        }
    }
}

public readonly record struct PlayerStateSnapshot(
    uint EntityId,
    float PositionX,
    float PositionY,
    float VelocityX,
    float VelocityY,
    uint LastProcessedInputSeq);

internal readonly record struct VisibleEntity(
    uint EntityId,
    string DisplayName,
    float PositionX,
    float PositionY,
    float VelocityX,
    float VelocityY,
    EntityKind EntityKind,
    uint CurrentHealth,
    uint MaxHealth);

internal readonly record struct PendingReconnectSession(
    uint EntityId,
    byte[] ReconnectToken,
    float PositionX,
    float PositionY,
    float MoveInputX,
    float MoveInputY,
    uint LastMoveSeq,
    bool HasLastMoveSeq,
    uint LastProcessedInputSeq,
    int Coins,
    DateTimeOffset ExpiresAtUtc);

internal sealed class PeerSession
{
    private readonly Queue<DateTimeOffset> _helloAttempts = new();
    private readonly Queue<DateTimeOffset> _chatAttempts = new();
    private readonly Queue<DateTimeOffset> _packetAttempts = new();
    private readonly Queue<DateTimeOffset> _malformedPacketAttempts = new();
    private DateTimeOffset _lastChatRejectLogUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTransportRejectLogUtc = DateTimeOffset.MinValue;

    public bool HandshakeComplete { get; set; }

    public uint EntityId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public byte[] ReconnectToken { get; set; } = [];

    public float PositionX { get; set; }

    public float PositionY { get; set; }

    public float VelocityX { get; set; }

    public float VelocityY { get; set; }

    public float MoveInputX { get; set; }

    public float MoveInputY { get; set; }

    public uint LastMoveSeq { get; set; }

    public bool HasLastMoveSeq { get; set; }

    public uint LastProcessedInputSeq { get; set; }

    public int Coins { get; set; }

    public uint CurrentHealth { get; set; }

    public uint MaxHealth { get; set; }

    public uint NextAttackAllowedTick { get; set; }

    public HashSet<uint> VisibleEntityIds { get; } = [];

    public bool TryRegisterHelloAttempt(DateTimeOffset now, TimeSpan window, int maxAttemptsPerWindow)
    {
        while (_helloAttempts.Count > 0 && now - _helloAttempts.Peek() > window)
        {
            _helloAttempts.Dequeue();
        }

        if (_helloAttempts.Count >= maxAttemptsPerWindow)
        {
            return false;
        }

        _helloAttempts.Enqueue(now);
        return true;
    }

    public bool TryRegisterChatAttempt(DateTimeOffset now, TimeSpan window, int maxAttemptsPerWindow)
    {
        while (_chatAttempts.Count > 0 && now - _chatAttempts.Peek() > window)
        {
            _chatAttempts.Dequeue();
        }

        if (_chatAttempts.Count >= maxAttemptsPerWindow)
        {
            return false;
        }

        _chatAttempts.Enqueue(now);
        return true;
    }

    public bool TryRegisterPacketAttempt(DateTimeOffset now, TimeSpan window, int maxAttemptsPerWindow)
    {
        while (_packetAttempts.Count > 0 && now - _packetAttempts.Peek() > window)
        {
            _packetAttempts.Dequeue();
        }

        if (_packetAttempts.Count >= maxAttemptsPerWindow)
        {
            return false;
        }

        _packetAttempts.Enqueue(now);
        return true;
    }

    public bool TryRegisterMalformedPacket(DateTimeOffset now, TimeSpan window, int maxAttemptsPerWindow)
    {
        while (_malformedPacketAttempts.Count > 0 && now - _malformedPacketAttempts.Peek() > window)
        {
            _malformedPacketAttempts.Dequeue();
        }

        if (_malformedPacketAttempts.Count >= maxAttemptsPerWindow)
        {
            return false;
        }

        _malformedPacketAttempts.Enqueue(now);
        return true;
    }

    public bool ShouldLogChatReject(DateTimeOffset now, TimeSpan cooldown)
    {
        if (now - _lastChatRejectLogUtc < cooldown)
        {
            return false;
        }

        _lastChatRejectLogUtc = now;
        return true;
    }

    public bool ShouldLogTransportReject(DateTimeOffset now, TimeSpan cooldown)
    {
        if (now - _lastTransportRejectLogUtc < cooldown)
        {
            return false;
        }

        _lastTransportRejectLogUtc = now;
        return true;
    }
}

internal sealed class NpcEntity
{
    public uint EntityId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public float PositionX { get; set; }

    public float PositionY { get; set; }

    public float SpawnX { get; set; }

    public float SpawnY { get; set; }

    public float VelocityX { get; set; }

    public float VelocityY { get; set; }

    public float DirectionX { get; set; }

    public float DirectionY { get; set; }

    public float MoveSpeed { get; set; }

    public uint CurrentHealth { get; set; }

    public uint MaxHealth { get; set; }

    public bool IsAlive { get; set; }

    public uint RespawnAtTick { get; set; }

    public int TicksUntilDirectionChange { get; set; }
}

internal sealed class LootEntity
{
    public uint EntityId { get; set; }

    public float PositionX { get; set; }

    public float PositionY { get; set; }

    public int Amount { get; set; }
}
