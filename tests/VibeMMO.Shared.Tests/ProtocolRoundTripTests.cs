using Google.Protobuf;
using VibeMMO.Shared.Protocol.V0;

namespace VibeMMO.Shared.Tests;

public class ProtocolRoundTripTests
{
    [Fact]
    public void ClientHelloPacket_RoundTrips()
    {
        var expected = new ClientPacket
        {
            CHello = new C_Hello
            {
                ProtocolVersion = 1,
                DisplayName = "alice",
                ReconnectToken = ByteString.CopyFrom(new byte[] { 1, 2, 3, 4 })
            }
        };

        var bytes = expected.ToByteArray();
        var actual = ClientPacket.Parser.ParseFrom(bytes);

        Assert.Equal(ClientPacket.PayloadOneofCase.CHello, actual.PayloadCase);
        Assert.Equal(expected.CHello.ProtocolVersion, actual.CHello.ProtocolVersion);
        Assert.Equal(expected.CHello.DisplayName, actual.CHello.DisplayName);
        Assert.Equal(expected.CHello.ReconnectToken, actual.CHello.ReconnectToken);
    }

    [Fact]
    public void ServerSnapshotPacket_RoundTrips()
    {
        var expected = new ServerPacket
        {
            SSnapshot = new S_Snapshot
            {
                ServerTick = 240,
                LastProcessedInputSeq = 17,
                Entities =
                {
                    new EntityState { EntityId = 101, X = 12.5f, Y = -3.25f, Vx = 1.0f, Vy = 0.0f },
                    new EntityState { EntityId = 102, X = 0.0f, Y = 9.75f, Vx = 0.0f, Vy = -1.0f }
                }
            }
        };

        var bytes = expected.ToByteArray();
        var actual = ServerPacket.Parser.ParseFrom(bytes);

        Assert.Equal(ServerPacket.PayloadOneofCase.SSnapshot, actual.PayloadCase);
        Assert.Equal(expected.SSnapshot.ServerTick, actual.SSnapshot.ServerTick);
        Assert.Equal(expected.SSnapshot.LastProcessedInputSeq, actual.SSnapshot.LastProcessedInputSeq);
        Assert.Equal(expected.SSnapshot.Entities.Count, actual.SSnapshot.Entities.Count);
        Assert.Equal(expected.SSnapshot.Entities[0].EntityId, actual.SSnapshot.Entities[0].EntityId);
        Assert.Equal(expected.SSnapshot.Entities[1].Y, actual.SSnapshot.Entities[1].Y);
    }

    [Fact]
    public void ChatBroadcastPacket_RoundTrips()
    {
        var expected = new ServerPacket
        {
            SChatBroadcast = new S_ChatBroadcast
            {
                FromEntityId = 42,
                FromName = "alice",
                Text = "hello world"
            }
        };

        var bytes = expected.ToByteArray();
        var actual = ServerPacket.Parser.ParseFrom(bytes);

        Assert.Equal(ServerPacket.PayloadOneofCase.SChatBroadcast, actual.PayloadCase);
        Assert.Equal(expected.SChatBroadcast.FromEntityId, actual.SChatBroadcast.FromEntityId);
        Assert.Equal(expected.SChatBroadcast.FromName, actual.SChatBroadcast.FromName);
        Assert.Equal(expected.SChatBroadcast.Text, actual.SChatBroadcast.Text);
    }
}
