using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using LiteNetLib;
using LiteNetLib.Utils;
using VibeMMO.Server;
using VibeMMO.Shared.Protocol.V0;

namespace VibeMMO.Server.Tests;

public class M15LootPersistenceTests
{
    private const uint ProtocolVersion = 1;

    [Fact]
    public void LootPickup_IncrementsCoins_AndPersistsAcrossReconnect()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vibemmo-m15-{Guid.NewGuid():N}.db");
        var port = ReserveUdpPort();

        try
        {
            using var server = new HandshakeServer(port, dbPath);
            Assert.True(server.Start());

            byte[] reconnectToken;
            uint collectedCoins;
            using (var firstClient = new TestClient("looter", Array.Empty<byte>()))
            {
                firstClient.Connect(port);
                PumpUntil(server, firstClient, TimeSpan.FromSeconds(5), () => firstClient.TryGetWelcome(out _));
                Assert.True(firstClient.TryGetWelcome(out var welcome));
                reconnectToken = welcome!.ReconnectToken.ToByteArray();

                PumpUntil(server, firstClient, TimeSpan.FromSeconds(5), () => firstClient.NpcSpawns.Count > 0);
                var nearestNpcId = firstClient.GetNearestNpcEntityId();
                Assert.NotEqual(0u, nearestNpcId);

                for (var i = 0; i < 4; i++)
                {
                    firstClient.SendAttack(nearestNpcId);
                    PumpUntil(server, firstClient, TimeSpan.FromMilliseconds(450), () => false);
                }

                PumpUntil(server, firstClient, TimeSpan.FromSeconds(5), () => firstClient.TryGetLatestLoot(out _));
                Assert.True(firstClient.TryGetLatestLoot(out var lootEntityId));

                firstClient.SendPickup(lootEntityId);
                PumpUntil(server, firstClient, TimeSpan.FromSeconds(3), () => firstClient.GetLatestCoins() > 0);
                collectedCoins = firstClient.GetLatestCoins();
                Assert.True(collectedCoins > 0);
            }

            PumpUntil(server, null, TimeSpan.FromMilliseconds(400), () => false);

            using var reconnectClient = new TestClient("looter", reconnectToken);
            reconnectClient.Connect(port);
            PumpUntil(server, reconnectClient, TimeSpan.FromSeconds(5), () => reconnectClient.TryGetWelcome(out _));
            Assert.True(reconnectClient.TryGetWelcome(out _));

            PumpUntil(server, reconnectClient, TimeSpan.FromSeconds(5), () => reconnectClient.GetLatestCoins() >= collectedCoins);
            Assert.True(reconnectClient.GetLatestCoins() >= collectedCoins);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    private static void PumpUntil(HandshakeServer server, TestClient? client, TimeSpan timeout, Func<bool> condition)
    {
        if (condition())
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout && !condition())
        {
            server.PollEvents();
            client?.PollEvents();
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

        public uint GetNearestNpcEntityId()
        {
            var nearestNpcId = 0u;
            var nearestDistanceSquared = float.MaxValue;
            foreach (var kvp in NpcSpawns)
            {
                var distanceSquared = (kvp.Value.X * kvp.Value.X) + (kvp.Value.Y * kvp.Value.Y);
                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearestNpcId = kvp.Key;
                }
            }

            return nearestNpcId;
        }

        public bool TryGetLatestLoot(out uint lootEntityId)
        {
            for (var i = _receivedPackets.Count - 1; i >= 0; i--)
            {
                var packet = _receivedPackets[i];
                if (packet.PayloadCase == ServerPacket.PayloadOneofCase.SEntitySpawn &&
                    packet.SEntitySpawn.EntityKind == EntityKind.Loot)
                {
                    lootEntityId = packet.SEntitySpawn.EntityId;
                    return true;
                }
            }

            lootEntityId = 0;
            return false;
        }

        public uint GetLatestCoins()
        {
            for (var i = _receivedPackets.Count - 1; i >= 0; i--)
            {
                var packet = _receivedPackets[i];
                if (packet.PayloadCase == ServerPacket.PayloadOneofCase.SSnapshot)
                {
                    return packet.SSnapshot.YourCoins;
                }
            }

            return 0;
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

        public void SendPickup(uint lootEntityId)
        {
            Assert.NotNull(_peer);
            _peer!.Send(
                new ClientPacket
                {
                    CPickup = new C_Pickup
                    {
                        LootEntityId = lootEntityId
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
