using System.Runtime.InteropServices;
using KeedParsecNative.Contracts.Streaming;
using KeedParsecNative.Core.Abstractions;
using Vortice.MediaFoundation;

namespace KeedParsecNative.Streaming.Codecs;

/// <summary>
/// H.264 encode/decode via Media Foundation Transform (MFT) API.
/// Encoder prefers hardware MFTs registered by the GPU driver (NVENC/AMF/QSV)
/// and falls back to the software H.264 encoder MFT. Decoder likewise prefers
/// hardware DXVA/D3D11VA transforms. Input frames are BGRA; the codec
/// negotiates NV12/YUY2/RGB32 with whatever the selected MFT accepts.
/// </summary>
internal static class MfNative
{
    private static int _startupCount;

    public static readonly Guid MFMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormatNv12 = new("3231564e-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormatYuy2 = new("32595559-0000-0010-8000-00AA00389B71");
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8b");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b1-47e1-8761-7136654d3111");
    public static readonly Guid MF_MT_FRAME_RATE = new("703ae68a-0000-0010-8000-00aa00389b71");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("cf55fa65-c6ba-11d3-aa4d-00c04f7941e4");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("61735ae2-9b92-11d0-80ae-00a0c9223101");
    public static readonly Guid MF_MT_AVERAGE_BITRATE = new("20332624-fdb5-4e1d-ae6e-93b8a79b4a47");
    public static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3c8508ac-47de-4a32-908b-79cb5be36a33");
    public static readonly Guid IID_IMFTransform = new("3a0a8a36-e06f-4943-861c-38c9ee61a95a");
    public static readonly Guid MF_GUID_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7fbd394");
    public static readonly Guid MF_GUID_CATEGORY_VIDEO_DECODER = new("d6cc22c3-6e22-4e87-91ca-d993c7d1c4a8");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MFlowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");


    public static void EnsureStarted()
    {
        if (Interlocked.Increment(ref _startupCount) == 1)
        {
            MediaFactory.MFStartup();
        }
    }

    public static void Shutdown()
    {
        if (Interlocked.Decrement(ref _startupCount) == 0)
        {
            MediaFactory.MFShutdown();
        }
    }

    public static IMFMediaType CreateVideoType(Guid subtype, int width, int height, int fpsNum, int fpsDen)
    {
        var mt = MediaFactory.MFCreateMediaType();
        mt.Set(MF_MT_MAJOR_TYPE, MFMediaTypeVideo);
        mt.Set(MF_MT_SUBTYPE, subtype);
        mt.Set(MF_MT_FRAME_SIZE, ((ulong)width << 32) | (uint)height);
        mt.Set(MF_MT_FRAME_RATE, ((ulong)fpsNum << 32) | (uint)Math.Max(1, fpsDen));
        mt.Set(MF_MT_PIXEL_ASPECT_RATIO, 1UL | (1UL << 32));
        mt.Set(MF_MT_INTERLACE_MODE, (uint)VideoInterlaceMode.Progressive);
        return mt;
    }

