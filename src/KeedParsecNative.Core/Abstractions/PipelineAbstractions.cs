using KeedParsecNative.Contracts.Input;
using KeedParsecNative.Contracts.Session;
using KeedParsecNative.Contracts.Signaling;
using KeedParsecNative.Contracts.Streaming;

namespace KeedParsecNative.Core.Abstractions;

/// <summary>Transport for room-based signaling messages (e.g. MQTT).</summary>
public interface ISignalingClient : IAsyncDisposable
{
    event Func<SignalingEnvelope, CancellationToken, Task>? MessageReceived;
    event Func<ConnectionState, CancellationToken, Task>? ConnectionStateChanged;

    Task ConnectAsync(Uri brokerUri, string clientId, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task PublishAsync(SignalingEnvelope envelope, MqttQualityOfService qos = MqttQualityOfService.AtLeastOnce, CancellationToken cancellationToken = default);
    Task SubscribeAsync(string topicFilter, MqttQualityOfService qos = MqttQualityOfService.AtLeastOnce, CancellationToken cancellationToken = default);
}

public enum MqttQualityOfService
{
    AtMostOnce = 0,
    AtLeastOnce = 1,
    ExactlyOnce = 2
}

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Failed
}

/// <summary>Local SDP description produced by the peer connection.</summary>
public sealed record LocalDescription
{
    public required string Sdp { get; init; }
    public required string Type { get; init; } // "offer" | "answer" | "pranswer" | "rollback"
}

/// <summary>An ICE candidate gathered by the peer connection.</summary>
public sealed record IceCandidateInfo
{
    public required string Candidate { get; init; }
    public string? SdpMid { get; init; }
    public int SdpMLineIndex { get; init; }
}

/// <summary>WebRTC media transport abstraction (peer connection + data channels).</summary>
public interface IMediaTransport : IAsyncDisposable
{
    /// <summary>Fired when a local SDP description is ready to send to the peer.</summary>
    event Func<LocalDescription, CancellationToken, Task>? LocalDescriptionReady;
    /// <summary>Fired for each gathered ICE candidate (trickle ICE).</summary>
    event Func<IceCandidateInfo, CancellationToken, Task>? IceCandidateGathered;
    /// <summary>Fired for incoming data channel messages. Payload is the raw packet bytes.</summary>
    event Func<ReadOnlyMemory<byte>, InputChannelKind, CancellationToken, Task>? DataChannelMessageReceived;
    /// <summary>Fired on peer connection / ICE state changes. Values: "connecting", "connected", "disconnected", "failed", "closed".</summary>
    event Func<string, CancellationToken, Task>? ConnectionStateChanged;

    /// <summary>Creates an offer and starts ICE gathering. LocalDescriptionReady fires when SDP is ready.</summary>
    Task CreateOfferAsync(CancellationToken cancellationToken = default);
    /// <summary>Sets a remote offer, creates an answer, and starts ICE gathering.</summary>
    Task CreateAnswerAsync(CancellationToken cancellationToken = default);
    Task SetLocalDescriptionAsync(LocalDescription description, CancellationToken cancellationToken = default);
    Task SetRemoteDescriptionAsync(string sdp, string type, CancellationToken cancellationToken = default);
    Task AddIceCandidateAsync(IceCandidateInfo candidate, CancellationToken cancellationToken = default);
    Task SendDataChannelMessageAsync(ReadOnlyMemory<byte> payload, InputChannelKind channel, bool reliable, CancellationToken cancellationToken = default);
    Task<StreamStats> GetStatsAsync(CancellationToken cancellationToken = default);

    // ---- Media tracks ----

    /// <summary>
    /// Sends an H.264 access unit (Annex-B) for transmission. No-op if the
    /// peer connection has not negotiated video or is not connected.
    /// </summary>
    Task SendVideoAsync(uint rtpDurationUnits, ReadOnlyMemory<byte> annexBAccessUnit, bool isKeyframe, CancellationToken cancellationToken = default);

    /// <summary>Sends an encoded audio sample (codec per negotiation, e.g. PCMU/Opus).</summary>
    Task SendAudioAsync(uint rtpDurationUnits, ReadOnlyMemory<byte> encodedSample, CancellationToken cancellationToken = default);

    /// <summary>Fired when a full video access unit is received (Annex-B).</summary>
    event Func<ReadOnlyMemory<byte>, VideoFrameInfo, CancellationToken, Task>? RemoteVideoFrameReceived;

    /// <summary>Fired when an encoded audio sample is received.</summary>
    event Func<ReadOnlyMemory<byte>, AudioFrameInfo, CancellationToken, Task>? RemoteAudioSampleReceived;
}

