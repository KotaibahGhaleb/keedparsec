using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using KeedParsecNative.Contracts.Streaming;
using KeedParsecNative.Core.Abstractions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace KeedParsecNative.Capture.Desktop;

/// <summary>
/// DXGI Desktop Duplication screen capture. Enumerates GPU outputs via DXGI,
/// acquires <see cref="IDXGIOutputDuplication"/> for the selected monitor and
/// streams BGRA frames on a dedicated thread. Handles access loss and output
/// changes by recreating the duplication.
/// </summary>
public sealed class DxgiDesktopDuplicatorCapture : IScreenCapture, IDisposable
{
    private const uint DuplicationAcquireTimeoutMs = 250;
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const int DxgiErrorInvalidCall = unchecked((int)0x887A0001);
    private const int DxgiErrorAccessDenied = unchecked((int)0x887A002B);
    private const int DxgiErrorNotFound = unchecked((int)0x887A0022);

    private readonly record struct DxgiOutputRef(IDXGIAdapter Adapter, int OutputIndex, OutputDescription Description);

    private readonly object _sync = new();
    private Thread? _captureThread;
    private CancellationTokenSource? _cts;
    private volatile bool _running;
    private Exception? _fatalError;

    /// <summary>Zero-based index into <see cref="EnumerateMonitors"/> ordering.</summary>
    public int MonitorIndex { get; set; }

    public event Func<VideoFrame, CancellationToken, Task>? FrameCaptured;

    // ---- Monitor enumeration ----

    public IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var outputs = EnumerateDxgiOutputs();
        var result = new List<MonitorInfo>(outputs.Count);
        var primaryHandle = outputs.Count > 0 ? outputs[0].Description.Monitor : IntPtr.Zero;
        for (var primary = 0; primary < outputs.Count; primary++)
        {
            if (outputs[primary].Description.AttachedToDesktop)
            {
                primaryHandle = outputs[primary].Description.Monitor;
                break;
            }
        }