    /// <summary>Activates all MFTs in a category matching the requested subtypes (merit order).</summary>
    public static List<IMFTransform> ActivateTransforms(Guid category, Guid? inputSubtype, Guid? outputSubtype, uint extraFlags = 0)
    {
        EnsureStarted();
        var transforms = new List<IMFTransform>();
        RegisterTypeInfo? inputInfo = inputSubtype is { } isub
            ? new RegisterTypeInfo { GuidMajorType = MFMediaTypeVideo, GuidSubtype = isub }
            : null;
        RegisterTypeInfo? outputInfo = outputSubtype is { } osub
            ? new RegisterTypeInfo { GuidMajorType = MFMediaTypeVideo, GuidSubtype = osub }
            : null;

        var flags = (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagLocalmft | (EnumFlag)extraFlags);
        IMFActivateCollection? collection;
        try
        {
            collection = MediaFactory.MFTEnumEx(category, flags, inputInfo, outputInfo);
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[mf] MFTEnumEx threw {ex.Message}");
            return transforms;
        }
        if (collection is null)
        {
            System.Console.Error.WriteLine("[mf] MFTEnumEx returned null");
            return transforms;
        }
        // Debug: unfiltered count
        try
        {
            var all = MediaFactory.MFTEnumEx(category, (uint)EnumFlag.EnumFlagAll, null, null);
            var n=0; foreach(var _ in all){n++;}
            System.Console.Error.WriteLine($"[mf] unfiltered count={n}");
            all.Dispose();
        }
        catch(Exception ex2){ System.Console.Error.WriteLine($"[mf] unfiltered threw {ex2.Message}"); }

        var dbgCount = 0;
        foreach (var activate in collection)
        {
            dbgCount++;
            var actResult = activate.ActivateObject<IMFTransform>(out var transform);
            System.Console.Error.WriteLine($"[mf] activate result={actResult.Code:X8} transform={(transform is null ? "null" : "ok")}");
            if (actResult.Failure || transform is null)
            {
                continue;
            }
            transforms.Add(transform);
        }
        collection.Dispose();
        System.Console.Error.WriteLine($"[mf] activated {transforms.Count} of {dbgCount} MFTs");
        return transforms;
    }

