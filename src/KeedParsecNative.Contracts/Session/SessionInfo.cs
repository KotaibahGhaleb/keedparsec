namespace KeedParsecNative.Contracts.Session;

public enum SessionRole
{
    Host = 0,
    Client = 1
}

public enum SessionState
{
    Disconnected = 0,
    Connecting = 1,
    Authenticating = 2,
    Negotiating = 3,
    Connected = 4,
    Reconnecting = 5,
    Disconnecting = 6,
    Failed = 7
}

public sealed record SessionInfo
{
    public required string RoomId { get; init; }
    public required SessionRole Role { get; init; }
    public required string LocalPeerId { get; init; }
    public string? RemotePeerId { get; init; }
    public SessionState State { get; init; } = SessionState.Disconnected;
    public string? FailureReason { get; init; }
}

public sealed record PeerInfo
{
    public required string PeerId { get; init; }
    public required SessionRole Role { get; init; }
    public bool IsApproved { get; init; }
    public DateTimeOffset JoinedAtUtc { get; init; }
}
