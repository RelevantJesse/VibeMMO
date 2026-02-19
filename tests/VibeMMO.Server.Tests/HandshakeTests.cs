using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using LiteNetLib;
using LiteNetLib.Utils;
using VibeMMO.Server;
using VibeMMO.Shared.Protocol.V0;

namespace VibeMMO.Server.Tests;

public class HandshakeTests
{
    private const uint ProtocolVersion = 1;

    [Fact]
    public void Hello_WithMatchingProtocol_ReceivesWelcome()
    {
        var response = ExchangeHello(ProtocolVersion, "alice");

        Assert.NotNull(response);
        Assert.Equal(ServerPacket.PayloadOneofCase.SWelcome, response!.PayloadCase);
        Assert.Equal(ProtocolVersion, response.SWelcome.ProtocolVersion);
        Assert.True(response.SWelcome.YourEntityId > 0);
        Assert.Equal(20u, response.SWelcome.TickRateHz);
        Assert.Equal(10u, response.SWelcome.SnapshotRateHz);
        Assert.True(response.SWelcome.ReconnectToken.Length > 0);
    }

    [Fact]
    public void Hello_WithMismatchedProtocol_ReceivesReject()
    {
        var response = ExchangeHello(999, "alice");

        Assert.NotNull(response);
        Assert.Equal(ServerPacket.PayloadOneofCase.SReject, response!.PayloadCase);
        Assert.Contains("Protocol mismatch", response.SReject.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static ServerPacket? ExchangeHello(uint protocolVersion, string displayName)
    {
        var port = ReserveUdpPort();
        using var server = new HandshakeServer(port);
        Assert.True(server.Start());

        var listener = new EventBasedNetListener();
        var client = new NetManager(listener)
        {
            AutoRecycle = false
        };

        ServerPacket? response = null;
        var helloSent = false;

        listener.PeerConnectedEvent += peer =>
        {
            var hello = new ClientPacket
            {
                CHello = new C_Hello
                {
                    ProtocolVersion = protocolVersion,
                    DisplayName = displayName,
                    ReconnectToken = ByteString.Empty
                }
            };

            peer.Send(hello.ToByteArray(), DeliveryMethod.ReliableOrdered);
            helloSent = true;
        };

        listener.NetworkReceiveEvent += (_, reader, _, _) =>
        {
            try
            {
                var packet = ServerPacket.Parser.ParseFrom(reader.GetRemainingBytes());
                if (packet.PayloadCase is ServerPacket.PayloadOneofCase.SWelcome or ServerPacket.PayloadOneofCase.SReject)
                {
                    response = packet;
                }
            }
            finally
            {
                reader.Recycle();
            }
        };

        try
        {
            Assert.True(client.Start());
            client.Connect("127.0.0.1", port, string.Empty);

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(3) && response is null)
            {
                server.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }
        }
        finally
        {
            client.Stop();
            server.Stop();
        }

        Assert.True(helloSent);
        return response;
    }

    private static int ReserveUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