    public static IMFSample WrapFrame(byte[] data, int length, long pts100ns, long duration100ns)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(length);
        buffer.CurrentLength = length;
        IntPtr ptr; int maxLen; int curLen;
        buffer.Lock(out ptr, out maxLen, out curLen);
        try
        {
            Marshal.Copy(data, 0, ptr, Math.Min(length, maxLen));
        }
        finally
        {
            buffer.Unlock();
        }
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = pts100ns;
        sample.SampleDuration = duration100ns;
        return sample;
    }

    public static byte[]? ReadSampleBytes(IMFSample sample)
    {
        var count = sample.BufferCount;
        if (count <= 0)
        {
            return null;
        }
        var buffer = sample.GetBufferByIndex(0);
        var len = buffer.CurrentLength;
        IntPtr ptr; int maxLen; int curLen;
        buffer.Lock(out ptr, out maxLen, out curLen);
        try
        {
            var data = new byte[len];
            Marshal.Copy(ptr, data, 0, len);
            return data;
        }
        finally
        {
            buffer.Unlock();
        }
    }

    public static byte[]? GetBlob(IMFMediaType mt, Guid key)
    {
        try
        {
            return mt.GetBlob(key);
        }
        catch
        {
            return null;
        }
    }

    public static Guid? GetSubtype(IMFMediaType mt) => mt.GetGUID(MF_MT_SUBTYPE);

    public static string LastTypeError = "";

    /// <summary>Returns (inputStreamId, outputStreamId) for a single-stream video MFT.</summary>
    public static (int Input, int Output) GetStreamIds(IMFTransform t)
    {
        try
        {
            t.GetStreamCount(out var inCount, out var outCount);
            var inIds = new int[Math.Max(1, inCount)];
            var outIds = new int[Math.Max(1, outCount)];
            t.GetStreamIDs(inCount, inIds, outCount, outIds);
            return (inIds.Length > 0 ? inIds[0] : 0, outIds.Length > 0 ? outIds[0] : 0);
        }
        catch
        {
            return (0, 0);
        }
    }

    public static IMFMediaType? TryGetInputType(IMFTransform t, int stream, int index)
    {
        try { return t.GetInputAvailableType(stream, index); }
        catch (Exception ex) { LastTypeError = "in:" + ex.Message; return null; }
    }

    public static IMFMediaType? TryGetOutputType(IMFTransform t, int stream, int index)
    {
        try { return t.GetOutputAvailableType(stream, index); }
        catch (Exception ex) { LastTypeError = "out:" + ex.Message; return null; }
    }

    /// <summary>Converts an MF H.264 sample (AVCC length-prefixed) to Annex-B.</summary>
    public static byte[] AvccToAnnexB(byte[] avcc)
    {
        using var ms = new MemoryStream(avcc.Length + 64);
        var span = avcc.AsSpan();
        var pos = 0;
        while (pos + 4 <= span.Length)
        {
            var len = (span[pos] << 24) | (span[pos + 1] << 16) | (span[pos + 2] << 8) | span[pos + 3];
            pos += 4;
            if (len <= 0 || pos + len > span.Length)
            {
                break;
            }
            ms.Write(new byte[] { 0, 0, 0, 1 }, 0, 4);
            ms.Write(span.Slice(pos, len));
            pos += len;
        }
        // If parsing failed (no length prefixes found), treat input as Annex-B already.
        if (ms.Length == 0)
        {
            return avcc;
        }
        return ms.ToArray();
    }

    /// <summary>Detects whether an Annex-B access unit contains an IDR slice.</summary>
    public static bool ContainsIdr(byte[] annexB)
    {
        var span = annexB.AsSpan();
        for (var i = 0; i + 5 < span.Length; i++)
        {
            if (span[i] == 0 && span[i + 1] == 0 && span[i + 2] == 1)
            {
                var nalType = span[i + 3] & 0x1F;
                if (nalType == 5)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // ---- Pixel format conversions (BT.601 limited range) ----

    public static byte[] BgraToNv12(byte[] bgra, int width, int height)
    {
        var yuv = new byte[width * height * 3 / 2];
        var yPlane = yuv.AsSpan(0, width * height);
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var p = row + x * 4;
                var b = bgra[p]; var g = bgra[p + 1]; var r = bgra[p + 2];
                yPlane[y * width + x] = (byte)Math.Clamp(
                    ((66 * r + 129 * g + 25 * b + 128 >> 8) + 16), 0, 255);
            }
        }
        // NV12 UV plane is interleaved U,V pairs.
        var uv = yuv.AsSpan(width * height, width * height / 2);
        var uvIdx2 = 0;
        for (var y = 0; y < height; y += 2)
        {
            for (var x = 0; x < width; x += 2)
            {
                var uSum = 0; var vSum = 0; var n = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var xx = Math.Min(x + dx, width - 1);
                        var yy = Math.Min(y + dy, height - 1);
                        var p = (yy * width + xx) * 4;
                        var b = bgra[p]; var g = bgra[p + 1]; var r = bgra[p + 2];
                        uSum += ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                        vSum += ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                        n++;
                    }
                }
                uv[uvIdx2++] = (byte)Math.Clamp(uSum / n, 0, 255);
                uv[uvIdx2++] = (byte)Math.Clamp(vSum / n, 0, 255);
            }
        }
        return yuv;
    }

    public static byte[] BgraToYuy2(byte[] bgra, int width, int height)
    {
        var yuy2 = new byte[width * height * 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x += 2)
            {
                var uSum = 0; var vSum = 0; var n = 0;
                for (var dx = 0; dx < 2 && x + dx < width; dx++)
                {
                    var p = (y * width + x + dx) * 4;
                    var b = bgra[p]; var g = bgra[p + 1]; var r = bgra[p + 2];
                    yuy2[(y * width + x + dx) * 2] = (byte)Math.Clamp(
                        ((66 * r + 129 * g + 25 * b + 128 >> 8) + 16), 0, 255);
                    uSum += ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                    vSum += ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                    n++;
                }
                var o = (y * width + x) * 2;
                yuy2[o + 1] = (byte)Math.Clamp(uSum / n, 0, 255);
                yuy2[o + 3] = (byte)Math.Clamp(vSum / n, 0, 255);
            }
        }
        return yuy2;
    }

    /// <summary>Converts NV12 to BGRA.</summary>
    public static byte[] Nv12ToBgra(byte[] nv12, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        var ySize = width * height;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var yy = nv12[y * width + x];
                var uvIdx = ySize + (y / 2) * width + (x & ~1);
                var u = nv12[uvIdx] - 128;
                var v = nv12[uvIdx + 1] - 128;
                var c = yy - 16;
                var r = Math.Clamp((298 * c + 409 * v + 128 >> 8) + 0, 0, 255);
                var g = Math.Clamp((298 * c - 100 * u - 208 * v + 128 >> 8), 0, 255);
                var b = Math.Clamp((298 * c + 516 * u + 128 >> 8), 0, 255);
                var o = (y * width + x) * 4;
                bgra[o] = (byte)b; bgra[o + 1] = (byte)g; bgra[o + 2] = (byte)r; bgra[o + 3] = 255;
            }
        }
        return bgra;
    }
}

