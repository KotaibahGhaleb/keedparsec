using KeedParsecNative.Contracts.Input;
using KeedParsecNative.Contracts.Session;
using KeedParsecNative.Contracts.Signaling;
using KeedParsecNative.Contracts.Streaming;
using KeedParsecNative.Core.Abstractions;
using KeedParsecNative.Core.Session;

namespace KeedParsecNative.Signaling;

/// <summary>
/// UI-agnostic room session coordinator. Owns the signaling connection, room
/// lifecycle state machine, peer tracking, host approval flow, and WebRTC
/// negotiation (SDP offer/answer + trickle ICE) once a media transport is
/// provided. Point-to-point messages are targeted via
/// <see cref="SignalingEnvelope.TargetPeerId"/>.
/// </summary>
public sealed class RoomSessionCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan JoinRequestCooldown = TimeSpan.FromSeconds(2);

    /// <summary>Message types that must be addressed to a specific peer.</summary>
    private static readonly HashSet<SignalingMessageType> PointToPointTypes = new()
    {
        SignalingMessageType.JoinAccepted,
        SignalingMessageType.JoinRejected,
        SignalingMessageType.HostApprovalRequest,
        SignalingMessageType.HostApprovalResponse,
        SignalingMessageType.Offer,
        SignalingMessageType.Answer,
        SignalingMessageType.IceCandidate,
        SignalingMessageType.IceCandidateEnd,
        SignalingMessageType.Stats
    };

    private readonly ISignalingClient _signaling;
    private readonly SessionStateMachine _stateMachine;
    private readonly JoinTokenService _joinTokens;
    private readonly Func<IMediaTransport>? _mediaTransportFactory;
    private readonly bool _requireHostApproval;
    private readonly int _roomCodeLength;
    private readonly Dictionary<string, PeerInfo> _peers = new();
    private readonly Dictionary<string, DateTimeOffset> _lastJoinRequestUtc = new();
    private readonly object _peerGate = new();
    private CancellationTokenSource? _cts;
    private IMediaTransport? _mediaTransport;
    private readonly Dictionary<string, IMediaTransport> _peerTransports = new();
    private readonly Dictionary<string, PeerInputPermissions> _peerPermissions = new();
    private readonly object _transportGate = new();
    private readonly object _permissionGate = new();
    private string? _remotePeerId;
    private string? _pendingJoinPeerId;
    private string? _pendingJoinDisplayName;
    private long _sequence;

    public RoomSessionCoordinator(
        ISignalingClient signaling,
        JoinTokenService joinTokens,
        bool requireHostApproval = true,
        int roomCodeLength = 6,
        Func<IMediaTransport>? mediaTransportFactory = null)
    {
        _signaling = signaling;
        _joinTokens = joinTokens;
        _requireHostApproval = requireHostApproval;
        _roomCodeLength = roomCodeLength;
        _mediaTransportFactory = mediaTransportFactory;
        _stateMachine = new SessionStateMachine(new SessionInfo
        {
            RoomId = string.Empty,
            Role = SessionRole.Host,
            LocalPeerId = CreatePeerId("local")
        });

        _signaling.MessageReceived += OnSignalingMessageAsync;
        _signaling.ConnectionStateChanged += OnSignalingConnectionStateAsync;
        _stateMachine.StateChanged += OnSessionStateChangedAsync;
    }

    public SessionInfo Info => _stateMachine.Info;
    public string? RoomCode { get; private set; }
    public string? PendingJoinPeerId => _pendingJoinPeerId;
    public string? PendingJoinDisplayName => _pendingJoinDisplayName;
    public string? RemotePeerId => _remotePeerId;
    public IMediaTransport? MediaTransport => _mediaTransport;

    /// <summary>All active per-peer media transports (multi-peer host fan-out).</summary>
    public IReadOnlyList<IMediaTransport> MediaTransports
    {
        get
        {
            lock (_transportGate)
            {
                return _peerTransports.Count > 0
                    ? _peerTransports.Values.ToList()
                    : (_mediaTransport is null ? Array.Empty<IMediaTransport>() : new[] { _mediaTransport });
            }
        }
    }
    public IReadOnlyList<PeerInfo> Peers
    {
        get { lock (_peerGate) { return _peers.Values.ToList(); } }
    }

    /// <summary>Default permissions applied to newly admitted peers (host-side).</summary>
    public PeerInputPermissions DefaultPeerInputPermissions { get; set; } = PeerInputPermissions.All;

    /// <summary>Fired when a peer's input permissions change (for state release).</summary>
    public event Func<string, PeerInputPermissions, CancellationToken, Task>? PeerPermissionsChanged;

    public PeerInputPermissions GetPeerPermissions(string peerId)
    {
        lock (_permissionGate)
        {
            return _peerPermissions.TryGetValue(peerId, out var p) ? p : DefaultPeerInputPermissions;
        }
    }

    public async Task SetPeerInputPermissionsAsync(string peerId, PeerInputPermissions permissions, CancellationToken cancellationToken = default)
    {
        lock (_permissionGate)
        {
            _peerPermissions[peerId] = permissions;
        }
        if (PeerPermissionsChanged is not null)
        {
            await PeerPermissionsChanged.Invoke(peerId, permissions, cancellationToken);
        }
    }

    /// <summary>Resolves the peerId owning a media transport (host fan-out).</summary>
    public bool TryGetPeerIdForTransport(IMediaTransport transport, out string peerId)
    {
        lock (_transportGate)
        {
            foreach (var kv in _peerTransports)
            {
                if (ReferenceEquals(kv.Value, transport))
                {
                    peerId = kv.Key;
                    return true;
                }
            }
        }
        peerId = string.Empty;
        return false;
    }

    public event Func<SessionInfo, SessionInfo, CancellationToken, Task>? SessionStateChanged;
    public event Func<PeerInfo, CancellationToken, Task>? PeerJoined;
    public event Func<string, CancellationToken, Task>? PeerLeft;
    public event Func<string, string, CancellationToken, Task>? JoinApprovalRequested;
    public event Func<string, CancellationToken, Task>? StatusChanged;
    /// <summary>Fired when the media transport is created and ready for tracks.</summary>
    public event Func<IMediaTransport, CancellationToken, Task>? MediaTransportReady;

    // ---- Public API ----

    public async Task StartHostingAsync(
        Uri brokerUri,
        string clientIdPrefix,
        VideoStreamConfig? videoConfig = null,
        AudioStreamConfig? audioConfig = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfBusy();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cts.Token;

        RoomCode = RoomCodeGenerator.Generate(_roomCodeLength);
        _stateMachine.SetRole(SessionRole.Host);
        _stateMachine.SetRoomId(RoomCode);
        await SetStatusAsync($"Hosting room {RoomCode}. Waiting for clients…", ct);

        await _stateMachine.BeginConnectAsync(ct);
        await _signaling.ConnectAsync(brokerUri, $"{clientIdPrefix}-{Info.LocalPeerId}", ct);
        await _signaling.SubscribeAsync(SignalingTopics.RoomSignalTopic(RoomCode), cancellationToken: ct);
        await _stateMachine.MarkAuthenticatingAsync(ct);
        await _stateMachine.MarkNegotiatingAsync(null, ct);
        // Host reaches "Connected" only when a client's peer connection is up.
        await SetStatusAsync($"Room {RoomCode} is open. Share the code with your client.", ct);
    }

    public async Task JoinAsync(
        Uri brokerUri,
        string clientIdPrefix,
        string roomCode,
        string displayName,
        VideoStreamConfig? videoConfig = null,
        AudioStreamConfig? audioConfig = null,
        string? joinToken = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfBusy();
        if (!RoomCodeGenerator.IsValidFormat(roomCode, _roomCodeLength))
        {
            throw new ArgumentException($"Room code must be {_roomCodeLength} characters.", nameof(roomCode));
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cts.Token;

        RoomCode = roomCode.Trim().ToUpperInvariant();
        _stateMachine.SetRole(SessionRole.Client);
        _stateMachine.SetRoomId(RoomCode);
        await SetStatusAsync($"Joining room {RoomCode}…", ct);

        await _stateMachine.BeginConnectAsync(ct);
        await _signaling.ConnectAsync(brokerUri, $"{clientIdPrefix}-{Info.LocalPeerId}", ct);
        await _signaling.SubscribeAsync(SignalingTopics.RoomSignalTopic(RoomCode), cancellationToken: ct);
        await _stateMachine.MarkAuthenticatingAsync(ct);
        // Enter Negotiating before publishing so a fast host response (e.g. a
        // valid join token auto-accept) can transition us forward.
        await _stateMachine.MarkNegotiatingAsync("pending", ct);

        await _signaling.PublishPayloadAsync(NewEnvelope(SignalingMessageType.JoinRequest, targetPeerId: null), new JoinRequestPayload
        {
            DisplayName = displayName,
            VideoConfig = videoConfig ?? new VideoStreamConfig(),
            AudioConfig = audioConfig ?? new AudioStreamConfig(),
            ClientVersion = typeof(RoomSessionCoordinator).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            JoinToken = joinToken
        }, cancellationToken: ct);

        await SetStatusAsync("Join request sent. Waiting for host approval…", ct);
    }

    public async Task ApproveJoinAsync(CancellationToken cancellationToken = default)
    {
        var peerId = _pendingJoinPeerId ?? throw new InvalidOperationException("No pending join request.");
        var ct = _cts?.Token ?? cancellationToken;

        await _signaling.PublishPayloadAsync(NewEnvelope(SignalingMessageType.JoinAccepted, peerId),
            new JoinResponsePayload { Accepted = true }, cancellationToken: ct);

        await AddPeerAsync(peerId, SessionRole.Client, approved: true, ct);
        ClearPendingJoin();
        await SetStatusAsync("Client approved. Starting connection…", ct);
        await StartNegotiationForPeerAsync(peerId, ct);
    }

    public async Task DenyJoinAsync(CancellationToken cancellationToken = default)
    {
        var peerId = _pendingJoinPeerId ?? throw new InvalidOperationException("No pending join request.");
        var ct = _cts?.Token ?? cancellationToken;

        await _signaling.PublishPayloadAsync(NewEnvelope(SignalingMessageType.JoinRejected, peerId),
            new JoinResponsePayload { Accepted = false, Reason = "Denied by host" }, cancellationToken: ct);

        ClearPendingJoin();
        await SetStatusAsync("Join request denied.", ct);
    }

    public async Task LeaveAsync(CancellationToken cancellationToken = default)
    {
        var ct = _cts?.Token ?? cancellationToken;
        try
        {
            if (RoomCode is not null)
            {
                var leave = NewEnvelope(SignalingMessageType.Leave, _remotePeerId);
                await _signaling.PublishAsync(leave, cancellationToken: ct);
            }
            await SafeTransitionAsync(ct2 => _stateMachine.BeginDisconnectAsync(ct2), ct);
            await _signaling.DisconnectAsync(ct);
            await SafeTransitionAsync(ct2 => _stateMachine.ResetToDisconnectedAsync(ct2), ct);
            await SetStatusAsync("Session ended.", ct);
        }
        finally
        {
            await DisposeAllMediaTransportsAsync();
            _cts?.Dispose();
            _cts = null;
            ClearPendingJoin();
            lock (_peerGate) { _peers.Clear(); }
            _remotePeerId = null;
            RoomCode = null;
        }
    }

    /// <summary>Issues a join token for sharing (host side).</summary>
    public string CreateJoinToken(string? peerId, TimeSpan ttl) =>
        _joinTokens.CreateToken(RoomCode ?? string.Empty, peerId, ttl);

    // ---- Signaling handlers ----

    private async Task OnSignalingMessageAsync(SignalingEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.SenderId == Info.LocalPeerId || RoomCode is null ||
            !string.Equals(envelope.RoomId, RoomCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Point-to-point messages must be addressed to us; room-wide types
        // (JoinRequest, Leave, PeerList, SessionState) may have no target.
        if (envelope.TargetPeerId is not null &&
            !string.Equals(envelope.TargetPeerId, Info.LocalPeerId, StringComparison.Ordinal))
        {
            return;
        }
        if (PointToPointTypes.Contains(envelope.Type) &&
            envelope.TargetPeerId is null &&
            envelope.Type is not (SignalingMessageType.JoinAccepted or SignalingMessageType.JoinRejected))
        {
            // JoinAccepted/Rejected are sent targeted; tolerate untargeted for compat.
            if (envelope.Type is SignalingMessageType.Offer or SignalingMessageType.Answer
                or SignalingMessageType.IceCandidate or SignalingMessageType.IceCandidateEnd
                or SignalingMessageType.Stats or SignalingMessageType.HostApprovalRequest
                or SignalingMessageType.HostApprovalResponse)
            {
                return;
            }
        }

        try
        {
            switch (envelope.Type)
            {
                case SignalingMessageType.JoinRequest when Info.Role == SessionRole.Host:
                    await HandleJoinRequestAsync(envelope, cancellationToken);
                    break;

                case SignalingMessageType.JoinAccepted when Info.Role == SessionRole.Client:
                    await HandleJoinAcceptedAsync(envelope, cancellationToken);
                    break;

                case SignalingMessageType.JoinRejected when Info.Role == SessionRole.Client:
                    await HandleJoinRejectedAsync(envelope, cancellationToken);
                    break;

                case SignalingMessageType.Leave:
                    await RemovePeerAsync(envelope.SenderId, cancellationToken);
                    break;

                case SignalingMessageType.Offer when Info.Role == SessionRole.Client:
                    await HandleRemoteOfferAsync(envelope, cancellationToken);
                    break;

                case SignalingMessageType.Answer when Info.Role == SessionRole.Host:
                    await HandleRemoteAnswerAsync(envelope, cancellationToken);
                    break;

                case SignalingMessageType.IceCandidate:
                    await HandleRemoteIceCandidateAsync(envelope, cancellationToken);
                    break;

                case SignalingMessageType.IceCandidateEnd:
                    break;

                case SignalingMessageType.Ping:
                    await _signaling.PublishAsync(NewEnvelope(SignalingMessageType.Pong, envelope.SenderId), cancellationToken: cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[coordinator] message handler error: {ex}");
            await SetStatusAsync($"Signaling error: {ex.Message}", cancellationToken);
        }
    }

    private async Task HandleJoinRequestAsync(SignalingEnvelope envelope, CancellationToken ct)
    {
        // Rate limit join attempts per peer.
        var now = DateTimeOffset.UtcNow;
        lock (_peerGate)
        {
            if (_lastJoinRequestUtc.TryGetValue(envelope.SenderId, out var last) &&
                now - last < JoinRequestCooldown)
            {
                return;
            }
            _lastJoinRequestUtc[envelope.SenderId] = now;
        }

        var payload = SignalingMessageSerializer.DeserializePayload<JoinRequestPayload>(envelope);
        var tokenValid = payload?.JoinToken is not null &&
                         _joinTokens.ConsumeToken(envelope.RoomId, payload.JoinToken);

        if (_requireHostApproval && !tokenValid)
        {
            _pendingJoinPeerId = envelope.SenderId;
            _pendingJoinDisplayName = payload?.DisplayName ?? envelope.SenderId;
            if (JoinApprovalRequested is not null)
            {
                await JoinApprovalRequested.Invoke(_pendingJoinPeerId, _pendingJoinDisplayName, ct);
            }
            await SetStatusAsync($"Join request from {_pendingJoinDisplayName} — approve or deny.", ct);
            return;
        }

        await _signaling.PublishPayloadAsync(NewEnvelope(SignalingMessageType.JoinAccepted, envelope.SenderId),
            new JoinResponsePayload { Accepted = true }, cancellationToken: ct);
        await AddPeerAsync(envelope.SenderId, SessionRole.Client, approved: true, ct);
        await StartNegotiationForPeerAsync(envelope.SenderId, ct);
    }

    private async Task HandleJoinAcceptedAsync(SignalingEnvelope envelope, CancellationToken ct)
    {
        var payload = SignalingMessageSerializer.DeserializePayload<JoinResponsePayload>(envelope);
        if (payload?.Accepted != true)
        {
            return;
        }

        _remotePeerId = envelope.SenderId;
        await AddPeerAsync(envelope.SenderId, SessionRole.Host, approved: true, ct);
        await SetStatusAsync("Join accepted. Waiting for connection…", ct);
        await EnsureMediaTransportAsync(envelope.SenderId, ct);
        // Client waits for the host's Offer; nothing else to do here.
    }

    private async Task HandleJoinRejectedAsync(SignalingEnvelope envelope, CancellationToken ct)
    {
        var payload = SignalingMessageSerializer.DeserializePayload<JoinResponsePayload>(envelope);
        var reason = payload?.Reason ?? "Join rejected by host";
        await SafeTransitionAsync(ct2 => _stateMachine.MarkFailedAsync(reason, ct2), ct);
        await SafeTransitionAsync(ct2 => _stateMachine.ResetToDisconnectedAsync(ct2), ct);
        await SetStatusAsync($"Join rejected: {reason}", ct);
    }

    private async Task HandleRemoteOfferAsync(SignalingEnvelope envelope, CancellationToken ct)
    {
        var payload = SignalingMessageSerializer.DeserializePayload<SdpPayload>(envelope);
        if (payload?.SdpType != "offer" || payload.Sdp is null)
        {
            return;
        }

        var transport = await EnsureMediaTransportAsync(envelope.SenderId, ct);
        if (transport is null)
        {
            return;
        }
        await transport.SetRemoteDescriptionAsync(payload.Sdp, "offer", ct);
        await transport.CreateAnswerAsync(ct);
        // LocalDescriptionReady event sends the Answer envelope.
    }

    private async Task HandleRemoteAnswerAsync(SignalingEnvelope envelope, CancellationToken ct)
    {
        var payload = SignalingMessageSerializer.DeserializePayload<SdpPayload>(envelope);
        if (payload?.SdpType != "answer" || payload.Sdp is null)
        {
            return;
        }
        var transport = GetPeerTransport(envelope.SenderId);
        if (transport is null)
        {
            return;
        }
        await transport.SetRemoteDescriptionAsync(payload.Sdp, "answer", ct);
    }

    private async Task HandleRemoteIceCandidateAsync(SignalingEnvelope envelope, CancellationToken ct)
    {
        var payload = SignalingMessageSerializer.DeserializePayload<IceCandidatePayload>(envelope);
        var transport = GetPeerTransport(envelope.SenderId);
        if (payload?.Candidate is null || transport is null)
        {
            return;
        }
        await transport.AddIceCandidateAsync(new IceCandidateInfo
        {
            Candidate = payload.Candidate,
            SdpMid = payload.SdpMid,
            SdpMLineIndex = payload.SdpMLineIndex
        }, ct);
    }

    // ---- WebRTC negotiation ----

    private IMediaTransport? GetPeerTransport(string peerId)
    {
        lock (_transportGate)
        {
            return _peerTransports.TryGetValue(peerId, out var t) ? t : null;
        }
    }

    private async Task<IMediaTransport?> EnsureMediaTransportAsync(string peerId, CancellationToken ct)
    {
        if (_mediaTransportFactory is null)
        {
            return null;
        }

        IMediaTransport transport;
        bool created;
        lock (_transportGate)
        {
            if (_peerTransports.TryGetValue(peerId, out var existing))
            {
                transport = existing;
                created = false;
            }
            else
            {
                transport = _mediaTransportFactory()!;
                created = true;
            }
            if (created)
            {
                _peerTransports[peerId] = transport;
                _mediaTransport ??= transport;
                transport.LocalDescriptionReady += (d, c) => OnLocalDescriptionReadyAsync(peerId, d, c);
                transport.IceCandidateGathered += (cand, c) => OnIceCandidateGatheredAsync(peerId, cand, c);
                transport.ConnectionStateChanged += (st, c) => OnMediaConnectionStateAsync(peerId, st, c);
            }
        }

        if (created && MediaTransportReady is not null)
        {
            await MediaTransportReady.Invoke(transport, ct);
        }
        return transport;
    }

    private async Task DisposePeerTransportAsync(string peerId)
    {
        IMediaTransport? transport;
        lock (_transportGate)
        {
            if (!_peerTransports.Remove(peerId, out transport))
            {
                return;
            }
            if (ReferenceEquals(_mediaTransport, transport))
            {
                _mediaTransport = _peerTransports.Values.FirstOrDefault();
            }
        }
        await transport.DisposeAsync();
    }

    private async Task StartNegotiationForPeerAsync(string peerId, CancellationToken ct)
    {
        _remotePeerId = peerId;
        var transport = await EnsureMediaTransportAsync(peerId, ct);
        if (transport is null)
        {
            // No media transport configured: signaling-only session.
            if (Info.Role == SessionRole.Host && _stateMachine.CanTransitionTo(SessionState.Connected))
            {
                await _stateMachine.MarkConnectedAsync(peerId, ct);
            }
            return;
        }

        await transport.CreateOfferAsync(ct);
        // LocalDescriptionReady fires with the offer; ICE candidates trickle.
    }

    private async Task OnLocalDescriptionReadyAsync(string peerId, LocalDescription description, CancellationToken ct)
    {
        if (RoomCode is null)
        {
            return;
        }

        var type = description.Type;
        var messageType = type == "offer" ? SignalingMessageType.Offer : SignalingMessageType.Answer;
        await _signaling.PublishPayloadAsync(
            NewEnvelope(messageType, peerId),
            new SdpPayload { Sdp = description.Sdp, SdpType = type },
            cancellationToken: ct);
        await SetStatusAsync($"Sent WebRTC {type}. Waiting for peer…", ct);
    }

    private async Task OnIceCandidateGatheredAsync(string peerId, IceCandidateInfo candidate, CancellationToken ct)
    {
        if (RoomCode is null)
        {
            return;
        }

        await _signaling.PublishPayloadAsync(
            NewEnvelope(SignalingMessageType.IceCandidate, peerId),
            new IceCandidatePayload
            {
                Candidate = candidate.Candidate,
                SdpMid = candidate.SdpMid ?? string.Empty,
                SdpMLineIndex = candidate.SdpMLineIndex
            },
            cancellationToken: ct);
    }

    private async Task OnMediaConnectionStateAsync(string peerId, string state, CancellationToken ct)
    {
        switch (state)
        {
            case "connected":
                if (_stateMachine.CanTransitionTo(SessionState.Connected))
                {
                    await _stateMachine.MarkConnectedAsync(peerId, ct);
                }
                await SetStatusAsync("Connected.", ct);
                break;

            case "failed":
                if (_stateMachine.CanTransitionTo(SessionState.Failed))
                {
                    await _stateMachine.MarkFailedAsync("Peer connection failed", ct);
                }
                await SetStatusAsync("Peer connection failed.", ct);
                break;

            case "disconnected":
                if (_stateMachine.CanTransitionTo(SessionState.Reconnecting))
                {
                    await _stateMachine.BeginReconnectAsync(ct);
                    await SetStatusAsync("Connection degraded — attempting to recover…", ct);
                }
                break;

            case "closed":
                break;
        }
    }

    private async Task DisposeAllMediaTransportsAsync()
    {
        List<IMediaTransport> transports;
        lock (_transportGate)
        {
            transports = _peerTransports.Values.ToList();
            _peerTransports.Clear();
            _mediaTransport = null;
        }
        foreach (var t in transports)
        {
            try { await t.DisposeAsync(); } catch { }
        }
    }

    private async Task OnSignalingConnectionStateAsync(ConnectionState state, CancellationToken cancellationToken)
    {
        if (state == ConnectionState.Failed &&
            _stateMachine.Info.State is SessionState.Connecting or SessionState.Authenticating
                or SessionState.Negotiating or SessionState.Connected)
        {
            await SafeTransitionAsync(ct2 => _stateMachine.MarkFailedAsync("Signaling connection lost", ct2), cancellationToken);
            await SafeTransitionAsync(ct2 => _stateMachine.ResetToDisconnectedAsync(ct2), cancellationToken);
            await SetStatusAsync("Signaling connection lost.", cancellationToken);
        }
    }

    private Task OnSessionStateChangedAsync(SessionInfo current, SessionInfo previous, CancellationToken ct)
    {
        SessionStateChanged?.Invoke(current, previous, ct);
        return Task.CompletedTask;
    }

    // ---- Helpers ----

    private SignalingEnvelope NewEnvelope(SignalingMessageType type, string? targetPeerId) => new()
    {
        Type = type,
        RoomId = RoomCode!,
        SenderId = Info.LocalPeerId,
        TargetPeerId = targetPeerId,
        Sequence = Interlocked.Increment(ref _sequence),
        TimestampUtcTicks = DateTime.UtcNow.Ticks
    };

    private async Task AddPeerAsync(string peerId, SessionRole role, bool approved, CancellationToken ct)
    {
        var peer = new PeerInfo
        {
            PeerId = peerId,
            Role = role,
            IsApproved = approved,
            JoinedAtUtc = DateTimeOffset.UtcNow
        };
        lock (_peerGate)
        {
            _peers[peerId] = peer;
        }
        if (PeerJoined is not null)
        {
            await PeerJoined.Invoke(peer, ct);
        }
    }

    private async Task RemovePeerAsync(string peerId, CancellationToken ct)
    {
        bool removed;
        lock (_peerGate)
        {
            removed = _peers.Remove(peerId);
        }
        lock (_permissionGate)
        {
            _peerPermissions.Remove(peerId);
        }
        if (removed && PeerLeft is not null)
        {
            await PeerLeft.Invoke(peerId, ct);
        }

        await DisposePeerTransportAsync(peerId);
        if (peerId == _remotePeerId)
        {
            _remotePeerId = null;
            if (_stateMachine.CanTransitionTo(SessionState.Disconnecting))
            {
                await SafeTransitionAsync(ct2 => _stateMachine.BeginDisconnectAsync(ct2), ct);
            }
            if (_stateMachine.CanTransitionTo(SessionState.Disconnected))
            {
                await SafeTransitionAsync(ct2 => _stateMachine.ResetToDisconnectedAsync(ct2), ct);
            }
            await SetStatusAsync("Peer left the session.", ct);
        }
    }

    private void ClearPendingJoin()
    {
        _pendingJoinPeerId = null;
        _pendingJoinDisplayName = null;
    }

    private void ThrowIfBusy()
    {
        if (_cts is not null)
        {
            throw new InvalidOperationException("A session is already active. Leave it first.");
        }
    }

    private async Task SetStatusAsync(string message, CancellationToken ct)
    {
        if (StatusChanged is not null)
        {
            await StatusChanged.Invoke(message, ct);
        }
    }

    private static async Task SafeTransitionAsync(Func<CancellationToken, Task> transition, CancellationToken ct)
    {
        try
        {
            await transition(ct);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string CreatePeerId(string prefix)
    {
        var bytes = new byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return $"{prefix}-{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await LeaveAsync();
        }
        catch
        {
            // best-effort cleanup
        }
        await _signaling.DisposeAsync();
    }
}
