using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KeedParsecNative.Capture.Audio;
using KeedParsecNative.Capture.Desktop;
using KeedParsecNative.Contracts.Input;
using KeedParsecNative.Contracts.Streaming;
using KeedParsecNative.Core.Abstractions;
using KeedParsecNative.Input;
using KeedParsecNative.Input.Gamepad;
using KeedParsecNative.Input.Injection;
using KeedParsecNative.Streaming.Codecs;

namespace KeedParsecNative.App.Services;

/// <summary>
/// Wires the media pipeline for an active session: on the host it captures the
/// selected monitor, H.264-encodes frames and pushes them into the WebRTC video
/// track; on the client it decodes incoming access units and raises
/// <see cref="FrameDecoded"/> for the UI. Also routes reliable/unreliable input
/// data-channel messages: client→host packets are injected via SendInput on the
/// host; the host releases all injected inputs when the session ends.
/// </summary>
public sealed class SessionMediaPipeline : IAsyncDisposable
{
    private readonly VideoStreamConfig _videoConfig;
    private readonly AudioStreamConfig? _audioConfig;
    private readonly int _monitorIndex;
    private readonly Dispatcher? _dispatcher;

    private DxgiDesktopDuplicatorCapture? _capture;
    private IVideoEncoder? _encoder;
    private IVideoDecoder? _decoder;
    private IAudioCapture? _audioCapture;
    private IAudioPlayback? _audioPlayback;
    private Win32InputInjector? _injector;
    private ViGEmGamepadInjector? _gamepadInjector;
    private readonly List<IMediaTransport> _transports = new();
    private IMediaTransport? _transport; // first transport (client send path)
    private WindowsGamepadSource? _gamepadSource;
    private GamepadState? _lastGamepadState;
    private bool _isHost;
    private bool _disposed;
    private readonly object _sendGate = new();

    /// <summary>Decoded BGRA frame for the client UI (raised on UI thread when dispatcher provided).</summary>
    public event Func<VideoFrame, CancellationToken, Task>? FrameDecoded;
    /// <summary>Latest encoder/decoder/capture diagnostics for the stats overlay.</summary>
    public string PipelineStatus { get; private set; } = "idle";

    private readonly Func<IMediaTransport, string?>? _peerIdResolver;
    private readonly Func<string, PeerInputPermissions>? _permissionProvider;
    private readonly Dictionary<int, (float[] Axes, bool[] Buttons)> _gamepadSlots = new();

    public SessionMediaPipeline(VideoStreamConfig videoConfig, AudioStreamConfig? audioConfig,
        int monitorIndex, Dispatcher? dispatcher = null,
        Func<IMediaTransport, string?>? peerIdResolver = null,
        Func<string, PeerInputPermissions>? permissionProvider = null)
    {
        _videoConfig = videoConfig;
        _audioConfig = audioConfig;
        _monitorIndex = monitorIndex;
        _dispatcher = dispatcher;
        _peerIdResolver = peerIdResolver;
        _permissionProvider = permissionProvider;
    }