/// <summary>Media Foundation H.264 encoder. Prefers hardware MFTs (NVENC/AMF/QSV).</summary>
public sealed class MfH264Encoder : IVideoEncoder
{
    private static readonly Guid[] InputSubtypePreference =
    {
        MfNative.MFVideoFormatNv12,
        MfNative.MFVideoFormatYuy2,
        VideoFormatGuids.Rgb32
    };

    private readonly VideoStreamConfig _config;
    private IMFTransform? _transform;
    private Guid _inputSubtype;
    private int _inStreamId;
    private int _outStreamId;
    private byte[]? _sequenceHeader;
    private int _width;
    private int _height;
    private long _frameDuration100ns;
    private bool _disposed;
    private bool _isHardware;

    public MfH264Encoder(VideoStreamConfig config)
    {
        _config = config;
    }

    public string Name => _transform is null ? "uninitialized"
        : _isHardware ? "Hardware: MediaFoundation H.264 (GPU MFT)" : "MediaFoundation H.264 (software MFT)";
    public bool IsHardwareAccelerated => _isHardware;

    public async Task InitializeAsync(VideoStreamConfig config, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            _width = config.Width;
            _height = config.Height;
            _frameDuration100ns = 10_000_000L / Math.Max(1, config.FrameRateNumerator);

            // Hardware MFTs first (NVENC/AMF/QSV via MFT_ENUM_FLAG_HARDWARE),
            // then all remaining encoder MFTs as fallback.
            Exception? lastError = null;
            var attempts = new List<IMFTransform>();
            var hw = MfNative.ActivateTransforms(TransformCategoryGuids.VideoEncoder,
                inputSubtype: null, outputSubtype: MfNative.MFVideoFormatH264,
                (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagAsyncmft));
            attempts.AddRange(hw);
            attempts.AddRange(MfNative.ActivateTransforms(TransformCategoryGuids.VideoEncoder,
                inputSubtype: null, outputSubtype: MfNative.MFVideoFormatH264));
            attempts.AddRange(MfNative.ActivateTransforms(TransformCategoryGuids.VideoEncoder,
                inputSubtype: null, outputSubtype: null));
            var hwSet = new HashSet<IMFTransform>(hw);
            foreach (var transform in attempts.Distinct())
            {
                try
                {
                    if (TryConfigure(transform, config))
                    {
                        _isHardware = hwSet.Contains(transform);
                        _transform = transform;
                        foreach (var t in attempts) if (!ReferenceEquals(t, transform)) t.Dispose();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }
            foreach (var t in attempts) t.Dispose();
            System.Console.Error.WriteLine($"[mf-enc] no encoder configured; transforms tried, lastError={lastError}");
            throw new InvalidOperationException(
                "No Media Foundation H.264 encoder MFT could be configured.", lastError);
        }, cancellationToken);
    }

