using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using LiteNetLib;
using LiteNetLib.Utils;
using VibeMMO.Server;
using VibeMMO.Shared.Protocol.V0;

namespace VibeMMO.Server.Tests;

public class M14CombatTests
{
    private const uint ProtocolVersion = 1;

    [Fact]
    public void Attack_ValidatesRangeAndCooldown_AndReducesNpcHealth()
    {
        var port = ReserveUdpPort();
        using var server = new HandshakeServer(port);
        Assert.True(server.Start());

        using var client = new TestClient("fighter", Array.Empty<byte>());
        client.Connect(port);
        PumpUntil(server, client, TimeSpan.FromSeconds(5), () => client.TryGetWelcome(out _));
        Assert.True(client.TryGetWelcome(out _));

        PumpUntil(server, client, TimeSpan.FromSeconds(5), () => client.NpcSpawns.Count >= 2);
        Assert.True(client.NpcSpawns.Count >= 2);

        var nearestNpc = client.GetNearestNpc();
        var farthestNpc = client.GetFarthestNpc();
        Assert.NotEqual(0u, nearestNpc.EntityId);
        Assert.NotEqual(0u, farthestNpc.EntityId);

        PumpUntil(server, client, TimeSpan.FromSeconds(3), () => client.TryGetNpcHealth(nearestNpc.EntityId, out _));
        Assert.True(client.TryGetNpcHealth(nearestNpc.EntityId, out var nearestHealthBefore));

        PumpUntil(server, client, TimeSpan.FromSeconds(3), () => client.TryGetNpcHealth(farthestNpc.EntityId, out _));
        Assert.True(client.TryGetNpcHealth(farthestNpc.EntityId, out var farHealthBefore));

        client.SendAttack(farthestNpc.EntityId);
        PumpUntil(server, client, TimeSpan.FromMilliseconds(300), () => false);
        Assert.True(client.TryGetNpcHealth(farthestNpc.EntityId, out var farHealthAfter));
        Assert.Equal(farHealthBefore, farHealthAfter);

        client.SendAttack(nearestNpc.EntityId);
        PumpUntil(
            server,
            client,
            TimeSpan.FromSeconds(3),
            () => client.TryGetNpcHealth(nearestNpc.EntityId, out var hp) && hp < nearestHealthBefore);
        Assert.True(client.TryGetNpcHealth(nearestNpc.EntityId, out var firstHitHealth));
        Assert.True(firstHitHealth < nearestHealthBefore);

        client.SendAttack(nearestNpc.EntityId);
        PumpUntil(server, client, TimeSpan.FromMilliseconds(220), () => false);
        Assert.True(client.TryGetNpcHealth(nearestNpc.EntityId, out var cooldownHealth));
        Assert.Equal(firstHitHealth, cooldownHealth);

        PumpUntil(server, client, TimeSpan.FromMilliseconds(250), () => false);
        client.SendAttack(nearestNpc.EntityId);
        PumpUntil(
            server,
            client,
            TimeSpan.FromSeconds(3),
            () => client.TryGetNpcHealth(nearestNpc.EntityId, out var hp) && hp < cooldownHealth);
        Assert.True(client.TryGetNpcHealth(nearestNpc.EntityId, out var secondHitHealth));
        Assert.True(secondHitHealth < cooldownHealth);
    }

    private static void PumpUntil(HandshakeServer server, TestClient client, TimeSpan timeout, Func<bool> condition)
    {
        if (condition())
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout && !condition())
        {
            server.PollEvents();
            client.PollEvents();
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
        private readonly string _displayName;
        private readonly byte[] _reconnectToken;
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly List<ServerPacket> _receivedPackets = [];
        private NetPeer? _peer;

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

        public Dictionary<uint, Vector2Position> NpcSpawns { get; } = [];

        public void Connect(int port)
        {
            Assert.True(_client.Start());
            _client.Connect("127.0.0.1", port, string.Empty);
        }

        public void PollEvents()
        {
            _client.PollEvents();

            for (var i = 0; i < _receivedPackets.Count; i++)
            {
                var packet = _receivedPackets[i];
                if (packet.PayloadCase == ServerPacket.PayloadOneofCase.SEntitySpawn &&
                    packet.SEntitySpawn.EntityKind == EntityKind.Npc)
                {
                    NpcSpawns[packet.SEntitySpawn.EntityId] =
                        new Vector2Position(packet.SEntitySpawn.X, packet.SEntitySpawn.Y);
                }
            }
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

        public (uint EntityId, float DistanceSquared) GetNearestNpc()
        {
            var bestEntityId = 0u;
            var bestDistanceSquared = float.MaxValue;
            foreach (var kvp in NpcSpawns)
            {
                var distanceSquared = (kvp.Value.X * kvp.Value.X) + (kvp.Value.Y * kvp.Value.Y);
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    bestEntityId = kvp.Key;
                }
            }

            return (bestEntityId, bestDistanceSquared);
        }

        public (uint EntityId, float DistanceSquared) GetFarthestNpc()
        {
            var bestEntityId = 0u;
            var bestDistanceSquared = -1f;
            foreach (var kvp in NpcSpawns)
            {
                var distanceSquared = (kvp.Value.X * kvp.Value.X) + (kvp.Value.Y * kvp.Value.Y);
                if (distanceSquared > bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    bestEntityId = kvp.Key;
                }
            }

            return (bestEntityId, bestDistanceSquared);
        }

        public bool TryGetNpcHealth(uint entityId, out uint health)
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
                    if (entity.EntityId == entityId && entity.EntityKind == EntityKind.Npc)
                    {
                        health = entity.CurrentHealth;
                        return true;
                    }
                }
            }

            health = 0;
            return false;
        }

        public void SendAttack(uint targetEntityId)
        {
            Assert.NotNull(_peer);
            _peer!.Send(
                new ClientPacket
                {
                    CAttack = new C_Attack
                    {
                        TargetEntityId = targetEntityId
                    }
                }.ToByteArray(),
                DeliveryMethod.ReliableOrdered);
        }

        public void Dispose()
        {
            _client.Stop();
        }
    }

    private readonly record struct Vector2Position(float X, float Y);
}
