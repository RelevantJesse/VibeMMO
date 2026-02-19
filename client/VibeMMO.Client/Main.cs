using Google.Protobuf;
using Godot;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using VibeMMO.Shared.Protocol.V0;

public partial class Main : Control
{
    private const uint ProtocolVersion = 1;
    private const float InputSendHz = 20.0f;
    private const float PingSendHz = 1.0f;
    private const float AttackRangeUnits = 2.0f;
    private const float PickupRangeUnits = 1.25f;
    private const double AttackSendCooldownSeconds = 0.25;
    private const double PickupSendCooldownSeconds = 0.15;
    private const float WorldUnitsToPixels = 32.0f;
    private const float SnapshotSmoothingRate = 12.0f;
    private const int MaxDisplayNameLength = 24;
    private const int MaxChatLines = 100;
    private const string DefaultAddress = "127.0.0.1:7777";
    private const string DefaultName = "Player";

    private readonly byte[] _emptyToken = [];

    private LineEdit _addressInput = null!;
    private LineEdit _nameInput = null!;
    private Button _connectButton = null!;
    private Label _statusLabel = null!;
    private Label _debugOverlayLabel = null!;
    private ItemList _chatLog = null!;
    private LineEdit _chatInput = null!;
    private Button _chatSendButton = null!;

    private EventBasedNetListener? _listener;
    private NetManager? _client;
    private NetPeer? _peer;

    private ConnectionUiState _state = ConnectionUiState.Disconnected;
    private byte[] _reconnectToken = [];
    private string _pendingDisplayName = string.Empty;
    private bool _isStoppingClient;
    private double _moveInputAccumulatorSeconds;
    private double _pingAccumulatorSeconds;
    private uint _nextMoveSeq = 1;
    private uint _nextPingId = 1;
    private double _attackCooldownRemainingSeconds;
    private double _pickupCooldownRemainingSeconds;
    private uint _localEntityId;
    private uint _lastServerTick;
    private uint _selectedTargetEntityId;
    private uint _selectedLootEntityId;
    private uint _localCurrentHealth;
    private uint _localMaxHealth;
    private uint _coins;
    private float? _rollingPingMs;
    private bool _hasLocalEntityId;
    private Vector2 _lastLocalPosition;
    private readonly Dictionary<uint, ulong> _pendingPingSentAtMs = [];
    private readonly Dictionary<uint, string> _entityNames = [];
    private readonly Dictionary<uint, EntityKind> _entityKinds = [];
    private readonly Dictionary<uint, uint> _entityCurrentHealth = [];
    private readonly Dictionary<uint, uint> _entityMaxHealth = [];
    private readonly Dictionary<uint, Vector2> _snapshotWorldPositions = [];
    private readonly Dictionary<uint, Vector2> _renderWorldPositions = [];

    public override void _Ready()
    {
        BuildUi();
        SetUiState(ConnectionUiState.Disconnected, "Disconnected");
    }

    public override void _Process(double delta)
    {
        _client?.PollEvents();
        TrySendMoveInput(delta);
        TrySendPing(delta);
        UpdateSelectionsAndCooldowns(delta);
        TrySendAttack();
        TrySendPickup();
        UpdateRenderPositions(delta);
        UpdateDebugOverlay();
    }