/// <summary>Metadata for a received video access unit.</summary>
public sealed record VideoFrameInfo
{
    public required uint RtpTimestamp { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public bool IsKeyframe { get; init; }
}

/// <summary>Metadata for a received encoded audio sample.</summary>
public sealed record AudioFrameInfo
{
    public required uint RtpTimestamp { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
}

/// <summary>Screen capture source producing video frames.</summary>
public interface IScreenCapture : IAsyncDisposable
{
    event Func<VideoFrame, CancellationToken, Task>? FrameCaptured;
    Task StartAsync(VideoStreamConfig config, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<MonitorInfo> EnumerateMonitors();
}

public sealed record MonitorInfo
{
    public required int Index { get; init; }
    public required string DeviceName { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public bool IsPrimary { get; init; }
}

public sealed record VideoFrame : IDisposable
{
    public required IntPtr DataPointer { get; init; }
    /// <summary>Optional CPU-side pixel buffer (BGRA). Preferred over <see cref="DataPointer"/> when set.</summary>
    public byte[]? ManagedData { get; init; }
    /// <summary>Valid byte count in <see cref="ManagedData"/>.</summary>
    public int DataLength { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required VideoPixelFormat Format { get; init; }
    public required long TimestampUtcTicks { get; init; }
    public int TextureHandle { get; init; }
    public Action? ReleaseAction { get; init; }

    public void Dispose() => ReleaseAction?.Invoke();
}

public enum VideoPixelFormat
{
    Bgra8 = 0,
    Nv12 = 1,
    Rgba8 = 2
}

/// <summary>Hardware-accelerated video encoder (H.264 baseline).</summary>
public interface IVideoEncoder : IAsyncDisposable
{
    string Name { get; }
    bool IsHardwareAccelerated { get; }
    Task InitializeAsync(VideoStreamConfig config, CancellationToken cancellationToken = default);
    Task<EncodedVideoFrame?> EncodeAsync(VideoFrame frame, CancellationToken cancellationToken = default);
    Task SetBitrateAsync(int targetKbps, int maxKbps, CancellationToken cancellationToken = default);
}

public sealed record EncodedVideoFrame
{
    public required ReadOnlyMemory<byte> Data { get; init; }
    public required bool IsKeyframe { get; init; }
    public required long CaptureTimestampUtcTicks { get; init; }
    public required long EncodeTimestampUtcTicks { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>Video decoder on the client.</summary>
public interface IVideoDecoder : IAsyncDisposable
{
    string Name { get; }
    bool IsHardwareAccelerated { get; }
    Task InitializeAsync(VideoStreamConfig config, CancellationToken cancellationToken = default);
    Task<VideoFrame?> DecodeAsync(ReadOnlyMemory<byte> encoded, CancellationToken cancellationToken = default);
}

/// <summary>Host audio capture (WASAPI loopback).</summary>
public interface IAudioCapture : IAsyncDisposable
{
    Task StartAsync(AudioStreamConfig config, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    event Func<AudioFrame, CancellationToken, Task>? FrameCaptured;
}

public sealed record AudioFrame
{
    public required ReadOnlyMemory<byte> PcmData { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required long TimestampUtcTicks { get; init; }
}

/// <summary>Client audio playback.</summary>
public interface IAudioPlayback : IAsyncDisposable
{
    Task InitializeAsync(AudioStreamConfig config, CancellationToken cancellationToken = default);
    Task PlayAsync(AudioFrame frame, CancellationToken cancellationToken = default);
}

/// <summary>Captures local input events (keyboard/mouse/gamepad).</summary>
public interface IInputSource : IAsyncDisposable
{
    event Func<InputEventBatch, CancellationToken, Task>? InputCaptured;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed record InputEventBatch
{
    public required IReadOnlyList<InputEvent> Events { get; init; }
    public required long TimestampUtcTicks { get; init; }
}

public abstract record InputEvent
{
    public abstract InputPacketKind Kind { get; }
}

public sealed record KeyboardEvent(ushort VirtualKey, bool KeyDown) : InputEvent
{
    public override InputPacketKind Kind => InputPacketKind.KeyboardKey;
}

public sealed record MouseMoveEvent(int DeltaX, int DeltaY) : InputEvent
{
    public override InputPacketKind Kind => InputPacketKind.MouseMoveRelative;
}

public sealed record MouseButtonEvent(byte Button, bool Down) : InputEvent
{
    public override InputPacketKind Kind => InputPacketKind.MouseButton;
}

public sealed record MouseWheelEvent(int Delta) : InputEvent
{
    public override InputPacketKind Kind => InputPacketKind.MouseWheel;
}

public sealed record GamepadAxisEvent(byte AxisIndex, short Value) : InputEvent
{
    public override InputPacketKind Kind => InputPacketKind.GamepadAxis;
}

public sealed record GamepadButtonEvent(byte ButtonIndex, bool Down) : InputEvent
{
    public override InputPacketKind Kind => InputPacketKind.GamepadButton;
}

/// <summary>Injects input events on the host machine.</summary>
public interface IInputInjector : IAsyncDisposable
{
    Task InjectAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Gamepad source/injector abstractions (XInput).</summary>
public interface IGamepadSource : IAsyncDisposable
{
    event Func<GamepadState, CancellationToken, Task>? StateChanged;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IGamepadInjector : IAsyncDisposable
{
    Task InjectAsync(GamepadState state, CancellationToken cancellationToken = default);
}

public sealed record GamepadState
{
    public required int Index { get; init; }
    public float[] Axes { get; init; } = Array.Empty<float>();
    public bool[] Buttons { get; init; } = Array.Empty<bool>();
}
