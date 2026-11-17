namespace KeedParsecNative.Capture.Audio;

/// <summary>G.711 µ-law codec used for the WebRTC PCMU audio track (8kHz mono).</summary>
public static class G711Codec
{
    private const int Bias = 0x84;
    private const int Clip = 32635;

    private static readonly byte[] MuLawEncodeTable = new byte[256];
    private static readonly short[] MuLawDecodeTable = new short[256];

    static G711Codec()
    {
        for (var i = 0; i < 256; i++)
        {
            MuLawDecodeTable[i] = DecodeMuLaw((byte)i);
        }
    }

    /// <summary>Encode 16-bit PCM samples to µ-law bytes (one byte per sample).</summary>
    public static byte[] Encode(ReadOnlySpan<short> pcm)
    {
        var output = new byte[pcm.Length];
        for (var i = 0; i < pcm.Length; i++)
        {
            output[i] = EncodeSample(pcm[i]);
        }
        return output;
    }

    /// <summary>Decode µ-law bytes to 16-bit PCM samples.</summary>
    public static short[] Decode(ReadOnlySpan<byte> muLaw)
    {
        var output = new short[muLaw.Length];
        for (var i = 0; i < muLaw.Length; i++)
        {
            output[i] = MuLawDecodeTable[muLaw[i]];
        }
        return output;
    }

    /// <summary>Decode µ-law bytes directly into interleaved little-endian PCM16 bytes.</summary>
    public static byte[] DecodeToPcmBytes(ReadOnlySpan<byte> muLaw)
    {
        var output = new byte[muLaw.Length * 2];
        for (var i = 0; i < muLaw.Length; i++)
        {
            var sample = MuLawDecodeTable[muLaw[i]];
            output[i * 2] = (byte)(sample & 0xFF);
            output[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return output;
    }

    private static byte EncodeSample(short sample)
    {
        var sign = (sample >> 8) & 0x80;
        if (sign != 0)
        {
            sample = (short)-sample;
        }
        if (sample > Clip)
        {
            sample = Clip;
        }
        sample = (short)(sample + Bias);
        var exponent = 7;
        for (var mask = 0x4000; (sample & mask) == 0 && exponent > 0; mask >>= 1)
        {
            exponent--;
        }
        var mantissa = (sample >> (exponent + 3)) & 0x0F;
        var encoded = (byte)(~(sign | (exponent << 4) | mantissa) & 0xFF);
        return encoded;
    }

    private static short DecodeMuLaw(byte muLaw)
    {
        var u = (byte)~muLaw;
        var sign = u & 0x80;
        var exponent = (u >> 4) & 0x07;
        var mantissa = u & 0x0F;
        var sample = ((mantissa << 3) + Bias) << exponent;
        sample -= Bias;
        return (short)(sign != 0 ? -sample : sample);
    }
}