    public override void _Draw()
    {
        if (_state != ConnectionUiState.Connected)
        {
            return;
        }

        var center = Size * 0.5f;
        var axisColor = new Color(0.34f, 0.34f, 0.34f, 0.6f);
        DrawLine(new Vector2(0, center.Y), new Vector2(Size.X, center.Y), axisColor, 1.0f);
        DrawLine(new Vector2(center.X, 0), new Vector2(center.X, Size.Y), axisColor, 1.0f);

        foreach (var kvp in _renderWorldPositions)
        {
            var entityId = kvp.Key;
            var worldPosition = kvp.Value;
            var screenPosition = center + (worldPosition * WorldUnitsToPixels);
            _entityKinds.TryGetValue(entityId, out var entityKind);
            var isLocal = _hasLocalEntityId && entityId == _localEntityId;
            var isNpc = entityKind == EntityKind.Npc;
            var isLoot = entityKind == EntityKind.Loot;
            var color = isLocal
                ? new Color(0.2f, 0.72f, 0.97f)
                : isNpc
                    ? new Color(0.31f, 0.82f, 0.43f)
                    : isLoot
                        ? new Color(0.99f, 0.85f, 0.22f)
                        : new Color(0.96f, 0.58f, 0.21f);
            var radius = isLocal ? 12.0f : isNpc ? 9.0f : isLoot ? 6.5f : 10.0f;
            DrawCircle(screenPosition, radius, color);

            if (_entityNames.TryGetValue(entityId, out var name) && !string.IsNullOrWhiteSpace(name))
            {
                var labelPosition = screenPosition + new Vector2(-28, -22);
                DrawString(ThemeDB.FallbackFont, labelPosition, name, HorizontalAlignment.Left, -1, 14, color);
            }
        }
    }

    public override void _ExitTree()
    {
        DisconnectClient("Disconnected");
    }

