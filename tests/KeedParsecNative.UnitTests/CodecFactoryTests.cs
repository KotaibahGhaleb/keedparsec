using KeedParsecNative.Contracts.Streaming;
using KeedParsecNative.Streaming.Codecs;

namespace KeedParsecNative.UnitTests;

public class CodecFactoryTests
{
    private static VideoStreamConfig Config() => new()
    {
        Width = 320, Height = 180, FrameRateNumerator = 30, FrameRateDenominator = 1,
        TargetBitrateKbps = 1000, MaxBitrateKbps = 2000, KeyframeIntervalFrames = 60
    };

    [Fact]
    public void Diagnose_ReportsEncoderAndDecoder()
    {
        var diag = H264CodecFactory.Diagnose();
        Assert.Contains(diag, d => d.Kind == "encoder" && d.Available);
        Assert.Contains(diag, d => d.Kind == "decoder" && d.Available);
    }

    [Fact]
    public async Task Encoder_Initializes_And_ReportsHonestBackend()
    {
        var encoder = H264CodecFactory.CreateEncoder(Config(), preferHardware: true);
        await encoder.InitializeAsync(Config());
        Assert.False(string.IsNullOrWhiteSpace(encoder.Name));
        // Name must honestly say hardware or software — never claim HW falsely.
        Assert.True(encoder.IsHardwareAccelerated
            ? encoder.Name.Contains("Hardware", StringComparison.OrdinalIgnoreCase)
            : !encoder.Name.Contains("NVENC", StringComparison.OrdinalIgnoreCase));
        await encoder.DisposeAsync();
    }

    [Fact]
    public async Task Decoder_Initializes()
    {
        var decoder = H264CodecFactory.CreateDecoder(Config(), preferHardware: true);
        await decoder.InitializeAsync(Config());
        Assert.False(string.IsNullOrWhiteSpace(decoder.Name));
        await decoder.DisposeAsync();
    }
}
