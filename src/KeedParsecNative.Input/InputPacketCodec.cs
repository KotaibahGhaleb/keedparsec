using System.Buffers;
using System.Buffers.Binary;
using KeedParsecNative.Contracts.Input;
using KeedParsecNative.Core.Abstractions;

namespace KeedParsecNative.Input;

/// <summary>
/// Compact binary codec for input packets.
/// Layout: [kind:1][payload little-endian].
/// KeyboardKey:      vk:u16 down:u8
/// MouseMoveRelative: dx:i16 dy:i16
/// MouseMoveAbsolute: x:u16 y:u16 (normalized 0-65535)
/// MouseButton:      button:u8 down:u8
/// MouseWheel:       delta:i16
/// GamepadAxis:      axisIndex:u8 value:i16 (-32768..32767)
/// GamepadButton:    buttonIndex:u8 down:u8
/// ControllerState:  reserved for future bulk state frames
/// </summary>
public static class InputPacketCodec
{
    public const int MaxPacketSize = 64;

    public static byte[] Encode(InputEvent inputEvent)
    {
        var buffer = new byte[MaxPacketSize];
        var length = EncodeInto(inputEvent, buffer);
        return buffer.AsSpan(0, length).ToArray();
    }

    public static int EncodeInto(InputEvent inputEvent, Span<byte> destination)
    {
        const int minSize = 8;
        if (destination.Length < minSize)
        {
            throw new ArgumentException($"Destination must be at least {minSize} bytes.", nameof(destination));
        }

        var span = destination;
        span[0] = (byte)inputEvent.Kind;
        var written = 1;

        switch (inputEvent)
        {
            case KeyboardEvent key:
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(written), key.VirtualKey);
                span[written + 2] = (byte)(key.KeyDown ? 1 : 0);
                written += 3;
                break;

            case MouseMoveEvent move:
                BinaryPrimitives.WriteInt16LittleEndian(span.Slice(written), (short)move.DeltaX);
                BinaryPrimitives.WriteInt16LittleEndian(span.Slice(written + 2), (short)move.DeltaY);
                written += 4;
                break;

            case MouseButtonEvent button:
                span[written] = button.Button;
                span[written + 1] = (byte)(button.Down ? 1 : 0);
                written += 2;
                break;

            case MouseWheelEvent wheel:
                BinaryPrimitives.WriteInt16LittleEndian(span.Slice(written), (short)wheel.Delta);
                written += 2;
                break;

            case GamepadAxisEvent axis:
                span[written] = axis.AxisIndex;
                BinaryPrimitives.WriteInt16LittleEndian(span.Slice(written + 1), axis.Value);
                written += 3;
                break;

            case GamepadButtonEvent button:
                span[written] = button.ButtonIndex;
                span[written + 1] = (byte)(button.Down ? 1 : 0);
                written += 2;
                break;

            default:
                throw new NotSupportedException($"Encoding {inputEvent.Kind} is not implemented.");
        }

        return written;
    }

    public static InputEvent? Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 2)
        {
            return null;
        }

        var kind = (InputPacketKind)packet[0];
        return kind switch
        {
            InputPacketKind.KeyboardKey when packet.Length >= 4 =>
                new KeyboardEvent(BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(1)), packet[3] != 0),

            InputPacketKind.MouseMoveRelative when packet.Length >= 5 =>
                new MouseMoveEvent(
                    BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(1)),
                    BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(3))),

            InputPacketKind.MouseButton when packet.Length >= 3 =>
                new MouseButtonEvent(packet[1], packet[2] != 0),

            InputPacketKind.MouseWheel when packet.Length >= 3 =>
                new MouseWheelEvent(BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(1))),

            InputPacketKind.GamepadAxis when packet.Length >= 4 =>
                new GamepadAxisEvent(packet[1], BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(2))),

            InputPacketKind.GamepadButton when packet.Length >= 3 =>
                new GamepadButtonEvent(packet[1], packet[2] != 0),

            _ => null
        };
    }

    public static IReadOnlyList<InputEvent> DecodeBatch(ReadOnlyMemory<byte> payload)
    {
        var events = new List<InputEvent>();
        var span = payload.Span;
        var offset = 0;

        // Batch framing: each record is [length:u8][packet bytes].
        while (offset < span.Length)
        {
            var recordLength = span[offset];
            offset++;
            if (recordLength == 0 || offset + recordLength > span.Length)
            {
                break;
            }
            var decoded = Decode(span.Slice(offset, recordLength));
            offset += recordLength;
            if (decoded is not null)
            {
                events.Add(decoded);
            }
        }

        return events;
    }

    public static byte[] EncodeBatch(IEnumerable<InputEvent> events)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Span<byte> packet = stackalloc byte[16];
        Span<byte> lengthPrefix = stackalloc byte[1];
        foreach (var inputEvent in events)
        {
            var length = EncodeInto(inputEvent, packet);
            lengthPrefix[0] = (byte)length;
            buffer.Write(lengthPrefix);
            buffer.Write(packet.Slice(0, length));
        }
        return buffer.WrittenSpan.ToArray();
    }
}
