using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using LiteNetLib;
using LiteNetLib.Utils;
using VibeMMO.Server;
using VibeMMO.Shared.Protocol.V0;

namespace VibeMMO.Server.Tests;

public class M8PersistenceTests
{
    private const uint ProtocolVersion = 1;

    [Fact]
    public void ReconnectToken_RestoresLastSavedPosition()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vibemmo-m8-{Guid.NewGuid():N}.db");
        var port = ReserveUdpPort();

        try
        {
            using var server = new HandshakeServer(port, dbPath);
            Assert.True(server.Start());

            S_Welcome initialWelcome;
            Vector2Position preDisconnectPosition;
            using (var firstClient = new TestClient("alice", Array.Empty<byte>()))
            {
                firstClient.Connect(port);
                PumpUntil(server, TimeSpan.FromSeconds(5), () => firstClient.TryGetWelcome(out _), firstClient);
                Assert.True(firstClient.TryGetWelcome(out var welcomeFromFirstClient));
                initialWelcome = welcomeFromFirstClient!;

                var movementWindow = Stopwatch.StartNew();
                while (movementWindow.Elapsed < TimeSpan.FromMilliseconds(900))
                {
                    server.PollEvents();
                    firstClient.PollEvents();
                    firstClient.TrySendMoveInput(1.0f, 0.0f);
                    Thread.Sleep(5);
                }

                Assert.True(firstClient.TryGetLatestPosition(initialWelcome.YourEntityId, out preDisconnectPosition));
                Assert.True(preDisconnectPosition.X > 0.5f);
            }

            PumpUntil(server, TimeSpan.FromMilliseconds(400), () => false);
            Assert.True(File.Exists(dbPath));

            using var reconnectClient = new TestClient("alice", initialWelcome.ReconnectToken.ToByteArray());
            reconnectClient.Connect(port);
            PumpUntil(server, TimeSpan.FromSeconds(5), () => reconnectClient.TryGetWelcome(out _), reconnectClient);

            Assert.True(reconnectClient.TryGetWelcome(out var reconnectWelcome));
            Assert.Equal(initialWelcome.ReconnectToken.ToByteArray(), reconnectWelcome!.ReconnectToken.ToByteArray());

            var restoredEntityId = reconnectWelcome.YourEntityId;
            PumpUntil(
                server,
                TimeSpan.FromSeconds(5),
                () =>
                    reconnectClient.TryGetLatestPosition(restoredEntityId, out var restored) &&
                    restored.X > 0.5f,
                reconnectClient);

            Assert.True(reconnectClient.TryGetLatestPosition(restoredEntityId, out var restoredPosition));
            Assert.InRange(MathF.Abs(restoredPosition.X - preDisconnectPosition.X), 0.0f, 1.5f);
            Assert.InRange(MathF.Abs(restoredPosition.Y - preDisconnectPosition.Y), 0.0f, 0.2f);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try
                {
                    File.Delete(dbPath);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static void PumpUntil(HandshakeServer server, TimeSpan timeout, Func<bool> condition, params TestClient[] clients)
    {
        if (condition())
        {
            return;
        }

        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout && !condition())
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

    private readonly record struct Vector2Position(float X, float Y);

    private sealed class TestClient : IDisposable
    {
        private readonly string _displayName;
        private readonly byte[] _reconnectToken;
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private NetPeer? _peer;
        private uint _nextMoveSeq = 1;
        private TimeSpan _lastMoveSend = TimeSpan.Zero;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<ServerPacket> _receivedPackets = [];

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

        public void PollEvents()
        {
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

        public void TrySendMoveInput(float moveX, float moveY)
        {
            if (_peer is null || _peer.ConnectionState != ConnectionState.Connected)
            {
                return;
            }

            if (_clock.Elapsed - _lastMoveSend < TimeSpan.FromMilliseconds(50))
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
            _lastMoveSend = _clock.Elapsed;
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
}