        for (var i = 0; i < outputs.Count; i++)
        {
            var o = outputs[i];
            var rect = o.Description.DesktopCoordinates;
            result.Add(new MonitorInfo
            {
                Index = i,
                DeviceName = o.Description.DeviceName ?? string.Empty,
                Width = Math.Max(0, rect.Right - rect.Left),
                Height = Math.Max(0, rect.Bottom - rect.Top),
                X = rect.Left,
                Y = rect.Top,
                IsPrimary = o.Description.Monitor == primaryHandle
            });
        }
        return result;
    }

    private static List<DxgiOutputRef> EnumerateDxgiOutputs()
    {
        var list = new List<DxgiOutputRef>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; ; a++)
        {
            IDXGIAdapter? adapter;
            if (factory.EnumAdapters(a, out adapter).Failure || adapter is null)
            {
                break;
            }
            using (adapter)
            {
                for (uint o = 0; ; o++)
                {
                    IDXGIOutput? output;
                    if (adapter.EnumOutputs(o, out output).Failure || output is null)
                    {
                        break;
                    }
                    using (output)
                    {
                        var desc = output.Description;
                        list.Add(new DxgiOutputRef(adapter, (int)o, desc));
                    }
                }
            }
        }
        return list;
    }

    // ---- Capture lifecycle ----

    public Task StartAsync(VideoStreamConfig? config, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_running)
            {
                return Task.CompletedTask;
            }

            var outputs = EnumerateDxgiOutputs();
            var attached = outputs.Where(o => o.Description.AttachedToDesktop).ToList();
            if (attached.Count == 0)
            {
                throw new InvalidOperationException("No attached DXGI desktop outputs found.");
            }

            // MonitorIndex refers to the EnumerateMonitors() ordering, which
            // includes all outputs; map it to the attached subset safely.
            var index = Math.Clamp(MonitorIndex, 0, outputs.Count - 1);
            var selected = outputs[index];
            if (!selected.Description.AttachedToDesktop)
            {
                selected = attached[0];
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _running = true;
            _captureThread = new Thread(() => CaptureLoop(selected))
            {
                IsBackground = true,
                Name = "DxgiDesktopCapture"
            };
            _captureThread.Start();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _running = false;
            _cts?.Cancel();
        }
        _captureThread?.Join(TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }

    public Exception? FatalError => _fatalError;

    // ---- Capture thread ----

    private void CaptureLoop(DxgiOutputRef outputRef)
    {
        IDXGIOutputDuplication? duplication = null;
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        ID3D11Texture2D? stagingTexture = null;
        int width = 0, height = 0;

        try
        {
            var hr = D3D11.D3D11CreateDevice(
                outputRef.Adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                new[]
                {
                    FeatureLevel.Level_11_1, FeatureLevel.Level_11_0,
                    FeatureLevel.Level_10_1, FeatureLevel.Level_10_0
                },
                out device,
                out _,
                out context);
            if (hr.Failure || device is null || context is null)
            {
                throw new InvalidOperationException($"D3D11CreateDevice failed: 0x{hr.Code:X8}");
            }

            duplication = CreateDuplication(outputRef.Adapter, device, outputRef.OutputIndex);
            if (duplication is null)
            {
                throw new InvalidOperationException(
                    "DuplicateOutput failed. The desktop may be locked or another app holds the duplication.");
            }

            var dupDesc = duplication.Description;
            width = (int)dupDesc.ModeDescription.Width;
            height = (int)dupDesc.ModeDescription.Height;
            stagingTexture = CreateStagingTexture(device, width, height, dupDesc.ModeDescription.Format);

            while (_running && !_cts!.IsCancellationRequested)
            {
                IDXGIResource? frameResource = null;
                OutduplFrameInfo frameInfo = default;
                var result = duplication.AcquireNextFrame(DuplicationAcquireTimeoutMs, out frameInfo, out frameResource);
                var code = result.Code;

                if (code == DxgiErrorWaitTimeout)
                {
                    continue;
                }
                if (code == DxgiErrorAccessLost || code == DxgiErrorInvalidCall)
                {
                    duplication.Dispose();
                    duplication = CreateDuplication(outputRef.Adapter, device, outputRef.OutputIndex);
                    if (duplication is null)
                    {
                        throw new InvalidOperationException("Desktop duplication lost and could not be recreated.");
                    }
                    var d = duplication.Description;
                    width = (int)d.ModeDescription.Width;
                    height = (int)d.ModeDescription.Height;
                    stagingTexture?.Dispose();
                    stagingTexture = CreateStagingTexture(device, width, height, d.ModeDescription.Format);
                    continue;
                }
                if (result.Failure)
                {
                    throw new InvalidOperationException(
                        $"AcquireNextFrame failed: 0x{code:X8} (access denied={code == DxgiErrorAccessDenied}, not found={code == DxgiErrorNotFound})");
                }

                try
                {
                    if (frameResource is null || frameInfo.ProtectedContentMaskedOut)
                    {
                        continue;
                    }

                    using (frameResource)
                    {
                        var texture = frameResource.QueryInterface<ID3D11Texture2D>();
                        using (texture)
                        {
                            var texDesc = texture.Description;
                            if ((int)texDesc.Width != width || (int)texDesc.Height != height || stagingTexture is null)
                            {
                                stagingTexture?.Dispose();
                                width = (int)texDesc.Width;
                                height = (int)texDesc.Height;
                                stagingTexture = CreateStagingTexture(device, width, height, texDesc.Format);
                            }

                            if (stagingTexture is not null)
                            {
                                context.CopyResource(stagingTexture, texture);
                                context.Flush();
                                EmitFrame(context, stagingTexture, width, height);
                            }
                        }
                    }
                }
                finally
                {
                    duplication.ReleaseFrame();
                }
            }
        }
        catch (Exception ex)
        {
            _fatalError = ex;
            System.Diagnostics.Debug.WriteLine($"[dxgi-capture] {ex}");
        }
        finally
        {
            stagingTexture?.Dispose();
            duplication?.Dispose();
            context?.Dispose();
            device?.Dispose();
            _running = false;
        }
    }

    private static IDXGIOutputDuplication? CreateDuplication(IDXGIAdapter adapter, ID3D11Device device, int outputIndex)
    {
        IDXGIOutput? output = null;
        for (uint o = 0; ; o++)
        {
            IDXGIOutput? candidate;
            if (adapter.EnumOutputs(o, out candidate).Failure || candidate is null)
            {
                break;
            }
            if ((int)o == outputIndex)
            {
                output = candidate;
                break;
            }
            candidate.Dispose();
        }
        if (output is null)
        {
            return null;
        }

        using (output)
        {
            var output1 = output.QueryInterface<IDXGIOutput1>();
            if (output1 is null)
            {
                return null;
            }
            using (output1)
            {
                try
                {
                    return output1.DuplicateOutput(device);
                }
                catch (SharpGen.Runtime.SharpGenException)
                {
                    return null;
                }
            }
        }
    }

    private static ID3D11Texture2D CreateStagingTexture(ID3D11Device device, int width, int height, Format format)
    {
        if (format != Format.B8G8R8A8_UNorm && format != Format.B8G8R8A8_UNorm_SRgb)
        {
            format = Format.B8G8R8A8_UNorm;
        }
        var desc = new Texture2DDescription(
            format,
            (uint)Math.Max(1, width),
            (uint)Math.Max(1, height),
            1, 1,
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read,
            1, 0,
            ResourceOptionFlags.None);
        return device.CreateTexture2D(desc);
    }

    private void EmitFrame(ID3D11DeviceContext context, ID3D11Texture2D staging, int width, int height)
    {
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pitch = (int)mapped.RowPitch;
            var rowBytes = width * 4;
            var total = rowBytes * height;
            var data = ArrayPool<byte>.Shared.Rent(total);
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy((IntPtr)((long)mapped.DataPointer + (long)y * pitch), data, y * rowBytes, rowBytes);
            }

            var frame = new VideoFrame
            {
                DataPointer = IntPtr.Zero,
                ManagedData = data,
                DataLength = total,
                Width = width,
                Height = height,
                Stride = rowBytes,
                Format = VideoPixelFormat.Bgra8,
                TimestampUtcTicks = DateTime.UtcNow.Ticks,
                ReleaseAction = () => ArrayPool<byte>.Shared.Return(data)
            };

            if (FrameCaptured is { } handler)
            {
                _ = Task.Run(async () => await handler.Invoke(frame, CancellationToken.None));
            }
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Dispose();
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        _running = false;
        _cts?.Dispose();
    }
}