    private bool TryConfigure(IMFTransform transform, VideoStreamConfig config)
    {
        var (inId, outId) = MfNative.GetStreamIds(transform);

        // Enumerate suggested input types; for each acceptable candidate set it,
        // then look for an H.264 output type.
        for (var i = 0; i < 64; i++)
        {
            var available = MfNative.TryGetInputType(transform, inId, i);
            if (available is null)
            {
                break;
            }
            var subtype = MfNative.GetSubtype(available);
            System.Console.Error.WriteLine($"[mf-enc] input cand {subtype} {MfNative.LastTypeError}");
            if (subtype is not { } st || !InputSubtypePreference.Contains(st))
            {
                available.Dispose();
                continue;
            }

            try
            {
                available.Set(MfNative.MF_MT_FRAME_SIZE, ((ulong)_width << 32) | (uint)_height);
                available.Set(MfNative.MF_MT_FRAME_RATE, ((ulong)Math.Max(1, config.FrameRateNumerator) << 32) | (uint)Math.Max(1, config.FrameRateDenominator));
                available.Set(MfNative.MF_MT_ALL_SAMPLES_INDEPENDENT, 1U);
                available.Set(MfNative.MF_MT_AVERAGE_BITRATE, (uint)Math.Max(1, config.TargetBitrateKbps) * 1000U);
                var stride = st == MfNative.MFVideoFormatNv12 ? _width : st == MfNative.MFVideoFormatYuy2 ? _width * 2 : _width * 4;
                try { available.Set(MfNative.MF_MT_DEFAULT_STRIDE, (uint)stride); } catch { }
                transform.SetInputType(inId, available, 0);
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[mf-enc] SetInputType({st}) fail {ex.Message.Substring(0, Math.Min(90, ex.Message.Length))}");
                available.Dispose();
                continue;
            }
            available.Dispose();
            System.Console.Error.WriteLine($"[mf-enc] SetInputType({st}) ok");

            // Input type accepted — now find an H.264 output type.
            var outputSet = false;
            for (var j = 0; j < 16; j++)
            {
                var omt = MfNative.TryGetOutputType(transform, outId, j);
                if (omt is null)
                {
                    break;
                }
                var ost = MfNative.GetSubtype(omt);
                if (ost == MfNative.MFVideoFormatH264)
                {
                    try
                    {
                        omt.Set(MfNative.MF_MT_FRAME_SIZE, ((ulong)_width << 32) | (uint)_height);
                        omt.Set(MfNative.MF_MT_FRAME_RATE, ((ulong)Math.Max(1, config.FrameRateNumerator) << 32) | (uint)Math.Max(1, config.FrameRateDenominator));
                        omt.Set(MfNative.MF_MT_AVERAGE_BITRATE, (uint)Math.Max(1, config.TargetBitrateKbps) * 1000U);
                        transform.SetOutputType(outId, omt, 0);
                        outputSet = true;
                    }
                    catch
                    {
                    }
                    omt.Dispose();
                    if (outputSet)
                    {
                        break;
                    }
                    continue;
                }
                omt.Dispose();
            }
            if (!outputSet)
            {
                continue; // try next input candidate
            }

            try
            {
                var current = transform.GetOutputCurrentType(outId);
                _sequenceHeader = MfNative.GetBlob(current, MfNative.MF_MT_MPEG_SEQUENCE_HEADER);
                current.Dispose();
            }
            catch
            {
                _sequenceHeader = null;
            }

            _inputSubtype = st;
            _inStreamId = inId;
            _outStreamId = outId;
            return true;
        }
        return false;
    }

    public Task<EncodedVideoFrame?> EncodeAsync(VideoFrame frame, CancellationToken cancellationToken = default)
    {
        var transform = _transform ?? throw new InvalidOperationException("Encoder not initialized.");
        var bgra = frame.ManagedData;
        if (bgra is null || frame.Width != _width || frame.Height != _height)
        {
            return Task.FromResult<EncodedVideoFrame?>(null);
        }

        byte[] input;
        if (_inputSubtype == MfNative.MFVideoFormatNv12)
        {
            input = MfNative.BgraToNv12(bgra, frame.Width, frame.Height);
        }
        else if (_inputSubtype == MfNative.MFVideoFormatYuy2)
        {
            input = MfNative.BgraToYuy2(bgra, frame.Width, frame.Height);
        }
        else
        {
            input = bgra; // RGB32 matches BGRA byte order.
        }

        var pts = frame.TimestampUtcTicks * 10; // 100ns units
        var sample = MfNative.WrapFrame(input, input.Length, pts, _frameDuration100ns);
        try
        {
            transform.ProcessInput(_inStreamId, sample, 0);
        }
        catch
        {
            return Task.FromResult<EncodedVideoFrame?>(null);
        }
        finally
        {
            sample.Dispose();
        }

        var outputs = DrainOutput(transform, _outStreamId);
        if (outputs.Count == 0)
        {
            return Task.FromResult<EncodedVideoFrame?>(null);
        }

        var last = outputs[^1];
        var annexB = MfNative.AvccToAnnexB(last);
        var isKey = MfNative.ContainsIdr(annexB);
        if (isKey && _sequenceHeader is { Length: > 0 } sps)
        {
            var combined = new byte[sps.Length + annexB.Length];
            Buffer.BlockCopy(sps, 0, combined, 0, sps.Length);
            Buffer.BlockCopy(annexB, 0, combined, sps.Length, annexB.Length);
            annexB = combined;
        }

        return Task.FromResult<EncodedVideoFrame?>(new EncodedVideoFrame
        {
            Data = annexB,
            IsKeyframe = isKey,
            CaptureTimestampUtcTicks = frame.TimestampUtcTicks,
            EncodeTimestampUtcTicks = DateTime.UtcNow.Ticks,
            Width = _width,
            Height = _height
        });
    }

