using KeedParsecNative.Contracts.Streaming;
using KeedParsecNative.Core.Abstractions;

namespace KeedParsecNative.Streaming.Codecs;

public sealed record CodecDiagnostic
{
    public required string Kind { get; init; } // "encoder" | "decoder"
    public required string Name { get; init; }
    public required bool Available { get; init; }
    public required bool HardwareAccelerated { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Encoder that prefers a hardware Media Foundation MFT (NVENC/AMF/QSV via
/// MFT_ENUM_FLAG_HARDWARE) and transparently falls back to OpenH264 software
/// when no usable hardware transform exists. The active backend is reported
/// honestly through <see cref="IVideoEncoder.Name"/> and
/// <see cref="IVideoEncoder.IsHardwareAccelerated"/>.
/// </summary>
public sealed class AutoH264Encoder : IVideoEncoder
{
    private readonly VideoStreamConfig _config;
    private readonly bool _preferHardware;
    private IVideoEncoder? _inner;

    public AutoH264Encoder(VideoStreamConfig config, bool preferHardware = true)
    {
        _config = config;
        _preferHardware = preferHardware && OperatingSystem.IsWindows();
    }

    private async Task<IVideoEncoder> EnsureInnerAsync(CancellationToken ct)
    {
        if (_inner is not null)
        {
            return _inner;
        }
        if (_preferHardware)
        {
            var mf = new MfH264Encoder(_config);
            try
            {
                await mf.InitializeAsync(_config, ct);
                _inner = mf;
                return _inner;
            }
            catch
            {
                await mf.DisposeAsync();
            }
        }
        var sw = new OpenH264Encoder(_config);
        await sw.InitializeAsync(_config, ct);
        _inner = sw;
        return _inner;
    }

    public string Name => _inner?.Name ?? "pending";
    public bool IsHardwareAccelerated => _inner?.IsHardwareAccelerated ?? false;

    public async Task InitializeAsync(VideoStreamConfig config, CancellationToken cancellationToken = default)
    {
        await EnsureInnerAsync(cancellationToken);
    }

    public async Task<EncodedVideoFrame?> EncodeAsync(VideoFrame frame, CancellationToken cancellationToken = default)
    {
        var inner = await EnsureInnerAsync(cancellationToken);
        return await inner.EncodeAsync(frame, cancellationToken);
    }

    public async Task SetBitrateAsync(int targetKbps, int maxKbps, CancellationToken cancellationToken = default)
    {
        if (_inner is not null)
        {
            await _inner.SetBitrateAsync(targetKbps, maxKbps, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_inner is not null)
        {
            await _inner.DisposeAsync();
            _inner = null;
        }
        await Task.CompletedTask;
    }
}

/// <summary>Decoder counterpart of <see cref="AutoH264Encoder"/>.</summary>
public sealed class AutoH264Decoder : IVideoDecoder
{
    private readonly VideoStreamConfig _config;
    private readonly bool _preferHardware;
    private IVideoDecoder? _inner;

    public AutoH264Decoder(VideoStreamConfig config, bool preferHardware = true)
    {
        _config = config;
        _preferHardware = preferHardware && OperatingSystem.IsWindows();
    }

    private async Task<IVideoDecoder> EnsureInnerAsync(CancellationToken ct)
    {
        if (_inner is not null)
        {
            return _inner;
        }
        if (_preferHardware)
        {
            var mf = new MfH264Decoder(_config);
            try
            {
                await mf.InitializeAsync(_config, ct);
                _inner = mf;
                return _inner;
            }
            catch
            {
                await mf.DisposeAsync();
            }
        }
        var sw = new OpenH264Decoder(_config);
        await sw.InitializeAsync(_config, ct);
        _inner = sw;
        return _inner;
    }

    public string Name => _inner?.Name ?? "pending";
    public bool IsHardwareAccelerated => _inner?.IsHardwareAccelerated ?? false;

    public async Task InitializeAsync(VideoStreamConfig config, CancellationToken cancellationToken = default)
        => await EnsureInnerAsync(cancellationToken);

    public async Task<VideoFrame?> DecodeAsync(ReadOnlyMemory<byte> encoded, CancellationToken cancellationToken = default)
    {
        var inner = await EnsureInnerAsync(cancellationToken);
        return await inner.DecodeAsync(encoded, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_inner is not null)
        {
            await _inner.DisposeAsync();
            _inner = null;
        }
        await Task.CompletedTask;
    }
}

/// <summary>
/// Creates H.264 encoder/decoder instances. Hardware Media Foundation MFTs are
/// attempted first on Windows; OpenH264 (software, SIMD) is the automatic
/// fallback. Diagnostics probe availability without committing a backend.
/// </summary>
public static class H264CodecFactory
{
    /// <summary>Whether any Media Foundation video encoder MFT category can be enumerated.</summary>
    public static bool MediaFoundationAvailable => OperatingSystem.IsWindows();

    public static IReadOnlyList<CodecDiagnostic> Diagnose()
    {
        var mfAvailable = MediaFoundationAvailable;
        return new[]
        {
            new CodecDiagnostic
            {
                Kind = "encoder",
                Name = "auto (MF hardware → OpenH264)",
                Available = true,
                HardwareAccelerated = mfAvailable,
                Reason = mfAvailable
                    ? "Prefers GPU MFTs (NVENC/AMF/QSV); falls back to OpenH264 software."
                    : "Non-Windows host: OpenH264 software only."
            },
            new CodecDiagnostic
            {
                Kind = "decoder",
                Name = "auto (MF hardware → OpenH264)",
                Available = true,
                HardwareAccelerated = mfAvailable,
                Reason = mfAvailable
                    ? "Prefers DXVA/D3D11VA MFTs; falls back to OpenH264 software."
                    : "Non-Windows host: OpenH264 software only."
            }
        };
    }

    public static IVideoEncoder CreateEncoder(VideoStreamConfig config, bool preferHardware)
        => new AutoH264Encoder(config, preferHardware);

    public static IVideoDecoder CreateDecoder(VideoStreamConfig config, bool preferHardware)
        => new AutoH264Decoder(config, preferHardware);
}
