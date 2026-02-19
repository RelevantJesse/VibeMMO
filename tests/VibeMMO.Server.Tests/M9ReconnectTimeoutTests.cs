using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using LiteNetLib;
using LiteNetLib.Utils;
using VibeMMO.Server;
using VibeMMO.Shared.Protocol.V0;

namespace VibeMMO.Server.Tests;

public class M9ReconnectTimeoutTests
{
    private const uint ProtocolVersion = 1;

    [Fact]
    public void DeadClient_TimesOutAndIsCleanedUp()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vibemmo-m9-timeout-{Guid.NewGuid():N}.db");
        var port = ReserveUdpPort();
        try
        {
            using var server = new HandshakeServer(
                port,
                dbPath,
                disconnectTimeoutMs: 400,
                reconnectGraceWindow: TimeSpan.FromSeconds(2));
            Assert.True(server.Start());

            using var observer = new TestClient("observer", Array.Empty<byte>());
            using var target = new TestClient("target", Array.Empty<byte>());

            observer.Connect(port);
            target.Connect(port);
            PumpUntil(
                server,
                TimeSpan.FromSeconds(5),
                () => observer.TryGetWelcome(out _) && target.TryGetWelcome(out _),
                observer,
                target);

            Assert.True(target.TryGetWelcome(out var targetWelcome));
            var targetEntityId = targetWelcome!.YourEntityId;
            Assert.True(server.TryGetPlayerState(targetEntityId, out _));

            target.FreezeWithoutDisconnect();
            PumpUntil(
                server,
                TimeSpan.FromSeconds(12),
                () => !server.TryGetPlayerState(targetEntityId, out _),
                observer);

            Assert.False(server.TryGetPlayerState(targetEntityId, out _));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public void ReconnectWithinGrace_ReclaimsSameEntity()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vibemmo-m9-grace-{Guid.NewGuid():N}.db");
        var port = ReserveUdpPort();
        try
        {
            using var server = new HandshakeServer(
                port,
                dbPath,
                disconnectTimeoutMs: 400,
                reconnectGraceWindow: TimeSpan.FromSeconds(3));
            Assert.True(server.Start());

            S_Welcome originalWelcome;
            float preTimeoutX;
            using (var original = new TestClient("alice", Array.Empty<byte>()))
            {
                original.Connect(port);
                PumpUntil(server, TimeSpan.FromSeconds(5), () => original.TryGetWelcome(out _), original);
                Assert.True(original.TryGetWelcome(out var welcomeFromOriginal));
                originalWelcome = welcomeFromOriginal!;

                var moveClock = Stopwatch.StartNew();
                while (moveClock.Elapsed < TimeSpan.FromMilliseconds(900))
                {
                    server.PollEvents();
                    original.PollEvents();
                    original.TrySendMoveInput(1.0f, 0.0f);
                    Thread.Sleep(5);
                }

                Assert.True(original.TryGetLatestPosition(originalWelcome.YourEntityId, out var movedPosition));
                preTimeoutX = movedPosition.X;
                original.FreezeWithoutDisconnect();
                PumpUntil(server, TimeSpan.FromSeconds(2), () => !server.TryGetPlayerState(originalWelcome.YourEntityId, out _));
            }

            using var reconnect = new TestClient("alice", originalWelcome.ReconnectToken.ToByteArray());
            reconnect.Connect(port);
            PumpUntil(server, TimeSpan.FromSeconds(5), () => reconnect.TryGetWelcome(out _), reconnect);

            Assert.True(reconnect.TryGetWelcome(out var reconnectWelcome));
            Assert.Equal(originalWelcome.YourEntityId, reconnectWelcome!.YourEntityId);
            Assert.Equal(originalWelcome.ReconnectToken.ToByteArray(), reconnectWelcome.ReconnectToken.ToByteArray());

            PumpUntil(
                server,
                TimeSpan.FromSeconds(3),
                () => reconnect.TryGetLatestPosition(reconnectWelcome.YourEntityId, out var pos) && pos.X > 0.2f,
                reconnect);

            Assert.True(reconnect.TryGetLatestPosition(reconnectWelcome.YourEntityId, out var restoredPosition));
            Assert.True(restoredPosition.X >= preTimeoutX);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    private static void PumpUntil(HandshakeServer server, TimeSpan timeout, Func<bool> condition, params TestClient[] clients)
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

    private static void TryDelete(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private sealed class TestClient : IDisposable
    {
        private readonly string _displayName;
        private readonly byte[] _reconnectToken;
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly List<ServerPacket> _receivedPackets = [];
        private readonly Stopwatch _moveClock = Stopwatch.StartNew();
        private NetPeer? _peer;
        private bool _isFrozen;
        private uint _nextMoveSeq = 1;
        private TimeSpan _lastMoveSend = TimeSpan.Zero;

        public TestClient(string displayName, byte[] reconnectToken)
        {
            _displayName = displayName;
            _reconnectToken = reconnectToken;
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
                            DisplayName = _displayName,
                            ReconnectToken = ByteString.CopyFrom(_reconnectToken)
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

        public void Connect(int port)
        {
            Assert.True(_client.Start());
            _client.Connect("127.0.0.1", port, string.Empty);
        }

        public void FreezeWithoutDisconnect()
        {
            _isFrozen = true;
        }

        public void PollEvents()
        {
            if (_isFrozen)
            {
                return;
            }

            _client.PollEvents();
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

        public bool HasDespawn(uint entityId)
        {
            foreach (var packet in _receivedPackets)
            {
                if (packet.PayloadCase == ServerPacket.PayloadOneofCase.SEntityDespawn &&
                    packet.SEntityDespawn.EntityId == entityId)
                {
                    return true;
                }
            }

            return false;
        }

        public void TrySendMoveInput(float moveX, float moveY)
        {
            if (_isFrozen || _peer is null || _peer.ConnectionState != ConnectionState.Connected)
            {
                return;
            }

            if (_moveClock.Elapsed - _lastMoveSend < TimeSpan.FromMilliseconds(50))
            {
                return;
            }

            _peer.Send(
                new ClientPacket
                {
                    CMoveInput = new C_MoveInput
                    {
                        Seq = _nextMoveSeq++,
                        MoveX = moveX,
                        MoveY = moveY
                    }
                }.ToByteArray(),
                DeliveryMethod.Sequenced);
            _lastMoveSend = _moveClock.Elapsed;
        }

        public bool TryGetLatestPosition(uint entityId, out Vector2Position position)
        {
            for (var i = _receivedPackets.Count - 1; i >= 0; i--)
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
                        position = new Vector2Position(entity.X, entity.Y);
                        return true;
                    }
                }
            }

            position = default;
            return false;
        }

        public void Dispose()
        {
            _client.Stop();
        }
    }

    private readonly record struct Vector2Position(float X, float Y);
}