    private static List<byte[]> DrainOutput(IMFTransform transform, int streamId)
    {
        var results = new List<byte[]>();
        for (var i = 0; i < 8; i++)
        {
            OutputDataBuffer buffer = default;
            var hr = transform.ProcessOutput(ProcessOutputFlags.None, streamId, ref buffer, out _);
            if (hr.Failure || buffer.Sample is null)
            {
                break;
            }
            var outSample = buffer.Sample;
            if (outSample is not null)
            {
                var data = MfNative.ReadSampleBytes(outSample);
                if (data is not null)
                {
                    results.Add(data);
                }
                outSample.Dispose();
            }

        }
        return results;
    }

    public Task SetBitrateAsync(int targetKbps, int maxKbps, CancellationToken cancellationToken = default)
    {
        // MF bitrate changes require reconfiguration; report supported via config at init.
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await Task.Run(() => _transform?.Dispose());
        MfNative.Shutdown();
    }
}

/// <summary>Media Foundation H.264 decoder. Prefers hardware MFTs (DXVA/D3D11VA).</summary>
public sealed class MfH264Decoder : IVideoDecoder
{
    private static readonly Guid[] OutputSubtypePreference =
    {
        MfNative.MFVideoFormatNv12,
        VideoFormatGuids.Rgb32
    };

    private readonly VideoStreamConfig _config;
    private IMFTransform? _transform;
    private Guid _outputSubtype;
    private int _inStreamId;
    private int _outStreamId;
    private int _width;
    private int _height;
    private byte[]? _spsPps;
    private bool _disposed;
    private bool _isHardware;

    public MfH264Decoder(VideoStreamConfig config)
    {
        _config = config;
    }

    public string Name => _transform is null ? "uninitialized"
        : _isHardware ? "Hardware: MediaFoundation H.264 (GPU MFT)" : "MediaFoundation H.264 (software MFT)";
    public bool IsHardwareAccelerated => _isHardware;

    public async Task InitializeAsync(VideoStreamConfig config, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            _width = config.Width;
            _height = config.Height;
            var hw = MfNative.ActivateTransforms(TransformCategoryGuids.VideoDecoder,
                inputSubtype: MfNative.MFVideoFormatH264, outputSubtype: null,
                (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagAsyncmft));
            var transforms = new List<IMFTransform>(hw);
            transforms.AddRange(MfNative.ActivateTransforms(TransformCategoryGuids.VideoDecoder,
                inputSubtype: MfNative.MFVideoFormatH264, outputSubtype: null));
            var hwSet = new HashSet<IMFTransform>(hw);
            transforms = transforms.Distinct().ToList();
            Exception? lastError = null;
            foreach (var transform in transforms)
            {
                try
                {
                    if (TryConfigure(transform))
                    {
                        _isHardware = hwSet.Contains(transform);
                        _transform = transform;
                        foreach (var t in transforms) if (!ReferenceEquals(t, transform)) t.Dispose();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
                transform.Dispose();
            }
            transforms.ForEach(t => t.Dispose());
            throw new InvalidOperationException(
                "No Media Foundation H.264 decoder MFT could be configured.", lastError);
        }, cancellationToken);
    }

