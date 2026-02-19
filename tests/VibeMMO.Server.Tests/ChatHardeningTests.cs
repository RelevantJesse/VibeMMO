using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using LiteNetLib;
using LiteNetLib.Utils;
using VibeMMO.Server;
using VibeMMO.Shared.Protocol.V0;

namespace VibeMMO.Server.Tests;

public class ChatHardeningTests
{
    private const uint ProtocolVersion = 1;

    [Fact]
    public void OversizedChat_IsRejectedWithoutBroadcast()
    {
        var port = ReserveUdpPort();
        using var server = new HandshakeServer(port);
        Assert.True(server.Start());

        using var sender = new TestClient("alice");
        using var receiver = new TestClient("bob");
        sender.Connect(port);
        receiver.Connect(port);

        PumpUntil(
            server,
            TimeSpan.FromSeconds(5),
            () => sender.TryGetWelcome(out _) && receiver.TryGetWelcome(out _),
            sender,
            receiver);

        var beforeSender = sender.ChatBroadcastCount;
        var beforeReceiver = receiver.ChatBroadcastCount;

        sender.SendChat(new string('x', 161));
        PumpUntil(
            server,
            TimeSpan.FromMilliseconds(700),
            () => false,
            sender,
            receiver);

        Assert.Equal(beforeSender, sender.ChatBroadcastCount);
        Assert.Equal(beforeReceiver, receiver.ChatBroadcastCount);
    }

    [Fact]
    public void RapidFireChat_IsRateLimited()
    {
        var port = ReserveUdpPort();
        using var server = new HandshakeServer(port);
        Assert.True(server.Start());

        using var sender = new TestClient("alice");
        using var receiver = new TestClient("bob");
        sender.Connect(port);
        receiver.Connect(port);

        PumpUntil(
            server,
            TimeSpan.FromSeconds(5),
            () => sender.TryGetWelcome(out _) && receiver.TryGetWelcome(out _),
            sender,
            receiver);

        Assert.True(sender.TryGetWelcome(out var senderWelcome));
        for (var i = 0; i < 20; i++)
        {
            sender.SendChat($"msg-{i}");
        }

        PumpUntil(
            server,
            TimeSpan.FromSeconds(2),
            () => receiver.CountChatsFrom(senderWelcome!.YourEntityId) >= 8,
            sender,
            receiver);

        var receivedFromSender = receiver.CountChatsFrom(senderWelcome!.YourEntityId);
        Assert.InRange(receivedFromSender, 1, 8);
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
                    _receivedPackets.Add(ServerPacket.Parser.ParseFrom(reader.GetRemainingBytes()));
                }
                finally
                {
                    reader.Recycle();
                }
            };
        }

        public int ChatBroadcastCount =>
            _receivedPackets.Count(packet => packet.PayloadCase == ServerPacket.PayloadOneofCase.SChatBroadcast);

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

        public void SendChat(string text)
        {
            Assert.NotNull(_peer);
            _peer!.Send(
                new ClientPacket
                {
                    CChatSend = new C_ChatSend
                    {
                        Text = text
                    }
                }.ToByteArray(),
                DeliveryMethod.ReliableOrdered);
        }

        public int CountChatsFrom(uint entityId)
        {
            var count = 0;
            foreach (var packet in _receivedPackets)
            {
                if (packet.PayloadCase == ServerPacket.PayloadOneofCase.SChatBroadcast &&
                    packet.SChatBroadcast.FromEntityId == entityId)
                {
                    count++;
                }
            }

            return count;
        }

        public void Dispose()
        {
            _client.Stop();
        }
    }
}
