using KeedParsecNative.Contracts.Signaling;
using KeedParsecNative.Signaling;
using Xunit;

namespace KeedParsecNative.UnitTests;

public class SignalingSerializationTests
{
    [Fact]
    public void Envelope_RoundTrips_WithoutPayload()
    {
        var envelope = new SignalingEnvelope
        {
            Type = SignalingMessageType.Ping,
            RoomId = "ABC234",
            SenderId = "peer-1",
            TimestampUtcTicks = DateTime.UtcNow.Ticks
        };

        var bytes = SignalingMessageSerializer.Serialize(envelope);
        var decoded = SignalingMessageSerializer.Deserialize(bytes);

        Assert.Equal(SignalingMessageType.Ping, decoded.Type);
        Assert.Equal("ABC234", decoded.RoomId);
        Assert.Equal("peer-1", decoded.SenderId);
    }

    [Fact]
    public void Envelope_WithPayload_RoundTrips()
    {
        var envelope = new SignalingEnvelope
        {
            Type = SignalingMessageType.JoinRequest,
            RoomId = "ABC234",
            SenderId = "peer-1",
            TimestampUtcTicks = DateTime.UtcNow.Ticks
        };
        var payload = new JoinRequestPayload
        {
            DisplayName = "Test User",
            ClientVersion = "0.1.0",
            VideoConfig = new KeedParsecNative.Contracts.Streaming.VideoStreamConfig(),
            AudioConfig = new KeedParsecNative.Contracts.Streaming.AudioStreamConfig()
        };

        var bytes = SignalingMessageSerializer.Serialize(envelope, payload);
        var decoded = SignalingMessageSerializer.Deserialize(bytes);
        var decodedPayload = SignalingMessageSerializer.DeserializePayload<JoinRequestPayload>(decoded);

        Assert.NotNull(decodedPayload);
        Assert.Equal("Test User", decodedPayload!.DisplayName);
        Assert.Equal("0.1.0", decodedPayload.ClientVersion);
    }

    [Fact]
    public void Topics_SanitizeRoomId()
    {
        Assert.Equal("keedparsec/v1/rooms/ABC234/signal",
            SignalingTopics.RoomSignalTopic("ABC234"));
        Assert.Equal("keedparsec/v1/rooms/ABC234/signal",
            SignalingTopics.RoomSignalTopic("abc-234!"));
    }
}