    private void BuildUi()
    {
        _debugOverlayLabel = new Label
        {
            Text = "Disconnected",
            Position = new Vector2(12, 12),
            AutowrapMode = TextServer.AutowrapMode.Off
        };
        _debugOverlayLabel.AddThemeColorOverride("font_color", new Color(0.92f, 0.95f, 0.98f));
        AddChild(_debugOverlayLabel);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(460, 0)
        };
        center.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        panel.AddChild(margin);

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 8);
        margin.AddChild(layout);

        var title = new Label
        {
            Text = "VibeMMO Connect"
        };
        layout.AddChild(title);

        layout.AddChild(new Label { Text = "Server Address" });
        _addressInput = new LineEdit
        {
            Text = DefaultAddress,
            PlaceholderText = "127.0.0.1:7777",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        layout.AddChild(_addressInput);

        layout.AddChild(new Label { Text = "Display Name" });
        _nameInput = new LineEdit
        {
            Text = DefaultName,
            PlaceholderText = "Enter display name",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        layout.AddChild(_nameInput);

        _connectButton = new Button
        {
            Text = "Connect",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _connectButton.Pressed += OnConnectButtonPressed;
        layout.AddChild(_connectButton);

        _statusLabel = new Label
        {
            Text = "Disconnected",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        layout.AddChild(_statusLabel);

        layout.AddChild(new HSeparator());
        layout.AddChild(new Label { Text = "Chat" });

        _chatLog = new ItemList
        {
            CustomMinimumSize = new Vector2(0, 140)
        };
        layout.AddChild(_chatLog);

        var chatInputRow = new HBoxContainer();
        chatInputRow.AddThemeConstantOverride("separation", 8);
        layout.AddChild(chatInputRow);

        _chatInput = new LineEdit
        {
            PlaceholderText = "Type a message...",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _chatInput.TextSubmitted += _ => TrySendChat();
        chatInputRow.AddChild(_chatInput);

        _chatSendButton = new Button
        {
            Text = "Send"
        };
        _chatSendButton.Pressed += TrySendChat;
        chatInputRow.AddChild(_chatSendButton);
    }

    private void OnConnectButtonPressed()
    {
        if (_state == ConnectionUiState.Disconnected)
        {
            BeginConnect();
            return;
        }

        DisconnectClient("Disconnected");
    }

    private void BeginConnect()
    {
        if (!TryParseAddress(_addressInput.Text, out var host, out var port))
        {
            SetUiState(ConnectionUiState.Disconnected, "Invalid address. Use host:port.");
            return;
        }

        if (!TryValidateDisplayName(_nameInput.Text, out var displayName))
        {
            SetUiState(ConnectionUiState.Disconnected, $"Display name must be 1-{MaxDisplayNameLength} characters.");
            return;
        }

        DisconnectClient("Disconnected");

        _listener = new EventBasedNetListener();
        _client = new NetManager(_listener)
        {
            AutoRecycle = false,
            DisconnectTimeout = 10000
        };

        WireClientEvents();

        if (!_client.Start())
        {
            SetUiState(ConnectionUiState.Disconnected, "Failed to initialize networking.");
            _client = null;
            _listener = null;
            return;
        }

        _pendingDisplayName = displayName;
        _moveInputAccumulatorSeconds = 0;
        _pingAccumulatorSeconds = 0;
        _nextMoveSeq = 1;
        _nextPingId = 1;
        _client.Connect(host, port, string.Empty);
        SetUiState(ConnectionUiState.Connecting, "Connecting...");
    }

    private void WireClientEvents()
    {
        if (_listener is null)
        {
            return;
        }

        _listener.PeerConnectedEvent += peer =>
        {
            _peer = peer;
            SendHello(peer);
        };

        _listener.PeerDisconnectedEvent += (_, info) =>
        {
            if (_isStoppingClient)
            {
                return;
            }

            DisconnectClient($"Disconnected: {info.Reason}");
        };

        _listener.NetworkErrorEvent += (_, error) =>
        {
            if (_isStoppingClient)
            {
                return;
            }

            DisconnectClient($"Network error: {error}");
        };

        _listener.NetworkReceiveEvent += (_, reader, _, _) =>
        {
            try
            {
                var packet = ServerPacket.Parser.ParseFrom(reader.GetRemainingBytes());
                HandleServerPacket(packet);
            }
            catch (InvalidProtocolBufferException)
            {
                DisconnectClient("Disconnected: malformed server packet.");
            }
            finally
            {
                reader.Recycle();
            }
        };
    }

    private void SendHello(NetPeer peer)
    {
        var token = _reconnectToken.Length > 0 ? _reconnectToken : _emptyToken;
        var hello = new ClientPacket
        {
            CHello = new C_Hello
            {
                ProtocolVersion = ProtocolVersion,
                DisplayName = _pendingDisplayName,
                ReconnectToken = ByteString.CopyFrom(token)
            }
        };

        peer.Send(hello.ToByteArray(), DeliveryMethod.ReliableOrdered);
    }

    private void HandleServerPacket(ServerPacket packet)
    {
        switch (packet.PayloadCase)
        {
            case ServerPacket.PayloadOneofCase.SWelcome:
                _reconnectToken = packet.SWelcome.ReconnectToken.ToByteArray();
                _moveInputAccumulatorSeconds = 0;
                _pingAccumulatorSeconds = 0;
                _nextMoveSeq = 1;
                _nextPingId = 1;
                _localEntityId = packet.SWelcome.YourEntityId;
                _lastServerTick = 0;
                _rollingPingMs = null;
                _hasLocalEntityId = true;
                _coins = 0;
                _localCurrentHealth = 0;
                _localMaxHealth = 0;
                _selectedTargetEntityId = 0;
                _selectedLootEntityId = 0;
                _attackCooldownRemainingSeconds = 0;
                _pickupCooldownRemainingSeconds = 0;
                _entityNames[_localEntityId] = _pendingDisplayName;
                _entityKinds[_localEntityId] = EntityKind.Player;
                _snapshotWorldPositions.Remove(_localEntityId);
                _renderWorldPositions.Remove(_localEntityId);
                _pendingPingSentAtMs.Clear();
                SetUiState(
                    ConnectionUiState.Connected,
                    $"Connected (EntityId: {_localEntityId}) - waiting for snapshots");
                AppendChatLine("[System] Connected.");
                break;

            case ServerPacket.PayloadOneofCase.SReject:
                DisconnectClient($"Rejected: {packet.SReject.Reason}");
                break;

            case ServerPacket.PayloadOneofCase.SSnapshot:
                HandleSnapshot(packet.SSnapshot);
                break;

            case ServerPacket.PayloadOneofCase.SEntitySpawn:
                HandleEntitySpawn(packet.SEntitySpawn);
                break;

            case ServerPacket.PayloadOneofCase.SEntityDespawn:
                HandleEntityDespawn(packet.SEntityDespawn);
                break;

            case ServerPacket.PayloadOneofCase.SChatBroadcast:
                AppendChatLine($"[{packet.SChatBroadcast.FromName}]: {packet.SChatBroadcast.Text}");
                break;

            case ServerPacket.PayloadOneofCase.SPong:
                HandlePong(packet.SPong);
                break;
        }
    }

    private void DisconnectClient(string message)
    {
        _isStoppingClient = true;
        _peer = null;

        if (_client is not null)
        {
            _client.Stop();
            _client = null;
        }

        _listener = null;
        _moveInputAccumulatorSeconds = 0;
        _pingAccumulatorSeconds = 0;
        _hasLocalEntityId = false;
        _localEntityId = 0;
        _lastServerTick = 0;
        _coins = 0;
        _localCurrentHealth = 0;
        _localMaxHealth = 0;
        _selectedTargetEntityId = 0;
        _selectedLootEntityId = 0;
        _attackCooldownRemainingSeconds = 0;
        _pickupCooldownRemainingSeconds = 0;
        _rollingPingMs = null;
        _entityNames.Clear();
        _entityKinds.Clear();
        _entityCurrentHealth.Clear();
        _entityMaxHealth.Clear();
        _snapshotWorldPositions.Clear();
        _renderWorldPositions.Clear();
        _pendingPingSentAtMs.Clear();
        _chatInput.Text = string.Empty;
        _isStoppingClient = false;
        SetUiState(ConnectionUiState.Disconnected, message);
    }

    private void TrySendMoveInput(double delta)
    {
        if (_state != ConnectionUiState.Connected || _peer is null || _peer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        _moveInputAccumulatorSeconds += delta;
        var sendIntervalSeconds = 1.0 / InputSendHz;
        if (_moveInputAccumulatorSeconds < sendIntervalSeconds)
        {
            return;
        }

        _moveInputAccumulatorSeconds -= sendIntervalSeconds;
        var moveVector = GetCurrentMoveInput();
        var packet = new ClientPacket
        {
            CMoveInput = new C_MoveInput
            {
                Seq = _nextMoveSeq++,
                MoveX = moveVector.X,
                MoveY = moveVector.Y
            }
        };

        _peer.Send(packet.ToByteArray(), DeliveryMethod.Sequenced);
    }

    private void TrySendPing(double delta)
    {
        if (_state != ConnectionUiState.Connected || _peer is null || _peer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        _pingAccumulatorSeconds += delta;
        var sendIntervalSeconds = 1.0 / PingSendHz;
        if (_pingAccumulatorSeconds < sendIntervalSeconds)
        {
            return;
        }

        _pingAccumulatorSeconds -= sendIntervalSeconds;
        var pingId = _nextPingId++;
        _pendingPingSentAtMs[pingId] = Time.GetTicksMsec();
        CleanupStalePendingPings();

        var packet = new ClientPacket
        {
            CPing = new C_Ping
            {
                PingId = pingId,
                ClientTimeMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        };

        _peer.Send(packet.ToByteArray(), DeliveryMethod.Sequenced);
    }

    private static Vector2 GetCurrentMoveInput()
    {
        var moveX = 0.0f;
        var moveY = 0.0f;

        if (Input.IsActionPressed("ui_left") || Input.IsKeyPressed(Key.A))
        {
            moveX -= 1.0f;
        }

        if (Input.IsActionPressed("ui_right") || Input.IsKeyPressed(Key.D))
        {
            moveX += 1.0f;
        }

        if (Input.IsActionPressed("ui_up") || Input.IsKeyPressed(Key.W))
        {
            moveY -= 1.0f;
        }

        if (Input.IsActionPressed("ui_down") || Input.IsKeyPressed(Key.S))
        {
            moveY += 1.0f;
        }

        var input = new Vector2(moveX, moveY);
        if (input.LengthSquared() > 1.0f)
        {
            input = input.Normalized();
        }

        return input;
    }

    private void HandleSnapshot(S_Snapshot snapshot)
    {
        if (!_hasLocalEntityId)
        {
            return;
        }

        _lastServerTick = snapshot.ServerTick;
        _coins = snapshot.YourCoins;
        var localPositionUpdated = false;
        foreach (var entity in snapshot.Entities)
        {
            if (!_entityNames.ContainsKey(entity.EntityId))
            {
                continue;
            }

            var nextPosition = new Vector2(entity.X, entity.Y);
            _snapshotWorldPositions[entity.EntityId] = nextPosition;
            _entityCurrentHealth[entity.EntityId] = entity.CurrentHealth;
            _entityMaxHealth[entity.EntityId] = entity.MaxHealth;
            if (!_renderWorldPositions.ContainsKey(entity.EntityId))
            {
                _renderWorldPositions[entity.EntityId] = nextPosition;
            }

            if (entity.EntityId == _localEntityId)
            {
                localPositionUpdated = true;
                _lastLocalPosition = nextPosition;
                _localCurrentHealth = entity.CurrentHealth;
                _localMaxHealth = entity.MaxHealth;
                _statusLabel.Text =
                    $"Connected (EntityId: {_localEntityId}) Tick: {snapshot.ServerTick} Pos: {nextPosition.X:0.00}, {nextPosition.Y:0.00} HP: {_localCurrentHealth}/{GetSafeMaxHealth()} Coins: {_coins} Players: {CountPlayers()} NPCs: {CountNpcs()}";
            }
        }

        if (!localPositionUpdated)
        {
            _statusLabel.Text =
                $"Connected (EntityId: {_localEntityId}) Tick: {snapshot.ServerTick} HP: {_localCurrentHealth}/{GetSafeMaxHealth()} Coins: {_coins} Players: {CountPlayers()} NPCs: {CountNpcs()}";
        }
    }

    private void UpdateRenderPositions(double delta)
    {
        if (_state != ConnectionUiState.Connected)
        {
            return;
        }

        var blend = Mathf.Clamp((float)(delta * SnapshotSmoothingRate), 0.0f, 1.0f);
        foreach (var kvp in _snapshotWorldPositions)
        {
            if (!_renderWorldPositions.TryGetValue(kvp.Key, out var current))
            {
                _renderWorldPositions[kvp.Key] = kvp.Value;
                continue;
            }

            _renderWorldPositions[kvp.Key] = current.Lerp(kvp.Value, blend);
        }

        QueueRedraw();
    }

    private void SetUiState(ConnectionUiState state, string statusText)
    {
        _state = state;
        _statusLabel.Text = statusText;

        var isDisconnected = state == ConnectionUiState.Disconnected;
        _addressInput.Editable = isDisconnected;
        _nameInput.Editable = isDisconnected;
        _chatInput.Editable = !isDisconnected;
        _chatSendButton.Disabled = isDisconnected;
        _connectButton.Text = isDisconnected ? "Connect" : "Disconnect";
        QueueRedraw();
    }

    private void HandlePong(S_Pong pong)
    {
        if (!_pendingPingSentAtMs.TryGetValue(pong.PingId, out var sentAt))
        {
            return;
        }

        _pendingPingSentAtMs.Remove(pong.PingId);
        var now = Time.GetTicksMsec();
        var measuredMs = (float)Math.Max(0, now - sentAt);
        _rollingPingMs = _rollingPingMs is null
            ? measuredMs
            : Mathf.Lerp(_rollingPingMs.Value, measuredMs, 0.25f);
    }

    private void UpdateDebugOverlay()
    {
        if (_state != ConnectionUiState.Connected || !_hasLocalEntityId)
        {
            _debugOverlayLabel.Text = "Disconnected";
            return;
        }

        var pingText = _rollingPingMs is null ? "--" : $"{_rollingPingMs.Value:0}";
        var targetText = _selectedTargetEntityId == 0
            ? "none"
            : $"{_selectedTargetEntityId} ({GetEntityHealthText(_selectedTargetEntityId)})";
        _debugOverlayLabel.Text =
            $"Entity: {_localEntityId}  HP: {_localCurrentHealth}/{GetSafeMaxHealth()}  Coins: {_coins}  Players: {CountPlayers()}  NPCs: {CountNpcs()}  Loot: {CountLoots()}  Target: {targetText}  Tick: {_lastServerTick}  Ping: {pingText} ms  Pos: {_lastLocalPosition.X:0.00},{_lastLocalPosition.Y:0.00}";
    }

    private void CleanupStalePendingPings()
    {
        if (_pendingPingSentAtMs.Count == 0)
        {
            return;
        }

        var now = Time.GetTicksMsec();
        var stalePingIds = new List<uint>();
        foreach (var kvp in _pendingPingSentAtMs)
        {
            if (now - kvp.Value > 10000)
            {
                stalePingIds.Add(kvp.Key);
            }
        }

        foreach (var pingId in stalePingIds)
        {
            _pendingPingSentAtMs.Remove(pingId);
        }
    }

    private void HandleEntitySpawn(S_EntitySpawn spawn)
    {
        _entityNames[spawn.EntityId] = spawn.DisplayName;
        _entityKinds[spawn.EntityId] = spawn.EntityKind == EntityKind.Unspecified
            ? EntityKind.Player
            : spawn.EntityKind;
        var initialPosition = new Vector2(spawn.X, spawn.Y);
        _snapshotWorldPositions[spawn.EntityId] = initialPosition;
        if (!_renderWorldPositions.ContainsKey(spawn.EntityId))
        {
            _renderWorldPositions[spawn.EntityId] = initialPosition;
        }

        if (_entityKinds[spawn.EntityId] == EntityKind.Player)
        {
            AppendChatLine($"[System] {spawn.DisplayName} joined.");
        }
    }

    private void HandleEntityDespawn(S_EntityDespawn despawn)
    {
        string? removedName = null;
        if (_entityNames.TryGetValue(despawn.EntityId, out var displayName))
        {
            removedName = displayName;
        }

        _entityKinds.TryGetValue(despawn.EntityId, out var removedKind);
        _entityNames.Remove(despawn.EntityId);
        _entityKinds.Remove(despawn.EntityId);
        _entityCurrentHealth.Remove(despawn.EntityId);
        _entityMaxHealth.Remove(despawn.EntityId);
        _snapshotWorldPositions.Remove(despawn.EntityId);
        _renderWorldPositions.Remove(despawn.EntityId);
        if (_selectedTargetEntityId == despawn.EntityId)
        {
            _selectedTargetEntityId = 0;
        }

        if (_selectedLootEntityId == despawn.EntityId)
        {
            _selectedLootEntityId = 0;
        }

        if (!string.IsNullOrWhiteSpace(removedName) && removedKind == EntityKind.Player)
        {
            AppendChatLine($"[System] {removedName} left.");
        }
    }

    private int CountPlayers()
    {
        var count = 0;
        foreach (var kind in _entityKinds.Values)
        {
            if (kind == EntityKind.Player)
            {
                count++;
            }
        }

        return count;
    }

    private int CountNpcs()
    {
        var count = 0;
        foreach (var kind in _entityKinds.Values)
        {
            if (kind == EntityKind.Npc)
            {
                count++;
            }
        }

        return count;
    }

    private int CountLoots()
    {
        var count = 0;
        foreach (var kind in _entityKinds.Values)
        {
            if (kind == EntityKind.Loot)
            {
                count++;
            }
        }

        return count;
    }

    private void UpdateSelectionsAndCooldowns(double delta)
    {
        if (_state != ConnectionUiState.Connected || !_hasLocalEntityId)
        {
            return;
        }

        _attackCooldownRemainingSeconds = Math.Max(0, _attackCooldownRemainingSeconds - delta);
        _pickupCooldownRemainingSeconds = Math.Max(0, _pickupCooldownRemainingSeconds - delta);

        _selectedTargetEntityId = 0;
        _selectedLootEntityId = 0;
        var bestTargetDistanceSquared = float.MaxValue;
        var bestLootDistanceSquared = float.MaxValue;
        foreach (var kvp in _snapshotWorldPositions)
        {
            var entityId = kvp.Key;
            if (!_entityKinds.TryGetValue(entityId, out var kind))
            {
                continue;
            }

            var deltaVector = kvp.Value - _lastLocalPosition;
            var distanceSquared = deltaVector.LengthSquared();
            if (kind == EntityKind.Npc && distanceSquared <= AttackRangeUnits * AttackRangeUnits)
            {
                if (distanceSquared < bestTargetDistanceSquared)
                {
                    bestTargetDistanceSquared = distanceSquared;
                    _selectedTargetEntityId = entityId;
                }
            }
            else if (kind == EntityKind.Loot && distanceSquared <= PickupRangeUnits * PickupRangeUnits)
            {
                if (distanceSquared < bestLootDistanceSquared)
                {
                    bestLootDistanceSquared = distanceSquared;
                    _selectedLootEntityId = entityId;
                }
            }
        }
    }

    private void TrySendAttack()
    {
        if (_state != ConnectionUiState.Connected || _peer is null || _peer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        if (_selectedTargetEntityId == 0 || _attackCooldownRemainingSeconds > 0 || !Input.IsKeyPressed(Key.Space))
        {
            return;
        }

        var packet = new ClientPacket
        {
            CAttack = new C_Attack
            {
                TargetEntityId = _selectedTargetEntityId
            }
        };

        _peer.Send(packet.ToByteArray(), DeliveryMethod.ReliableOrdered);
        _attackCooldownRemainingSeconds = AttackSendCooldownSeconds;
    }

    private void TrySendPickup()
    {
        if (_state != ConnectionUiState.Connected || _peer is null || _peer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        if (_selectedLootEntityId == 0 || _pickupCooldownRemainingSeconds > 0 || !Input.IsKeyPressed(Key.E))
        {
            return;
        }

        var packet = new ClientPacket
        {
            CPickup = new C_Pickup
            {
                LootEntityId = _selectedLootEntityId
            }
        };

        _peer.Send(packet.ToByteArray(), DeliveryMethod.ReliableOrdered);
        _pickupCooldownRemainingSeconds = PickupSendCooldownSeconds;
    }

    private string GetEntityHealthText(uint entityId)
    {
        if (!_entityCurrentHealth.TryGetValue(entityId, out var currentHealth))
        {
            return "?/?";
        }

        if (!_entityMaxHealth.TryGetValue(entityId, out var maxHealth))
        {
            return $"{currentHealth}/?";
        }

        return $"{currentHealth}/{maxHealth}";
    }

    private uint GetSafeMaxHealth()
    {
        return _localMaxHealth == 0 ? 1u : _localMaxHealth;
    }

    private void TrySendChat()
    {
        if (_state != ConnectionUiState.Connected || _peer is null || _peer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        var text = _chatInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var packet = new ClientPacket
        {
            CChatSend = new C_ChatSend
            {
                Text = text
            }
        };

        _peer.Send(packet.ToByteArray(), DeliveryMethod.ReliableOrdered);
        _chatInput.Text = string.Empty;
    }

    private void AppendChatLine(string text)
    {
        _chatLog.AddItem(text);
        while (_chatLog.ItemCount > MaxChatLines)
        {
            _chatLog.RemoveItem(0);
        }

        if (_chatLog.ItemCount > 0)
        {
            _chatLog.Select(_chatLog.ItemCount - 1, false);
            _chatLog.EnsureCurrentIsVisible();
        }
    }

    private static bool TryValidateDisplayName(string input, out string sanitized)
    {
        sanitized = (input ?? string.Empty).Trim();
        if (sanitized.Length is < 1 or > MaxDisplayNameLength)
        {
            return false;
        }

        foreach (var ch in sanitized)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseAddress(string input, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        var trimmed = (input ?? string.Empty).Trim();
        var separator = trimmed.LastIndexOf(':');
        if (separator <= 0 || separator >= trimmed.Length - 1)
        {
            return false;
        }

        host = trimmed[..separator].Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return int.TryParse(trimmed[(separator + 1)..], out port) && port is > 0 and <= 65535;
    }

    private enum ConnectionUiState
    {
        Disconnected,
        Connecting,
        Connected
    }
}
