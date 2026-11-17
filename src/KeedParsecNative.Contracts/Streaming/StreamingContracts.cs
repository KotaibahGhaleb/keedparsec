namespace KeedParsecNative.Contracts.Streaming;

public enum VideoCodec
{
    H264 = 0
}

public enum ConnectionType
{
    DirectIp = 0,
    DirectIpv6 = 1,
    TurnRelay = 2,
    Unknown = 3
}

public sealed record VideoStreamConfig
{
    public VideoCodec Codec { get; init; } = VideoCodec.H264;
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int FrameRateNumerator { get; init; } = 60;
    public int FrameRateDenominator { get; init; } = 1;
    public int TargetBitrateKbps { get; init; } = 30000;
    public int MaxBitrateKbps { get; init; } = 60000;
    public int KeyframeIntervalFrames { get; init; } = 120;
}

public sealed record AudioStreamConfig
{
    public int SampleRate { get; init; } = 48000;
    public int Channels { get; init; } = 2;
    public int BitrateKbps { get; init; } = 256;
}

/// <summary>STUN/TURN server configuration. Credentials come from secrets/env only.</summary>
public sealed record IceServerConfig
{
    /// <summary>Comma/space separated URLs, e.g. "turn:turn.example.com:3478?transport=udp".</summary>
    public required string Urls { get; init; }
    public string? Username { get; init; }
    public string? Credential { get; init; }
}

public sealed record StreamStats
{
    public double Fps { get; init; }
    public double BitrateKbps { get; init; }
    public double CaptureToDisplayLatencyMs { get; init; }
    public double EndToEndLatencyMs { get; init; }
    public double PacketLossPercent { get; init; }
    public double JitterMs { get; init; }
    public double EncodeTimeMs { get; init; }
    public double DecodeTimeMs { get; init; }
    public ConnectionType ConnectionType { get; init; } = ConnectionType.Unknown;
    public string? EncoderName { get; init; }
    public string? DecoderName { get; init; }
    public string? FallbackReason { get; init; }
}
