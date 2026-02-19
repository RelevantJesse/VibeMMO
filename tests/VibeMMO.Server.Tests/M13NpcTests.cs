using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using LiteNetLib;
using LiteNetLib.Utils;
using VibeMMO.Server;
using VibeMMO.Shared.Protocol.V0;

namespace VibeMMO.Server.Tests;

public class M13NpcTests
{
    private const uint ProtocolVersion = 1;

    [Fact]
    public void ConnectingClient_ReceivesNpcSpawns_AndNpcSnapshots()
    {
        var port = ReserveUdpPort();
        using var server = new HandshakeServer(port);
        Assert.True(server.Start());

        var listener = new EventBasedNetListener();
        var client = new NetManager(listener)
        {
            AutoRecycle = false
        };

        var npcSpawnPositions = new Dictionary<uint, (float X, float Y)>();
        var npcSeenInSnapshots = new HashSet<uint>();
        var sawNpcMovement = false;

        listener.PeerConnectedEvent += peer =>
        {
            var hello = new ClientPacket
            {
                CHello = new C_Hello
                {
                    ProtocolVersion = ProtocolVersion,
                    DisplayName = "npc-observer",
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
                    case ServerPacket.PayloadOneofCase.SEntitySpawn:
                        if (packet.SEntitySpawn.EntityKind == EntityKind.Npc)
                        {
                            npcSpawnPositions[packet.SEntitySpawn.EntityId] = (packet.SEntitySpawn.X, packet.SEntitySpawn.Y);
                        }
                        break;

                    case ServerPacket.PayloadOneofCase.SSnapshot:
                        foreach (var entity in packet.SSnapshot.Entities)
                        {
                            if (entity.EntityKind != EntityKind.Npc)
                            {
                                continue;
                            }

                            npcSeenInSnapshots.Add(entity.EntityId);
                            if (npcSpawnPositions.TryGetValue(entity.EntityId, out var spawn))
                            {
                                if (MathF.Abs(entity.X - spawn.X) > 0.05f || MathF.Abs(entity.Y - spawn.Y) > 0.05f)
                                {
                                    sawNpcMovement = true;
                                }
                            }
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

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                server.PollEvents();
                client.PollEvents();

                if (npcSpawnPositions.Count > 0 &&
                    npcSeenInSnapshots.Count > 0 &&
                    sawNpcMovement)
                {
                    break;
                }

                Thread.Sleep(5);
            }
        }
        finally
        {
            client.Stop();
            server.Stop();
        }

        Assert.NotEmpty(npcSpawnPositions);
        Assert.NotEmpty(npcSeenInSnapshots);
        Assert.True(sawNpcMovement);
    }

    private static int ReserveUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
