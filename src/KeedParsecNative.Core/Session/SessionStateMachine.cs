using KeedParsecNative.Contracts.Session;

namespace KeedParsecNative.Core.Session;

/// <summary>
/// Explicit session lifecycle state machine.
/// Disconnected -> Connecting -> Authenticating -> Negotiating -> Connected
/// Any state -> Disconnecting -> Disconnected
/// Connected/Reconnecting -> Failed on unrecoverable error.
/// </summary>
public sealed class SessionStateMachine
{
    private static readonly Dictionary<SessionState, HashSet<SessionState>> AllowedTransitions = new()
    {
        [SessionState.Disconnected] = new() { SessionState.Connecting },
        [SessionState.Connecting] = new() { SessionState.Authenticating, SessionState.Failed, SessionState.Disconnecting },
        [SessionState.Authenticating] = new() { SessionState.Negotiating, SessionState.Failed, SessionState.Disconnecting },
        [SessionState.Negotiating] = new() { SessionState.Connected, SessionState.Failed, SessionState.Disconnecting },
        [SessionState.Connected] = new() { SessionState.Reconnecting, SessionState.Disconnecting, SessionState.Failed },
        [SessionState.Reconnecting] = new() { SessionState.Connected, SessionState.Failed, SessionState.Disconnecting },
        [SessionState.Disconnecting] = new() { SessionState.Disconnected },
        [SessionState.Failed] = new() { SessionState.Disconnected, SessionState.Connecting }
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SessionInfo _info;

    public SessionStateMachine(SessionInfo initial)
    {
        _info = initial;
    }

    public SessionInfo Info => _info;

    /// <summary>Sets the room id once the session target is known.</summary>
    public void SetRoomId(string roomId)
    {
        _info = _info with { RoomId = roomId };
    }

    /// <summary>Sets the local role before connecting (Host or Client).</summary>
    public void SetRole(SessionRole role)
    {
        _info = _info with { Role = role };
    }

    public event Func<SessionInfo, SessionInfo, CancellationToken, Task>? StateChanged;

    public bool CanTransitionTo(SessionState next) =>
        AllowedTransitions.TryGetValue(_info.State, out var set) && set.Contains(next);

    public async Task TransitionToAsync(
        SessionState next,
        string? remotePeerId = null,
        string? failureReason = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CanTransitionTo(next))
            {
                throw new InvalidOperationException(
                    $"Invalid session transition {_info.State} -> {next}");
            }

            var previous = _info;
            _info = _info with
            {
                State = next,
                RemotePeerId = remotePeerId ?? _info.RemotePeerId,
                FailureReason = failureReason ?? (next == SessionState.Connected || next == SessionState.Disconnected
                    ? null
                    : _info.FailureReason)
            };

            if (StateChanged is not null)
            {
                await StateChanged.Invoke(_info, previous, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Begins a connection attempt; throws if not in a startable state.</summary>
    public async Task BeginConnectAsync(CancellationToken cancellationToken = default)
    {
        await TransitionToAsync(SessionState.Connecting, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public Task MarkAuthenticatingAsync(CancellationToken cancellationToken = default) =>
        TransitionToAsync(SessionState.Authenticating, cancellationToken: cancellationToken);

    public Task MarkNegotiatingAsync(string? remotePeerId, CancellationToken cancellationToken = default) =>
        TransitionToAsync(SessionState.Negotiating, remotePeerId: remotePeerId, cancellationToken: cancellationToken);

    public Task MarkConnectedAsync(string? remotePeerId = null, CancellationToken cancellationToken = default) =>
        TransitionToAsync(SessionState.Connected, remotePeerId: remotePeerId, cancellationToken: cancellationToken);

    public Task BeginReconnectAsync(CancellationToken cancellationToken = default) =>
        TransitionToAsync(SessionState.Reconnecting, cancellationToken: cancellationToken);

    public Task BeginDisconnectAsync(CancellationToken cancellationToken = default) =>
        TransitionToAsync(SessionState.Disconnecting, cancellationToken: cancellationToken);

    public Task MarkFailedAsync(string reason, CancellationToken cancellationToken = default) =>
        TransitionToAsync(SessionState.Failed, failureReason: reason, cancellationToken: cancellationToken);

    public Task ResetToDisconnectedAsync(CancellationToken cancellationToken = default) =>
        TransitionToAsync(SessionState.Disconnected, cancellationToken: cancellationToken);
}
