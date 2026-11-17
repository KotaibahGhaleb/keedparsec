using KeedParsecNative.Contracts.Session;
using KeedParsecNative.Contracts.Streaming;

namespace KeedParsecNative.Contracts.Signaling;

public sealed record JoinRequestPayload
{
    public required string DisplayName { get; init; }
    public required VideoStreamConfig VideoConfig { get; init; }
    public required AudioStreamConfig AudioConfig { get; init; }
    public required string ClientVersion { get; init; }
    /// <summary>Optional host-issued join token; room code alone is not sufficient when approval is required.</summary>
    public string? JoinToken { get; init; }
}

public sealed record JoinResponsePayload
{
    public required bool Accepted { get; init; }
    public string? Reason { get; init; }
    public PeerInfo? HostPeer { get; init; }
}

public sealed record HostApprovalPayload
{
    public required string RequesterPeerId { get; init; }
    public required string DisplayName { get; init; }
    public bool Approved { get; init; }
}

public sealed record SdpPayload
{
    public required string Sdp { get; init; }
    public required string SdpType { get; init; } // "offer" | "answer"
}

public sealed record IceCandidatePayload
{
    public required string Candidate { get; init; }
    public required string SdpMid { get; init; }
    public required int SdpMLineIndex { get; init; }
}

public sealed record SessionStatePayload
{
    public required SessionState State { get; init; }
    public IReadOnlyList<PeerInfo>? Peers { get; init; }
}

public sealed record StatsPayload
{
    public required StreamStats Stats { get; init; }
}
