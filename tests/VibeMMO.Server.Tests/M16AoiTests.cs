using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using LiteNetLib;
using LiteNetLib.Utils;
using VibeMMO.Server;
using VibeMMO.Shared.Protocol.V0;

namespace VibeMMO.Server.Tests;

public class M16AoiTests
{
    private const uint ProtocolVersion = 1;

    [Fact]
    public void PlayersFarApart_DoNotReceiveEachOtherSnapshotOrVisibility()
    {
        var port = ReserveUdpPort();
        using var server = new HandshakeServer(port);
        Assert.True(server.Start());

        using var firstClient = new TestClient("a");
        using var secondClient = new TestClient("b");

        firstClient.Connect(port);
        secondClient.Connect(port);
        PumpUntil(
            server,
            TimeSpan.FromSeconds(5),
            () => firstClient.TryGetWelcome(out _) && secondClient.TryGetWelcome(out _),
            firstClient,
            secondClient);

        Assert.True(firstClient.TryGetWelcome(out var firstWelcome));
        Assert.True(secondClient.TryGetWelcome(out var secondWelcome));
        var firstEntityId = firstWelcome!.YourEntityId;
        var secondEntityId = secondWelcome!.YourEntityId;

        PumpUntil(
            server,
            TimeSpan.FromSeconds(5),
            () => firstClient.HasSpawn(secondEntityId) && secondClient.HasSpawn(firstEntityId),
            firstClient,
            secondClient);

        Assert.True(firstClient.HasSpawn(secondEntityId));
        Assert.True(secondClient.HasSpawn(firstEntityId));

        var separationCheckpoint = firstClient.PacketCount;
        secondClient.SetMoveInput(1.0f, 0.0f);
        PumpUntil(server, TimeSpan.FromMilliseconds(2600), () => false, firstClient, secondClient);
        secondClient.SetMoveInput(0.0f, 0.0f);

        PumpUntil(
            server,
            TimeSpan.FromSeconds(5),
            () => firstClient.HasDespawnSince(secondEntityId, separationCheckpoint),
            firstClient,
            secondClient);
        Assert.True(firstClient.HasDespawnSince(secondEntityId, separationCheckpoint));

        var despawnCheckpoint = firstClient.PacketCount;
        PumpUntil(server, TimeSpan.FromMilliseconds(700), () => false, firstClient, secondClient);
        Assert.False(firstClient.SnapshotContainsEntitySince(secondEntityId, despawnCheckpoint));

        secondClient.SetMoveInput(-1.0f, 0.0f);
        PumpUntil(
            server,
            TimeSpan.FromSeconds(5),
            () => firstClient.HasSpawnSince(secondEntityId, despawnCheckpoint),
            firstClient,
            secondClient);
        secondClient.SetMoveInput(0.0f, 0.0f);
        Assert.True(firstClient.HasSpawnSince(secondEntityId, despawnCheckpoint));
    }

    private static void PumpUntil(
        HandshakeServer server,
        TimeSpan timeout,
        Func<bool> condition,
        params TestClient[] clients)
    {
        if (condition())
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout && !condition())
        {
            server.PollEvents();
            foreach (var client in clients)
            {
                client.PollEvents();
            }

            Thread.Sleep(5);
        }
    }

    private static int ReserveUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private sealed class TestClient : IDisposable
    {
        private readonly string _name;
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly List<ServerPacket> _receivedPackets = [];
        private readonly Stopwatch _moveClock = Stopwatch.StartNew();
        private NetPeer? _peer;
        private TimeSpan _lastMoveSent = TimeSpan.Zero;
        private uint _nextMoveSeq = 1;
        private float _moveX;
        private float _moveY;

        public TestClient(string name)
        {
            _name = name;
            _client = new NetManager(_listener)
            {
                AutoRecycle = false
            };

            _listener.PeerConnectedEvent += peer =>
            {
                _peer = peer;
                peer.Send(
                    new ClientPacket
                    {
                        CHello = new C_Hello
                        {
                            ProtocolVersion = ProtocolVersion,
                            DisplayName = _name,
                            ReconnectToken = ByteString.Empty
                        }
                    }.ToByteArray(),
                    DeliveryMethod.ReliableOrdered);
            };

            _listener.NetworkReceiveEvent += (_, reader, _, _) =>
            {
                try
                {
                    _receivedPackets.Add(ServerPacket.Parser.ParseFrom(reader.GetRemainingBytes()));
                }
                finally
                {
                    reader.Recycle();
                }
            };
        }

        public int PacketCount => _receivedPackets.Count;

        public void Connect(int port)
        {
            Assert.True(_client.Start());
            _client.Connect("127.0.0.1", port, string.Empty);
        }

        public void SetMoveInput(float moveX, float moveY)
        {
            _moveX = moveX;
            _moveY = moveY;
        }

        public void PollEvents()
        {
            _client.PollEvents();
            if (_peer is null || _peer.ConnectionState != ConnectionState.Connected)
            {
                return;
            }

            if (_moveClock.Elapsed - _lastMoveSent < TimeSpan.FromMilliseconds(50))
            {
                return;
            }

            _peer.Send(
                new ClientPacket
                {
                    CMoveInput = new C_MoveInput
                    {
                        Seq = _nextMoveSeq++,
                        MoveX = _moveX,
                        MoveY = _moveY
                    }
                }.ToByteArray(),
                DeliveryMethod.Sequenced);
            _lastMoveSent = _moveClock.Elapsed;
        }

        public bool TryGetWelcome(out S_Welcome? welcome)
        {
            foreach (var packet in _receivedPackets)
            {
                if (packet.PayloadCase == ServerPacket.PayloadOneofCase.SWelcome)
                {
                    welcome = packet.SWelcome;
                    return true;
                }
            }

            welcome = null;
            return false;
        }

        public bool HasSpawn(uint entityId)
        {
            foreach (var packet in _receivedPackets)
            {
                if (packet.PayloadCase == ServerPacket.PayloadOneofCase.SEntitySpawn &&
                    packet.SEntitySpawn.EntityId == entityId)
                {
                    return true;
                }
            }

            return false;
        }

        public bool HasSpawnSince(uint entityId, int startIndex)
        {
            for (var i = startIndex; i < _receivedPackets.Count; i++)
            {
                var packet = _receivedPackets[i];
                if (packet.PayloadCase == ServerPacket.PayloadOneofCase.SEntitySpawn &&
                    packet.SEntitySpawn.EntityId == entityId)
                {
                    return true;
                }
            }

            return false;
        }

        public bool HasDespawnSince(uint entityId, int startIndex)
        {
            for (var i = startIndex; i < _receivedPackets.Count; i++)
            {
                var packet = _receivedPackets[i];
                if (packet.PayloadCase == ServerPacket.PayloadOneofCase.SEntityDespawn &&
                    packet.SEntityDespawn.EntityId == entityId)
                {
                    return true;
                }
            }

            return false;
        }

        public bool SnapshotContainsEntitySince(uint entityId, int startIndex)
        {
            for (var i = startIndex; i < _receivedPackets.Count; i++)
            {
                var packet = _receivedPackets[i];
                if (packet.PayloadCase != ServerPacket.PayloadOneofCase.SSnapshot)
                {
                    continue;
                }

                foreach (var entity in packet.SSnapshot.Entities)
                {
                    if (entity.EntityId == entityId)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public void Dispose()
        {
            _client.Stop();
        }
    }
}
