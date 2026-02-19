using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using LiteNetLib;
using LiteNetLib.Utils;
using VibeMMO.Server;
using VibeMMO.Shared.Protocol.V0;

namespace VibeMMO.Server.Tests;

public class MovementTests
{
    private const uint ProtocolVersion = 1;

    [Fact]
    public void MoveInput_UsesConsistentSpeed_AndClampsOversizedVectors()
    {
        var baseline = RunMovementScenario(1.0f, 0.0f, TimeSpan.FromMilliseconds(700));
        var oversized = RunMovementScenario(8.0f, 0.0f, TimeSpan.FromMilliseconds(700));

        Assert.True(baseline.PositionX > 0.5f);
        Assert.InRange(baseline.VelocityX, 4.75f, 5.25f);
        Assert.InRange(MathF.Abs(baseline.VelocityY), 0.0f, 0.01f);
        Assert.True(baseline.LastProcessedInputSeq > 0);

        Assert.True(oversized.PositionX > 0.5f);
        Assert.InRange(oversized.VelocityX, 4.75f, 5.25f);
        Assert.InRange(MathF.Abs(oversized.VelocityY), 0.0f, 0.01f);
        Assert.True(oversized.LastProcessedInputSeq > 0);
    }

    [Fact]
    public void MoveInput_IgnoresInvalidValues()
    {
        var invalid = RunMovementScenario(float.NaN, 0.0f, TimeSpan.FromMilliseconds(700));

        Assert.InRange(MathF.Abs(invalid.PositionX), 0.0f, 0.05f);
        Assert.InRange(MathF.Abs(invalid.PositionY), 0.0f, 0.05f);
        Assert.InRange(MathF.Abs(invalid.VelocityX), 0.0f, 0.01f);
        Assert.InRange(MathF.Abs(invalid.VelocityY), 0.0f, 0.01f);
        Assert.Equal(0u, invalid.LastProcessedInputSeq);
    }

    private static PlayerStateSnapshot RunMovementScenario(float moveX, float moveY, TimeSpan runDuration)
    {
        var port = ReserveUdpPort();
        using var server = new HandshakeServer(port);
        Assert.True(server.Start());

        var listener = new EventBasedNetListener();
        var client = new NetManager(listener)
        {
            AutoRecycle = false
        };

        NetPeer? connectedPeer = null;
        uint? entityId = null;
        uint nextMoveSeq = 1;
        var overallTimeout = Stopwatch.StartNew();
        Stopwatch? movementClock = null;
        var sendInterval = TimeSpan.FromMilliseconds(50);
        var lastMoveSend = TimeSpan.Zero;
        PlayerStateSnapshot? finalState = null;

        listener.PeerConnectedEvent += peer =>
        {
            connectedPeer = peer;
            var hello = new ClientPacket
            {
                CHello = new C_Hello
                {
                    ProtocolVersion = ProtocolVersion,
                    DisplayName = "movement-tester",
                    ReconnectToken = ByteString.Empty
                }
            };

            peer.Send(hello.ToByteArray(), DeliveryMethod.ReliableOrdered);
        };

        listener.NetworkReceiveEvent += (_, reader, _, _) =>
        {
            try
            {
                var packet = ServerPacket.Parser.ParseFrom(reader.GetRemainingBytes());
                if (packet.PayloadCase == ServerPacket.PayloadOneofCase.SWelcome)
                {
                    entityId = packet.SWelcome.YourEntityId;
                    movementClock ??= Stopwatch.StartNew();
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

            while (overallTimeout.Elapsed < TimeSpan.FromSeconds(5))
            {
                server.PollEvents();
                client.PollEvents();

                if (entityId is not null && connectedPeer is not null && movementClock is not null)
                {
                    if (movementClock.Elapsed >= runDuration)
                    {
                        break;
                    }

                    if (movementClock.Elapsed - lastMoveSend >= sendInterval)
                    {
                        var movePacket = new ClientPacket
                        {
                            CMoveInput = new C_MoveInput
                            {
                                Seq = nextMoveSeq++,
                                MoveX = moveX,
                                MoveY = moveY
                            }
                        };

                        connectedPeer.Send(movePacket.ToByteArray(), DeliveryMethod.Sequenced);
                        lastMoveSend = movementClock.Elapsed;
                    }
                }

                Thread.Sleep(5);
            }

            Assert.NotNull(entityId);
            Assert.True(server.TryGetPlayerState(entityId!.Value, out var state));
            finalState = state;
        }
        finally
        {
            client.Stop();
            server.Stop();
        }

        Assert.NotNull(finalState);
        return finalState.Value;
    }

    private static int ReserveUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
