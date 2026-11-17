using KeedParsecNative.Contracts.Streaming;

namespace KeedParsecNative.Infrastructure.Configuration;

public sealed class AppConfig
{
    public const string SectionName = "KeedParsec";

    public SignalingConfig Signaling { get; set; } = new();
    public StreamingConfig Streaming { get; set; } = new();
    public InputConfig Input { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();
    public TurnConfig Turn { get; set; } = new();
    public DiagnosticsConfig Diagnostics { get; set; } = new();
}

public sealed class SignalingConfig
{
    /// <summary>MQTT broker URI, e.g. mqtt://localhost:1883 or mqtts://broker.example.com:8883</summary>
    public string BrokerUri { get; set; } = "mqtt://localhost:1883";

    /// <summary>Optional MQTT credentials (prefer environment variables for secrets).</summary>
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Base client id prefix; a random suffix is appended per session.</summary>
    public string ClientIdPrefix { get; set; } = "keedparsec";
}

public sealed class StreamingConfig
{
    public VideoStreamConfig Video { get; set; } = new();
    public AudioStreamConfig Audio { get; set; } = new();
    public int SelectedMonitorIndex { get; set; } = 0;
    public bool EnableHardwareEncoding { get; set; } = true;
    public bool EnableHardwareDecoding { get; set; } = true;
}

public sealed class InputConfig
 {
    /// <summary>Poll rate for relative mouse movement batching (Hz).</summary>
    public int MousePollRateHz { get; set; } = 120;
    public bool CaptureKeyboard { get; set; } = true;
    public bool CaptureMouse { get; set; } = true;
    public bool CaptureGamepad { get; set; } = true;
    public int GamepadPollRateHz { get; set; } = 60;
    public bool BlockLocalInputWhileStreaming { get; set; } = true;
}

public sealed class SecurityConfig
{
    /// <summary>Room code length in characters. Never treated as sufficient auth alone.</summary>
    public int RoomCodeLength { get; set; } = 6;
    public bool RequireHostApproval { get; set; } = true;
    public int JoinTokenTtlSeconds { get; set; } = 60;
    public string? JoinTokenSecret { get; set; }
}

public sealed class TurnConfig
{
    public List<IceServerConfig> Servers { get; set; } = new();

    /// <summary>STUN server URLs. Defaults to Google's public STUN when empty.</summary>
    public List<string> StunServers { get; set; } = new();
}

public sealed class DiagnosticsConfig
{
    public bool EnableStatsOverlay { get; set; } = true;
    public int StatsOverlayIntervalMs { get; set; } = 1000;
    public bool LogToFile { get; set; } = true;
    public string LogDirectory { get; set; } = "logs";
}