    public async Task StartHostAsync(IReadOnlyList<IMediaTransport> transports, CancellationToken ct = default)
    {
        _isHost = true;
        var index = 0;
        foreach (var t in transports)
        {
            var slot = index++;
            _transports.Add(t);
            t.DataChannelMessageReceived += (payload, kind, c) => OnInputMessageAsync(slot, t, payload, kind, c);
            t.ConnectionStateChanged += (st, c) => OnPeerTransportStateAsync(slot, st, c);
        }

        _capture = new DxgiDesktopDuplicatorCapture { MonitorIndex = _monitorIndex };
        _capture.FrameCaptured += OnFrameCapturedAsync;
        await _capture.StartAsync(_videoConfig, ct);

        _encoder = H264CodecFactory.CreateEncoder(_videoConfig, preferHardware: true);
        await _encoder.InitializeAsync(_videoConfig, ct);

        _injector = new Win32InputInjector();
        _gamepadInjector = new ViGEmGamepadInjector();

        if (_audioConfig is not null)
        {
            try
            {
                _audioCapture = new WasapiLoopbackAudioCapture();
                _audioCapture.FrameCaptured += OnAudioFrameCapturedAsync;
                await _audioCapture.StartAsync(_audioConfig, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[pipeline] audio capture unavailable: {ex.Message}");
                _audioCapture = null;
            }
        }

        PipelineStatus = $"host capture+encode ({_encoder.Name})";
    }

    public async Task StartClientAsync(IReadOnlyList<IMediaTransport> transports, CancellationToken ct = default)
    {
        _isHost = false;
        foreach (var t in transports)
        {
            _transports.Add(t);
            t.RemoteVideoFrameReceived += OnRemoteVideoAsync;
            t.RemoteAudioSampleReceived += OnRemoteAudioAsync;
            t.ConnectionStateChanged += OnTransportStateAsync;
        }
        _transport = _transports.FirstOrDefault();

        // Local gamepad capture -> data channel (client -> host).
        _gamepadSource = new WindowsGamepadSource();
        _gamepadSource.StateChanged += OnLocalGamepadStateAsync;
        await _gamepadSource.StartAsync(ct);

        _decoder = H264CodecFactory.CreateDecoder(_videoConfig, preferHardware: true);
        await _decoder.InitializeAsync(_videoConfig, ct);

        if (_audioConfig is not null)
        {
            try
            {
                _audioPlayback = new WaveOutAudioPlayback();
                await _audioPlayback.InitializeAsync(_audioConfig, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[pipeline] audio playback unavailable: {ex.Message}");
                _audioPlayback = null;
            }
        }

        PipelineStatus = $"client decode ({_decoder.Name})";
    }

    /// <summary>Host-side: attach an additional peer transport for fan-out (late joiners).</summary>
    public void AddHostTransport(IMediaTransport transport)
    {
        if (!_isHost)
        {
            return;
        }
        var slot = _transports.Count;
        _transports.Add(transport);
        transport.DataChannelMessageReceived += (payload, kind, c) => OnInputMessageAsync(slot, transport, payload, kind, c);
        transport.ConnectionStateChanged += (st, c) => OnPeerTransportStateAsync(slot, st, c);
    }

    /// <summary>Client-side: encode an input event and send it over the data channel.</summary>
    public async Task SendInputAsync(InputEvent inputEvent, bool reliable, CancellationToken ct = default)
    {
        var transport = _transport;
        if (transport is null)
        {
            return;
        }
        var packet = InputPacketCodec.Encode(inputEvent);
        await transport.SendDataChannelMessageAsync(packet,
            reliable ? InputChannelKind.Reliable : InputChannelKind.Unreliable, reliable, ct);
    }

    private async Task OnFrameCapturedAsync(VideoFrame frame, CancellationToken ct)
    {
        var encoder = _encoder;
        if (encoder is null || _transports.Count == 0)
        {
            frame.Dispose();
            return;
        }
        try
        {
            var encoded = await encoder.EncodeAsync(frame, ct);
            if (encoded is { Data.Length: > 0 })
            {
                var rtpDuration = (uint)(9_000_000L / Math.Max(1, _videoConfig.FrameRateNumerator /
                    Math.Max(1, _videoConfig.FrameRateDenominator)));
                foreach (var t in _transports)
                {
                    await t.SendVideoAsync(rtpDuration, encoded.Data, encoded.IsKeyframe, ct);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[pipeline] encode/send failed: {ex.Message}");
        }
        finally
        {
            frame.Dispose();
        }
    }

    private async Task OnRemoteVideoAsync(ReadOnlyMemory<byte> annexB, VideoFrameInfo info, CancellationToken ct)
    {
        var decoder = _decoder;
        if (decoder is null || FrameDecoded is null)
        {
            return;
        }
        try
        {
            var frame = await decoder.DecodeAsync(annexB, ct);
            if (frame is null)
            {
                return;
            }
            if (_dispatcher is not null && !_dispatcher.CheckAccess())
            {
                await _dispatcher.InvokeAsync(async () =>
                {
                    if (FrameDecoded is not null)
                    {
                        await FrameDecoded.Invoke(frame, ct);
                    }
                });
            }
            else if (FrameDecoded is not null)
            {
                await FrameDecoded.Invoke(frame, ct);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[pipeline] decode failed: {ex.Message}");
        }
    }

    private async Task OnAudioFrameCapturedAsync(AudioFrame frame, CancellationToken ct)
    {
        if (frame.PcmData.Length == 0 || _transports.Count == 0)
        {
            return;
        }
        try
        {
            // PCM16 mono -> bytes -> short[] -> µ-law (PCMU payload for RTP).
            var pcm = new short[frame.PcmData.Length / 2];
            Buffer.BlockCopy(frame.PcmData.ToArray(), 0, pcm, 0, pcm.Length * 2);
            var muLaw = G711Codec.Encode(pcm);
            foreach (var t in _transports)
            {
                await t.SendAudioAsync(160, muLaw, ct); // 20ms @ 8kHz clock
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[pipeline] audio send failed: {ex.Message}");
        }
    }

    private async Task OnRemoteAudioAsync(ReadOnlyMemory<byte> encoded, AudioFrameInfo info, CancellationToken ct)
    {
        var playback = _audioPlayback;
        if (playback is null || encoded.Length == 0)
        {
            return;
        }
        try
        {
            var pcmBytes = G711Codec.DecodeToPcmBytes(encoded.Span);
            await playback.PlayAsync(new AudioFrame
            {
                PcmData = pcmBytes,
                SampleRate = WasapiLoopbackAudioCapture.TargetSampleRate,
                Channels = 1,
                TimestampUtcTicks = DateTime.UtcNow.Ticks
            }, ct);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[pipeline] audio playback failed: {ex.Message}");
        }
    }

    private async Task OnInputMessageAsync(int peerSlot, IMediaTransport transport, ReadOnlyMemory<byte> payload, InputChannelKind kind, CancellationToken ct)
    {
        if (!_isHost)
        {
            return;
        }

        var events = InputPacketCodec.DecodeBatch(payload);

        // Host-side permission enforcement: resolve the owning peer and drop
        // every event type the host has not granted.
        var peerId = _peerIdResolver?.Invoke(transport);
        var permissions = peerId is not null && _permissionProvider is not null
            ? _permissionProvider(peerId)
            : PeerInputPermissions.All;
        var permitted = InputPermissionPolicy.Filter(events, permissions).ToList();

        var kbMouse = permitted.Where(e => e is not GamepadAxisEvent and not GamepadButtonEvent).ToList();
        var injector = _injector;
        if (injector is not null && kbMouse.Count > 0)
        {
            try
            {
                await injector.InjectAsync(InputPacketCodec.EncodeBatch(kbMouse), ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[pipeline] input inject failed: {ex.Message}");
            }
        }

        var gamepadEvents = permitted.Where(e => e is GamepadAxisEvent or GamepadButtonEvent).ToList();
        if (_gamepadInjector is not null && _gamepadInjector.IsAvailable && gamepadEvents.Count > 0)
        {
            if (!_gamepadSlots.TryGetValue(peerSlot, out var slot))
            {
                slot = (new float[6], new bool[14]);
                _gamepadSlots[peerSlot] = slot;
            }
            foreach (var e in gamepadEvents)
            {
                if (e is GamepadAxisEvent axis && axis.AxisIndex < slot.Axes.Length)
                {
                    slot.Axes[axis.AxisIndex] = Math.Clamp(axis.Value / 32767f, -1f, 1f);
                }
                else if (e is GamepadButtonEvent btn && btn.ButtonIndex < slot.Buttons.Length)
                {
                    slot.Buttons[btn.ButtonIndex] = btn.Down;
                }
            }
            try
            {
                await _gamepadInjector.InjectAsync(new GamepadState
                {
                    Index = peerSlot,
                    Axes = (float[])slot.Axes.Clone(),
                    Buttons = (bool[])slot.Buttons.Clone()
                }, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[pipeline] gamepad inject failed: {ex.Message}");
            }
        }
    }

    private Task OnTransportStateAsync(string state, CancellationToken ct)
    {
        if (state is "disconnected" or "failed" or "closed")
        {
            // Release all injected inputs so nothing stays held after disconnect.
            _ = _injector?.ResetAsync(ct);
        }
        PipelineStatus = $"transport {state}";
        return Task.CompletedTask;
    }

    private const float GamepadDeadzone = 0.15f;

    /// <summary>Client-side: diff local gamepad state and send deltas to the host.</summary>
    private async Task OnLocalGamepadStateAsync(GamepadState state, CancellationToken ct)
    {
        var transport = _transport;
        if (!_isHost && transport is not null)
        {
            await SendGamepadDiffAsync(state, ct);
        }
    }

    private async Task SendGamepadDiffAsync(GamepadState state, CancellationToken ct)
    {
        var transport = _transport;
        if (transport is null)
        {
            return;
        }
        var prev = _lastGamepadState;
        var events = new List<InputEvent>();
        for (var i = 0; i < Math.Min(state.Axes.Length, 6); i++)
        {
            var v = ApplyDeadzone(state.Axes[i]);
            var old = prev is not null && i < prev.Axes.Length ? ApplyDeadzone(prev.Axes[i]) : 0f;
            if (Math.Abs(v - old) > 0.01f)
            {
                events.Add(new GamepadAxisEvent((byte)i, (short)Math.Clamp(v * 32767f, short.MinValue, short.MaxValue)));
            }
        }
        for (var i = 0; i < Math.Min(state.Buttons.Length, 14); i++)
        {
            var was = prev is not null && i < prev.Buttons.Length && prev.Buttons[i];
            if (state.Buttons[i] != was)
            {
                events.Add(new GamepadButtonEvent((byte)i, state.Buttons[i]));
            }
        }
        _lastGamepadState = state;
        if (events.Count == 0)
        {
            return;
        }
        var reliable = events.Any(e => e is GamepadButtonEvent);
        await transport.SendDataChannelMessageAsync(
            InputPacketCodec.EncodeBatch(events),
            reliable ? InputChannelKind.Reliable : InputChannelKind.Unreliable, reliable, ct);
    }

    private static float ApplyDeadzone(float v)
        => Math.Abs(v) < GamepadDeadzone ? 0f : Math.Sign(v) * (Math.Abs(v) - GamepadDeadzone) / (1f - GamepadDeadzone);

    /// <summary>
    /// Called when the host changes a peer's input permissions. Releases that
    /// peer's gamepad slot and resets injected keys so nothing stays stuck.
    /// </summary>
    public async Task NotifyPeerPermissionsChangedAsync(string peerId, CancellationToken ct = default)
    {
        if (!_isHost || _peerIdResolver is null)
        {
            return;
        }
        for (var i = 0; i < _transports.Count; i++)
        {
            if (_peerIdResolver(_transports[i]) == peerId)
            {
                _gamepadSlots.Remove(i);
                _gamepadInjector?.ReleaseSlot(i);
                break;
            }
        }
        // Release any keys/buttons this peer may have left held.
        if (_injector is not null) { try { await _injector.ResetAsync(ct); } catch { } }
    }

    private Task OnPeerTransportStateAsync(int slot, string state, CancellationToken ct)
    {
        if ((state is "disconnected" or "failed" or "closed") && _gamepadInjector is not null)
        {
            _gamepadInjector.ReleaseSlot(slot);
        }
        return OnTransportStateAsync(state, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (var t in _transports)
        {
            t.RemoteVideoFrameReceived -= OnRemoteVideoAsync;
            t.RemoteAudioSampleReceived -= OnRemoteAudioAsync;
            t.ConnectionStateChanged -= OnTransportStateAsync;
        }
        _transports.Clear();
        if (_gamepadSource is not null)
        {
            _gamepadSource.StateChanged -= OnLocalGamepadStateAsync;
            await _gamepadSource.DisposeAsync();
            _gamepadSource = null;
        }

        if (_injector is not null)
        {
            try { await _injector.ResetAsync(); } catch { }
        }
        if (_capture is not null)
        {
            _capture.FrameCaptured -= OnFrameCapturedAsync;
            await _capture.DisposeAsync();
        }
        if (_encoder is not null)
        {
            await _encoder.DisposeAsync();
        }
        if (_decoder is not null)
        {
            await _decoder.DisposeAsync();
        }
        if (_audioCapture is not null)
        {
            _audioCapture.FrameCaptured -= OnAudioFrameCapturedAsync;
            await _audioCapture.DisposeAsync();
        }
        if (_audioPlayback is not null)
        {
            await _audioPlayback.DisposeAsync();
        }
        if (_gamepadInjector is not null)
        {
            _gamepadInjector.ReleaseSlot(0);
            await _gamepadInjector.DisposeAsync();
        }
        if (_injector is not null)
        {
            await _injector.DisposeAsync();
        }
    }
}
