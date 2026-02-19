using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using LiteNetLib;
using LiteNetLib.Utils;
using VibeMMO.Server;
using VibeMMO.Shared.Protocol.V0;

namespace VibeMMO.Server.Tests;

public class SnapshotTests
{
    private const uint ProtocolVersion = 1;

    [Fact]
    public void Snapshot_IncludesLocalEntityState()
    {
        var result = ReceiveSnapshotWhileMoving();

        Assert.True(result.Snapshot.ServerTick > 0);
        Assert.True(result.Snapshot.LastProcessedInputSeq > 0);
        Assert.Equal(result.EntityId, result.LocalEntityState.EntityId);
        Assert.True(result.LocalEntityState.X > 0.2f);
        Assert.InRange(result.LocalEntityState.Vx, 4.75f, 5.25f);
        Assert.InRange(MathF.Abs(result.LocalEntityState.Vy), 0.0f, 0.01f);
    }

    private static SnapshotResult ReceiveSnapshotWhileMoving()
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
        SnapshotResult? result = null;

        listener.PeerConnectedEvent += peer =>
        {
            connectedPeer = peer;
            var hello = new ClientPacket
            {
                CHello = new C_Hello
                {
                    ProtocolVersion = ProtocolVersion,
                    DisplayName = "snapshot-tester",
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
                switch (packet.PayloadCase)
                {
                    case ServerPacket.PayloadOneofCase.SWelcome:
                        entityId = packet.SWelcome.YourEntityId;
                        movementClock ??= Stopwatch.StartNew();
                        break;

                    case ServerPacket.PayloadOneofCase.SSnapshot:
                        if (entityId is null)
                        {
                            break;
                        }

                        var snapshot = packet.SSnapshot;
                        foreach (var entity in snapshot.Entities)
                        {
                            if (entity.EntityId != entityId.Value)
                            {
                                continue;
                            }

                            result = new SnapshotResult(entityId.Value, snapshot, entity);
                            break;
                        }
                        break;
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
                    if (movementClock.Elapsed - lastMoveSend >= sendInterval)
                    {
                        var movePacket = new ClientPacket
                        {
                            CMoveInput = new C_MoveInput
                            {
                                Seq = nextMoveSeq++,
                                MoveX = 1.0f,
                                MoveY = 0.0f
                            }
                        };

                        connectedPeer.Send(movePacket.ToByteArray(), DeliveryMethod.Sequenced);
                        lastMoveSend = movementClock.Elapsed;
                    }

                    if (movementClock.Elapsed >= TimeSpan.FromMilliseconds(900) &&
                        result is not null &&
                        result.Value.LocalEntityState.X > 0.2f)
                    {
                        break;
                    }
                }

                Thread.Sleep(5);
            }
        }
        finally
        {
            client.Stop();
            server.Stop();
        }

        Assert.NotNull(result);
        return result.Value;
    }

    private static int ReserveUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private readonly record struct SnapshotResult(uint EntityId, S_Snapshot Snapshot, EntityState LocalEntityState);
}