    /// <summary>Provides SPS/PPS so the decoder can initialize before the first IDR.</summary>
    public void SetParameterSets(byte[] sps, byte[] pps)
    {
        _spsPps = new byte[sps.Length + pps.Length];
        Buffer.BlockCopy(sps, 0, _spsPps, 0, sps.Length);
        Buffer.BlockCopy(pps, 0, _spsPps, sps.Length, pps.Length);
    }

    private bool TryConfigure(IMFTransform transform)
    {
        var (inId, outId) = MfNative.GetStreamIds(transform);
        var input = MfNative.CreateVideoType(MfNative.MFVideoFormatH264, _width, _height, _config.FrameRateNumerator, 1);
        if (_spsPps is { Length: > 0 })
        {
            input.SetBlob(MfNative.MF_MT_MPEG_SEQUENCE_HEADER, _spsPps);
        }
        try
        {
            transform.SetInputType(inId, input, 0);
        }
        catch
        {
            input.Dispose();
            return false;
        }
        input.Dispose();

        for (var i = 0; i < 64; i++)
        {
            var omt = MfNative.TryGetOutputType(transform, outId, i);
            if (omt is null)
            {
                break;
            }
            var st = MfNative.GetSubtype(omt);
            if (st is { } s && OutputSubtypePreference.Contains(s))
            {
                try
                {
                    transform.SetOutputType(outId, omt, 0);
                    _outputSubtype = s;
                    _inStreamId = inId;
                    _outStreamId = outId;
                    return true;
                }
                catch
                {
                    omt.Dispose();
                    return false;
                }
            }
            omt.Dispose();
        }
        return false;
    }

    public Task<VideoFrame?> DecodeAsync(ReadOnlyMemory<byte> encoded, CancellationToken cancellationToken = default)
    {
        var transform = _transform ?? throw new InvalidOperationException("Decoder not initialized.");
        var data = encoded.ToArray();
        var sample = MfNative.WrapFrame(data, data.Length,
            DateTime.UtcNow.Ticks * 10, 10_000_000L / Math.Max(1, _config.FrameRateNumerator));
        try
        {
            transform.ProcessInput(_inStreamId, sample, 0);
        }
        catch
        {
            return Task.FromResult<VideoFrame?>(null);
        }
        finally
        {
            sample.Dispose();
        }

        var outputs = DrainOutput(transform, _outStreamId);
        if (outputs.Count == 0)
        {
            return Task.FromResult<VideoFrame?>(null);
        }

        var raw = outputs[0];
        byte[] bgra;
        if (_outputSubtype == MfNative.MFVideoFormatNv12)
        {
            bgra = MfNative.Nv12ToBgra(raw, _width, _height);
        }
        else
        {
            bgra = raw; // RGB32 memory layout matches BGRA.
        }

        var expected = _width * _height * 4;
        if (bgra.Length < expected)
        {
            bgra = bgra.Concat(new byte[expected - bgra.Length]).ToArray();
        }

        return Task.FromResult<VideoFrame?>(new VideoFrame
        {
            DataPointer = IntPtr.Zero,
            ManagedData = bgra,
            DataLength = bgra.Length,
            Width = _width,
            Height = _height,
            Stride = _width * 4,
            Format = VideoPixelFormat.Bgra8,
            TimestampUtcTicks = DateTime.UtcNow.Ticks,
            ReleaseAction = null
        });
    }

    private static List<byte[]> DrainOutput(IMFTransform transform, int streamId)
    {
        var results = new List<byte[]>();
        for (var i = 0; i < 8; i++)
        {
            OutputDataBuffer buffer = default;
            var hr = transform.ProcessOutput(ProcessOutputFlags.None, streamId, ref buffer, out _);
            if (hr.Failure || buffer.Sample is null)
            {
                break;
            }
            var outSample = buffer.Sample;
            if (outSample is not null)
            {
                var data = MfNative.ReadSampleBytes(outSample);
                if (data is not null)
                {
                    results.Add(data);
                }
                outSample.Dispose();
            }
        }
        return results;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await Task.Run(() => _transform?.Dispose());
        MfNative.Shutdown();
    }
}
