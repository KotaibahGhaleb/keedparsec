using H264Sharp;
using KeedParsecNative.Contracts.Streaming;
using KeedParsecNative.Core.Abstractions;

namespace KeedParsecNative.Streaming.Codecs;

/// <summary>
/// H.264 encoder backed by Cisco OpenH264 (via H264Sharp). Software codec with
/// SIMD-optimized BGRA→YUV conversion; Annex-B output ready for RTP packetization.
/// </summary>
public sealed class OpenH264Encoder : IVideoEncoder
{
    private readonly VideoStreamConfig _config;
    private H264Encoder? _encoder;
    private bool _disposed;

    public OpenH264Encoder(VideoStreamConfig config)
    {
        _config = config;
    }

    public string Name => "OpenH264 (software, SIMD)";
    public bool IsHardwareAccelerated => false;

    public Task InitializeAsync(VideoStreamConfig config, CancellationToken cancellationToken = default)
    {
        var tmp = new H264Encoder();
        var p = tmp.GetDefaultParameters();
        p.iUsageType = EUsageType.SCREEN_CONTENT_REAL_TIME;
        p.iPicWidth = config.Width;
        p.iPicHeight = config.Height;
        p.iTargetBitrate = Math.Max(100, config.TargetBitrateKbps) * 1000;
        p.iMaxBitrate = Math.Max(p.iTargetBitrate, config.MaxBitrateKbps * 1000);
        p.fMaxFrameRate = Math.Max(1, config.FrameRateNumerator) / (float)Math.Max(1, config.FrameRateDenominator);
        p.iRCMode = RC_MODES.RC_BITRATE_MODE;
        p.uiIntraPeriod = (uint)Math.Max(1, config.KeyframeIntervalFrames);
        p.iMultipleThreadIdc = 0; // auto
        p.iNumRefFrame = 1;

        tmp.Dispose();
        _encoder = new H264Encoder();
        var rc = _encoder.Initialize(p);
        if (rc != 0)
        {
            _encoder.Dispose();
            _encoder = null;
            throw new InvalidOperationException($"OpenH264 encoder init failed: {rc}");
        }
        return Task.CompletedTask;
    }

    public Task<EncodedVideoFrame?> EncodeAsync(VideoFrame frame, CancellationToken cancellationToken = default)
    {
        var encoder = _encoder ?? throw new InvalidOperationException("Encoder not initialized.");
        var data = frame.ManagedData;
        if (data is null || frame.Width <= 0 || frame.Height <= 0)
        {
            return Task.FromResult<EncodedVideoFrame?>(null);
        }

        using var rgb = new RgbImage(ImageFormat.Bgra, frame.Width, frame.Height, frame.Stride, data);
        var ok = encoder.Encode(rgb, out var encoded);
        if (!ok || encoded is null || encoded.Length == 0)
        {
            return Task.FromResult<EncodedVideoFrame?>(null);
        }

        // OpenH264 may return multiple NAL units per access unit — concatenate.
        var total = EncodedDataExtentions.GetTotalSize(encoded);
        var annexB = new byte[total];
        EncodedDataExtentions.CopyAllTo(encoded, annexB, 0);
        var isKey = encoded.Any(e => e.FrameType == FrameType.IDR || e.FrameType == FrameType.I);

        return Task.FromResult<EncodedVideoFrame?>(new EncodedVideoFrame
        {
            Data = annexB,
            IsKeyframe = isKey,
            CaptureTimestampUtcTicks = frame.TimestampUtcTicks,
            EncodeTimestampUtcTicks = DateTime.UtcNow.Ticks,
            Width = frame.Width,
            Height = frame.Height
        });
    }

    public Task SetBitrateAsync(int targetKbps, int maxKbps, CancellationToken cancellationToken = default)
    {
        _encoder?.SetMaxBitrate(Math.Max(1, maxKbps) * 1000);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
        _encoder?.Dispose();
        _encoder = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>H.264 decoder backed by Cisco OpenH264 (via H264Sharp).</summary>
public sealed class OpenH264Decoder : IVideoDecoder
{
    private readonly VideoStreamConfig _config;
    private H264Decoder? _decoder;
    private bool _disposed;

    public OpenH264Decoder(VideoStreamConfig config)
    {
        _config = config;
    }

    public string Name => "OpenH264 (software, SIMD)";
    public bool IsHardwareAccelerated => false;

    public Task InitializeAsync(VideoStreamConfig config, CancellationToken cancellationToken = default)
    {
        _decoder = new H264Decoder();
        var rc = _decoder.Initialize();
        if (rc != 0)
        {
            _decoder.Dispose();
            _decoder = null;
            throw new InvalidOperationException($"OpenH264 decoder init failed: {rc}");
        }
        return Task.CompletedTask;
    }

    public Task<VideoFrame?> DecodeAsync(ReadOnlyMemory<byte> encoded, CancellationToken cancellationToken = default)
    {
        var decoder = _decoder ?? throw new InvalidOperationException("Decoder not initialized.");
        var data = encoded.ToArray();
        DecodingState state;
        var w = _config.Width;
        var h = _config.Height;
        var yuv = new YuvImage(w, h);
        try
        {
            var ok = decoder.Decode(data, 0, data.Length, false, out state, ref yuv);
            if (!ok)
            {
                return Task.FromResult<VideoFrame?>(null);
            }
        }
        catch
        {
            return Task.FromResult<VideoFrame?>(null);
        }

        var rgb = new RgbImage(ImageFormat.Bgra, w, h);
        try
        {
            Converter.Yuv2Rgb(yuv, rgb);
        }
        finally
        {
            yuv.Dispose();
        }
        var bytes = rgb.GetBytes(); // BGRA when format=Bgra
        rgb.Dispose();

        var frame = new VideoFrame
        {
            DataPointer = IntPtr.Zero,
            ManagedData = bytes,
            DataLength = bytes.Length,
            Width = w,
            Height = h,
            Stride = w * 4,
            Format = VideoPixelFormat.Bgra8,
            TimestampUtcTicks = DateTime.UtcNow.Ticks,
            ReleaseAction = null
        };
        return Task.FromResult<VideoFrame?>(frame);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
        _decoder?.Dispose();
        _decoder = null;
        return ValueTask.CompletedTask;
    }
}
