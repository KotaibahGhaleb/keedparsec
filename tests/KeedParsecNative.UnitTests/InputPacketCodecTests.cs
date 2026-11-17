using KeedParsecNative.Contracts.Input;
using KeedParsecNative.Core.Abstractions;
using KeedParsecNative.Input;
using Xunit;

namespace KeedParsecNative.UnitTests;

public class InputPacketCodecTests
{
    [Fact]
    public void KeyboardEvent_RoundTrips()
    {
        var input = new KeyboardEvent(0x41, true);
        var packet = InputPacketCodec.Encode(input);
        var decoded = InputPacketCodec.Decode(packet);

        var key = Assert.IsType<KeyboardEvent>(decoded);
        Assert.Equal(0x41, key.VirtualKey);
        Assert.True(key.KeyDown);
    }

    [Fact]
    public void MouseMove_RoundTrips()
    {
        var input = new MouseMoveEvent(-120, 45);
        var decoded = Assert.IsType<MouseMoveEvent>(InputPacketCodec.Decode(InputPacketCodec.Encode(input)));
        Assert.Equal(-120, decoded.DeltaX);
        Assert.Equal(45, decoded.DeltaY);
    }

    [Fact]
    public void MouseButton_RoundTrips()
    {
        var input = new MouseButtonEvent(0, false);
        var decoded = Assert.IsType<MouseButtonEvent>(InputPacketCodec.Decode(InputPacketCodec.Encode(input)));
        Assert.Equal(0, decoded.Button);
        Assert.False(decoded.Down);
    }

    [Fact]
    public void MouseWheel_RoundTrips()
    {
        var input = new MouseWheelEvent(-120);
        var decoded = Assert.IsType<MouseWheelEvent>(InputPacketCodec.Decode(InputPacketCodec.Encode(input)));
        Assert.Equal(-120, decoded.Delta);
    }

    [Fact]
    public void Batch_EncodeDecode_PreservesOrder()
    {
        InputEvent[] events =
        {
            new KeyboardEvent(0x41, true),
            new KeyboardEvent(0x41, false),
            new MouseMoveEvent(10, -5),
            new MouseButtonEvent(0, true),
            new MouseButtonEvent(0, false)
        };

        var batch = InputPacketCodec.EncodeBatch(events);
        var decoded = InputPacketCodec.DecodeBatch(batch);

        Assert.Equal(events.Length, decoded.Count);
        Assert.Equal(events.Select(e => e.Kind), decoded.Select(e => e.Kind));
    }

    [Fact]
    public void Decode_RejectsTruncatedPacket()
    {
        Assert.Null(InputPacketCodec.Decode(ReadOnlySpan<byte>.Empty));
        Assert.Null(InputPacketCodec.Decode(new byte[] { (byte)InputPacketKind.KeyboardKey }));
    }

    [Fact]
    public void GamepadAxis_RoundTrips()
    {
        var input = new GamepadAxisEvent(2, 12345);
        var decoded = Assert.IsType<GamepadAxisEvent>(InputPacketCodec.Decode(InputPacketCodec.Encode(input)));
        Assert.Equal(2, decoded.AxisIndex);
        Assert.Equal((short)12345, decoded.Value);
    }

    [Fact]
    public void GamepadButton_RoundTrips()
    {
        var input = new GamepadButtonEvent(10, true);
        var decoded = Assert.IsType<GamepadButtonEvent>(InputPacketCodec.Decode(InputPacketCodec.Encode(input)));
        Assert.Equal(10, decoded.ButtonIndex);
        Assert.True(decoded.Down);
    }
}
