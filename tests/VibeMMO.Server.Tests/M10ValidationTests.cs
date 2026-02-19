using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using LiteNetLib;
using LiteNetLib.Utils;
using VibeMMO.Server;
using VibeMMO.Shared.Protocol.V0;

namespace VibeMMO.Server.Tests;

public class M10ValidationTests
{
    private const uint ProtocolVersion = 1;

    [Fact]
    public void RandomMalformedBytes_DoNotCrashServer_AndNewClientCanConnect()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vibemmo-m10-malformed-{Guid.NewGuid():N}.db");
        var port = ReserveUdpPort();
        try
        {
            using var server = new HandshakeServer(port, dbPath);
            Assert.True(server.Start());

            using var attacker = new RawClient();
            attacker.Connect(port);
            PumpUntil(server, TimeSpan.FromSeconds(3), () => attacker.IsConnected, attacker);
            Assert.True(attacker.IsConnected);

            for (var i = 0; i < 6; i++)
            {
                attacker.SendRaw([0xFF, 0x00, 0xAA, (byte)i]);
            }

            PumpUntil(server, TimeSpan.FromMilliseconds(500), () => false, attacker);

            using var legit = new HandshakeClient("alice", Array.Empty<byte>());
            legit.Connect(port);
            PumpUntil(server, TimeSpan.FromSeconds(5), () => legit.TryGetWelcome(out _), attacker, legit);

            Assert.True(legit.TryGetWelcome(out _));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public void OversizedDatagram_IsDroppedAndPeerDisconnected()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vibemmo-m10-oversized-{Guid.NewGuid():N}.db");
        var port = ReserveUdpPort();
        try
        {
            using var server = new HandshakeServer(port, dbPath);
            Assert.True(server.Start());

            using var attacker = new RawClient();
            attacker.Connect(port);
            PumpUntil(server, TimeSpan.FromSeconds(3), () => attacker.IsConnected, attacker);
            Assert.True(attacker.IsConnected);

            attacker.SendRaw(new byte[1400]);
            PumpUntil(server, TimeSpan.FromSeconds(3), () => attacker.WasDisconnected, attacker);
            Assert.True(attacker.WasDisconnected);

            using var legit = new HandshakeClient("bob", Array.Empty<byte>());
            legit.Connect(port);
            PumpUntil(server, TimeSpan.FromSeconds(5), () => legit.TryGetWelcome(out _), legit);
            Assert.True(legit.TryGetWelcome(out _));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public void PacketFlood_TriggersPerConnectionRateLimit()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vibemmo-m10-flood-{Guid.NewGuid():N}.db");
        var port = ReserveUdpPort();
        try
        {
            using var server = new HandshakeServer(port, dbPath);
            Assert.True(server.Start());

            using var attacker = new RawClient();
            attacker.Connect(port);
            PumpUntil(server, TimeSpan.FromSeconds(3), () => attacker.IsConnected, attacker);
            Assert.True(attacker.IsConnected);

            for (var i = 0; i < 250; i++)
            {
                attacker.SendRaw([0x01, 0x02, 0x03, (byte)i]);
            }

            PumpUntil(server, TimeSpan.FromSeconds(3), () => attacker.WasDisconnected, attacker);
            Assert.True(attacker.WasDisconnected);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    private static void PumpUntil(HandshakeServer server, TimeSpan timeout, Func<bool> condition, params IPollableClient[] clients)
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

    private interface IPollableClient
    {
        void PollEvents();
    }

    private sealed class RawClient : IDisposable, IPollableClient
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private NetPeer? _peer;

        public RawClient()
        {
            _client = new NetManager(_listener)
            {
                AutoRecycle = false
            };

            _listener.PeerConnectedEvent += peer =>
            {
                _peer = peer;
            };

            _listener.PeerDisconnectedEvent += (_, _) =>
            {
                WasDisconnected = true;
                _peer = null;
            };
        }

        public bool IsConnected => _peer is not null && _peer.ConnectionState == ConnectionState.Connected;

        public bool WasDisconnected { get; private set; }

        public void Connect(int port)
        {
            Assert.True(_client.Start());
            _client.Connect("127.0.0.1", port, string.Empty);
        }

        public void PollEvents()
        {
            _client.PollEvents();
        }

        public void SendRaw(byte[] payload)
        {
            if (_peer is null || _peer.ConnectionState != ConnectionState.Connected)
            {
                return;
            }

            _peer.Send(payload, DeliveryMethod.ReliableOrdered);
        }

        public void Dispose()
        {
            _client.Stop();
        }
    }

    private sealed class HandshakeClient : IDisposable, IPollableClient
    {
        private readonly string _displayName;
        private readonly byte[] _reconnectToken;
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly List<ServerPacket> _receivedPackets = [];

        public HandshakeClient(string displayName, byte[] reconnectToken)
        {
            _displayName = displayName;
            _reconnectToken = reconnectToken;
            _client = new NetManager(_listener)
            {
                AutoRecycle = false
            };

            _listener.PeerConnectedEvent += peer =>
            {
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

        public void Dispose()
        {
            _client.Stop();
        }
    }
}
