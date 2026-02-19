using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using LiteNetLib;
using LiteNetLib.Utils;
using VibeMMO.Server;
using VibeMMO.Shared.Protocol.V0;

namespace VibeMMO.Server.Tests;

public class M6IntegrationTests
{
    private const uint ProtocolVersion = 1;

    [Fact]
    public void TwoClients_SyncSpawnChatAndDespawn()
    {
        var port = ReserveUdpPort();
        using var server = new HandshakeServer(port);
        Assert.True(server.Start());

        using var firstClient = new TestClient("alice");
        using var secondClient = new TestClient("bob");

        firstClient.Connect(port);
        PumpUntil(
            server,
            TimeSpan.FromSeconds(5),
            () => firstClient.TryGetWelcome(out _));

        secondClient.Connect(port);
        PumpUntil(
            server,
            TimeSpan.FromSeconds(5),
            () => firstClient.TryGetWelcome(out _) && secondClient.TryGetWelcome(out _));

        Assert.True(firstClient.TryGetWelcome(out var firstWelcome));
        Assert.True(secondClient.TryGetWelcome(out var secondWelcome));

        PumpUntil(
            server,
            TimeSpan.FromSeconds(5),
            () => firstClient.HasSpawn(secondWelcome!.YourEntityId) && secondClient.HasSpawn(firstWelcome!.YourEntityId));

        Assert.True(firstClient.HasSpawn(secondWelcome!.YourEntityId));
        Assert.True(secondClient.HasSpawn(firstWelcome!.YourEntityId));

        firstClient.SendChat("hello from alice");
        PumpUntil(
            server,
            TimeSpan.FromSeconds(5),
            () =>
                firstClient.HasChatFrom(firstWelcome!.YourEntityId, "hello from alice") &&
                secondClient.HasChatFrom(firstWelcome!.YourEntityId, "hello from alice"));

        Assert.True(firstClient.HasChatFrom(firstWelcome!.YourEntityId, "hello from alice"));
        Assert.True(secondClient.HasChatFrom(firstWelcome!.YourEntityId, "hello from alice"));

        secondClient.Stop();
        PumpUntil(
            server,
            TimeSpan.FromSeconds(5),
            () => firstClient.HasDespawn(secondWelcome!.YourEntityId),
            pollSecondClient: false);

        Assert.True(firstClient.HasDespawn(secondWelcome!.YourEntityId));
    }

    private static void PumpUntil(
        HandshakeServer server,
        TimeSpan timeout,
        Func<bool> condition,
        bool pollSecondClient = true)
    {
        if (condition())
        {
            return;
        }

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout && !condition())
        {
            server.PollEvents();
            TestClient.PollAll(pollSecondClient);
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
        private static readonly List<TestClient> Clients = [];

        private readonly string _name;
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly List<ServerPacket> _receivedPackets = [];
        private NetPeer? _peer;

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
                var hello = new ClientPacket
                {
                    CHello = new C_Hello
                    {
                        ProtocolVersion = ProtocolVersion,
                        DisplayName = _name,
                        ReconnectToken = ByteString.Empty
                    }
                };

                peer.Send(hello.ToByteArray(), DeliveryMethod.ReliableOrdered);
            };

            _listener.NetworkReceiveEvent += (_, reader, _, _) =>
            {
                try
                {
                    var packet = ServerPacket.Parser.ParseFrom(reader.GetRemainingBytes());
                    _receivedPackets.Add(packet);
                }
                finally
                {
                    reader.Recycle();
                }
            };

            Clients.Add(this);
        }

        public void Connect(int port)
        {
            Assert.True(_client.Start());
            _client.Connect("127.0.0.1", port, string.Empty);
        }

        public void Stop()
        {
            _client.Stop();
        }

        public void SendChat(string text)
        {
            Assert.NotNull(_peer);
            var packet = new ClientPacket
            {
                CChatSend = new C_ChatSend
                {
                    Text = text
                }
            };

            _peer!.Send(packet.ToByteArray(), DeliveryMethod.ReliableOrdered);
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

        public bool HasChatFrom(uint entityId, string text)
        {
            foreach (var packet in _receivedPackets)
            {
                if (packet.PayloadCase == ServerPacket.PayloadOneofCase.SChatBroadcast &&
                    packet.SChatBroadcast.FromEntityId == entityId &&
                    packet.SChatBroadcast.Text == text)
                {
                    return true;
                }
            }

            return false;
        }

        public static void PollAll(bool includeAllClients)
        {
            foreach (var client in Clients)
            {
                if (!includeAllClients && client != Clients[0])
                {
                    continue;
                }

                client._client.PollEvents();
            }
        }

        public void Dispose()
        {
            Stop();
            Clients.Remove(this);
        }
    }
}
