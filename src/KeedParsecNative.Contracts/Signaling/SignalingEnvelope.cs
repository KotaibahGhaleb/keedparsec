namespace KeedParsecNative.Contracts.Signaling;

/// <summary>
/// Wire-level signaling message envelope. The <see cref="Type"/> discriminates
/// the payload which is carried as JSON in <see cref="PayloadJson"/>.
/// </summary>
public sealed record SignalingEnvelope
{
    public required SignalingMessageType Type { get; init; }
    public required string RoomId { get; init; }
    public required string SenderId { get; init; }
    /// <summary>
    /// Optional intended recipient. Null means room-wide (e.g. JoinRequest,
    /// PeerList). Point-to-point messages (JoinAccepted, Offer, Answer,
    /// IceCandidate) MUST set this so only the target peer acts on them.
    /// </summary>
    public string? TargetPeerId { get; init; }
    /// <summary>Monotonic per-sender sequence number for replay/ordering checks.</summary>
    public long Sequence { get; init; }
    public required long TimestampUtcTicks { get; init; }
    public string? PayloadJson { get; init; }
}

public enum SignalingMessageType
{
    Unknown = 0,

    // Session lifecycle
    JoinRequest = 1,
    JoinAccepted = 2,
    JoinRejected = 3,
    HostApprovalRequest = 4,
    HostApprovalResponse = 5,
    Leave = 6,

    // WebRTC negotiation
    Offer = 10,
    Answer = 11,
    IceCandidate = 12,
    IceCandidateEnd = 13,

    // Session state broadcast
    SessionState = 20,
    PeerList = 21,

    // Diagnostics / keepalive
    Ping = 30,
    Pong = 31,
    Stats = 32
}
