using System.Collections.ObjectModel;
using System.Diagnostics;
using KeedParsecNative.Capture.Monitors;
using KeedParsecNative.Contracts.Input;
using KeedParsecNative.Contracts.Session;
using KeedParsecNative.Contracts.Streaming;
using KeedParsecNative.Core.Abstractions;
using KeedParsecNative.Core.Session;
using KeedParsecNative.Infrastructure.Configuration;
using KeedParsecNative.Signaling;
using KeedParsecNative.App.Services;
namespace KeedParsecNative.App.ViewModels;

/// <summary>
/// WPF-facing view model. Delegates session orchestration to
/// <see cref="RoomSessionCoordinator"/> and maps its events to bindable state.
/// </summary>
public sealed class SessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly RoomSessionCoordinator _coordinator;
    private readonly WinFormsMonitorSource _monitorSource = new();
    private VideoStreamConfig _videoConfig;
    private AudioStreamConfig _audioConfig;
    private SessionMediaPipeline? _mediaPipeline;

    public SessionViewModel(AppConfig config, ISignalingClient? signaling = null)
    {
        _config = config;
        _videoConfig = config.Streaming.Video;
        _audioConfig = config.Streaming.Audio;

        var joinTokens = new JoinTokenService(
            string.IsNullOrEmpty(config.Security.JoinTokenSecret)
                ? Guid.NewGuid().ToByteArray()
                : System.Text.Encoding.UTF8.GetBytes(config.Security.JoinTokenSecret));

        _coordinator = new RoomSessionCoordinator(
            signaling ?? new MqttSignalingClient(),
            joinTokens,
            config.Security.RequireHostApproval,
            config.Security.RoomCodeLength,
            mediaTransportFactory: () => new Streaming.Transport.SipsorceryMediaTransport(
                config.Turn.Servers,
                config.Turn.StunServers.Count > 0 ? config.Turn.StunServers : null));

        _coordinator.SessionStateChanged += OnCoordinatorStateChangedAsync;
        _coordinator.StatusChanged += OnCoordinatorStatusAsync;
        _coordinator.JoinApprovalRequested += OnJoinApprovalRequestedAsync;
        _coordinator.PeerJoined += OnPeerJoinedAsync;
        _coordinator.PeerLeft += OnPeerLeftAsync;
        _coordinator.MediaTransportReady += OnMediaTransportReadyAsync;
        _coordinator.PeerPermissionsChanged += OnPeerPermissionsChangedAsync;

        IReadOnlyList<MonitorInfo> monitors;
        try
        {
            monitors = _monitorSource.Enumerate();
            System.Diagnostics.Debug.WriteLine($"[VM] Found {monitors.Count} monitors");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VM] Monitor enumeration failed: {ex.Message}");
            monitors = Array.Empty<MonitorInfo>();
        }
        if (monitors.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine("[VM] No monitors found, using fallback");
            monitors = new List<MonitorInfo>
            {
                new() { Index = 0, DeviceName = "Primary", Width = 1920, Height = 1080, X = 0, Y = 0, IsPrimary = true }
            };
        }
        Monitors = new ObservableCollection<MonitorInfo>(monitors);
        SelectedMonitorIndex = config.Streaming.SelectedMonitorIndex;
        BrokerUriText = config.Signaling.BrokerUri;

        StartHostingCommand = new RelayCommand(StartHostingAsync,
            () => _coordinator.Info.State is SessionState.Disconnected or SessionState.Failed);
        StopSessionCommand = new RelayCommand(StopSessionAsync,
            () => _coordinator.Info.State is not SessionState.Disconnected and not SessionState.Failed);
        ConnectCommand = new RelayCommand(ConnectAsync,
            () => _coordinator.Info.State is SessionState.Disconnected or SessionState.Failed);
        ApproveJoinCommand = new RelayCommand(ApproveJoinAsync,
            () => _coordinator.PendingJoinPeerId is not null);
        DenyJoinCommand = new RelayCommand(DenyJoinAsync,
            () => _coordinator.PendingJoinPeerId is not null);
    }

    // ---- Bindable state ----

    public string? RoomCode => _coordinator.RoomCode;
    public SessionRole Role => _coordinator.Info.Role;
    public SessionState SessionState => _coordinator.Info.State;
    public string? FailureReason => _coordinator.Info.FailureReason;
    public string LocalPeerId => _coordinator.Info.LocalPeerId;
    public string? PendingJoinPeerId => _coordinator.PendingJoinPeerId;
    public string? PendingJoinDisplayName => _coordinator.PendingJoinDisplayName;

    public string? JoinRoomCodeInput
    {
        get => _joinRoomCodeInput;
        set => SetProperty(ref _joinRoomCodeInput, value);
    }
    private string? _joinRoomCodeInput;

    public string? DisplayNameInput
    {
        get => _displayNameInput;
        set => SetProperty(ref _displayNameInput, value);
    }
    private string? _displayNameInput = Environment.UserName;

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }
    private string? _statusMessage = "Idle";

    public string? BrokerUriText
    {
        get => _brokerUriText;
        set
        {
            if (SetProperty(ref _brokerUriText, value) && value is not null)
            {
                _config.Signaling.BrokerUri = value;
            }
        }
    }
    private string? _brokerUriText;

    public ObservableCollection<MonitorInfo> Monitors { get; }
    public ObservableCollection<string> PeerSummaries { get; } = new();

    public int SelectedMonitorIndex
    {
        get => _selectedMonitorIndex;
        set => SetProperty(ref _selectedMonitorIndex, value);
    }
    private int _selectedMonitorIndex;

    public VideoStreamConfig VideoConfig
    {
        get => _videoConfig;
        private set => SetProperty(ref _videoConfig, value);
    }

    public AudioStreamConfig AudioConfig
    {
        get => _audioConfig;
        private set => SetProperty(ref _audioConfig, value);
    }

    public int VideoWidth
    {
        get => _videoConfig.Width;
        set => VideoConfig = _videoConfig with { Width = value };
    }

    public int VideoHeight
    {
        get => _videoConfig.Height;
        set => VideoConfig = _videoConfig with { Height = value };
    }

    public int VideoFps
    {
        get => _videoConfig.FrameRateNumerator / Math.Max(1, _videoConfig.FrameRateDenominator);
        set => VideoConfig = _videoConfig with { FrameRateNumerator = value, FrameRateDenominator = 1 };
    }

    public int VideoBitrateKbps
    {
        get => _videoConfig.TargetBitrateKbps;
        set => VideoConfig = _videoConfig with { TargetBitrateKbps = value };
    }

    public int AudioSampleRate
    {
        get => _audioConfig.SampleRate;
        set => AudioConfig = _audioConfig with { SampleRate = value };
    }

    // ---- Commands ----

    public RelayCommand StartHostingCommand { get; }
    public RelayCommand StopSessionCommand { get; }
    public RelayCommand ConnectCommand { get; }
    public RelayCommand ApproveJoinCommand { get; }
    public RelayCommand DenyJoinCommand { get; }

    // ---- Actions ----

    private async Task StartHostingAsync()
    {
        try
        {
            await _coordinator.StartHostingAsync(ParseBrokerUri(), _config.Signaling.ClientIdPrefix,
                VideoConfig, AudioConfig);
            OnPropertyChanged(nameof(RoomCode));
            OnPropertyChanged(nameof(SessionState));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Host startup failed: {ex.Message}";
        }
    }

    private async Task ConnectAsync()
    {
        var roomCode = JoinRoomCodeInput?.Trim().ToUpperInvariant();
        if (!RoomCodeGenerator.IsValidFormat(roomCode, _config.Security.RoomCodeLength))
        {
            StatusMessage = $"Enter a valid {_config.Security.RoomCodeLength}-character room code.";
            return;
        }

        try
        {
            await _coordinator.JoinAsync(ParseBrokerUri(), _config.Signaling.ClientIdPrefix,
                roomCode!, DisplayNameInput ?? Environment.UserName, VideoConfig, AudioConfig);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Join failed: {ex.Message}";
        }
    }

    private async Task ApproveJoinAsync()
    {
        try
        {
            var approvedPeerId = _coordinator.PendingJoinPeerId;
            await _coordinator.ApproveJoinAsync();
            if (approvedPeerId is not null)
            {
                await _coordinator.SetPeerInputPermissionsAsync(approvedPeerId, PendingJoinPermissions);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Approve failed: {ex.Message}";
        }
        finally
        {
            NotifyPendingJoinChanged();
        }
    }

    private async Task DenyJoinAsync()
    {
        try
        {
            await _coordinator.DenyJoinAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Deny failed: {ex.Message}";
        }
        finally
        {
            NotifyPendingJoinChanged();
        }
    }

    private void NotifyPendingJoinChanged()
    {
        OnPropertyChanged(nameof(PendingJoinPeerId));
        OnPropertyChanged(nameof(PendingJoinDisplayName));
        ApproveJoinCommand.RaiseCanExecuteChanged();
        DenyJoinCommand.RaiseCanExecuteChanged();
    }

    private async Task StopSessionAsync()
    {
        try
        {
            await _coordinator.LeaveAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Disconnect error: {ex.Message}";
        }
    }

    private Uri ParseBrokerUri()
    {
        if (!Uri.TryCreate(BrokerUriText, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "mqtt" && uri.Scheme != "mqtts"))
        {
            throw new InvalidOperationException($"Invalid broker URI: {BrokerUriText}");
        }
        _config.Signaling.BrokerUri = uri.ToString();
        return uri;
    }

    // ---- Coordinator event adapters ----

    private Task OnCoordinatorStateChangedAsync(SessionInfo current, SessionInfo previous, CancellationToken ct)
    {
        OnPropertyChanged(nameof(SessionState));
        OnPropertyChanged(nameof(FailureReason));
        OnPropertyChanged(nameof(Role));
        OnPropertyChanged(nameof(RoomCode));
        OnPropertyChanged(nameof(PendingJoinPeerId));
        OnPropertyChanged(nameof(PendingJoinDisplayName));
        OnPropertyChanged(nameof(LocalPeerId));
        StartHostingCommand.RaiseCanExecuteChanged();
        StopSessionCommand.RaiseCanExecuteChanged();
        ConnectCommand.RaiseCanExecuteChanged();
        ApproveJoinCommand.RaiseCanExecuteChanged();
        DenyJoinCommand.RaiseCanExecuteChanged();

        if (current.State == SessionState.Connected)
        {
            _ = StartMediaPipelineAsync(ct);
        }
        else if (current.State is SessionState.Disconnected or SessionState.Failed)
        {
            _ = StopMediaPipelineAsync();
        }
        return Task.CompletedTask;
    }

    /// <summary>Raised on the UI thread with each decoded client frame (BGRA).</summary>
    public event Func<VideoFrame, CancellationToken, Task>? DecodedFrameReceived;

    public string PipelineStatusText => _mediaPipeline?.PipelineStatus ?? string.Empty;

    /// <summary>Client-side: send a keyboard/mouse input event to the host.</summary>
    public async Task SendClientInputAsync(InputEvent inputEvent, bool reliable)
    {
        if (_mediaPipeline is null || _coordinator.Info.Role != SessionRole.Client ||
            _coordinator.Info.State != SessionState.Connected)
        {
            return;
        }
        await _mediaPipeline.SendInputAsync(inputEvent, reliable);
    }

    private async Task OnMediaTransportReadyAsync(IMediaTransport transport, CancellationToken ct)
    {
        // Late-joining peers get their own transport; attach it to the running
        // host pipeline so encoded frames fan out without re-encoding.
        if (_mediaPipeline is not null && _coordinator.Info.Role == SessionRole.Host)
        {
            _mediaPipeline.AddHostTransport(transport);
        }
        await Task.CompletedTask;
    }

    private async Task StartMediaPipelineAsync(CancellationToken ct)
    {
        var transports = _coordinator.MediaTransports;
        if (transports.Count == 0 || _mediaPipeline is not null)
        {
            return;
        }
        try
        {
            _mediaPipeline = new SessionMediaPipeline(VideoConfig, AudioConfig, SelectedMonitorIndex,
                System.Windows.Application.Current?.Dispatcher,
                peerIdResolver: t => _coordinator.TryGetPeerIdForTransport(t, out var id) ? id : null,
                permissionProvider: id => _coordinator.GetPeerPermissions(id));
            _mediaPipeline.FrameDecoded += OnPipelineFrameDecodedAsync;
            if (_coordinator.Info.Role == SessionRole.Host)
            {
                await _mediaPipeline.StartHostAsync(transports, ct);
            }
            else
            {
                await _mediaPipeline.StartClientAsync(transports, ct);
            }
            StatusMessage = $"Media pipeline: {_mediaPipeline.PipelineStatus}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Media pipeline failed: {ex.Message}";
            await StopMediaPipelineAsync();
        }
    }

    private Task OnPeerPermissionsChangedAsync(string peerId, PeerInputPermissions permissions, CancellationToken ct)
    {
        if (_mediaPipeline is not null)
        {
            return _mediaPipeline.NotifyPeerPermissionsChangedAsync(peerId, ct);
        }
        return Task.CompletedTask;
    }

    /// <summary>Permissions to apply when the pending join is approved.</summary>
    public PeerInputPermissions PendingJoinPermissions => new()
    {
        AllowKeyboard = PendingAllowKeyboard,
        AllowMouse = PendingAllowMouse,
        AllowGamepad = PendingAllowGamepad
    };

    public bool PendingAllowKeyboard { get; set; } = true;
    public bool PendingAllowMouse { get; set; } = true;
    public bool PendingAllowGamepad { get; set; } = true;

    /// <summary>Live per-peer permission toggles shown in the host peer list.</summary>
    public ObservableCollection<PeerPermissionItem> PeerPermissions { get; } = new();
    public bool HasPeerPermissions => PeerPermissions.Count > 0;

    private Task OnPipelineFrameDecodedAsync(VideoFrame frame, CancellationToken ct)
    {
        return DecodedFrameReceived?.Invoke(frame, ct) ?? Task.CompletedTask;
    }

    private async Task StopMediaPipelineAsync()
    {
        if (_mediaPipeline is null)
        {
            return;
        }
        var pipeline = _mediaPipeline;
        _mediaPipeline = null;
        pipeline.FrameDecoded -= OnPipelineFrameDecodedAsync;
        try
        {
            await pipeline.DisposeAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[vm] pipeline dispose: {ex.Message}");
        }
    }

    private Task OnCoordinatorStatusAsync(string message, CancellationToken ct)
    {
        StatusMessage = message;
        return Task.CompletedTask;
    }

    private Task OnJoinApprovalRequestedAsync(string peerId, string displayName, CancellationToken ct)
    {
        OnPropertyChanged(nameof(PendingJoinPeerId));
        OnPropertyChanged(nameof(PendingJoinDisplayName));
        ApproveJoinCommand.RaiseCanExecuteChanged();
        DenyJoinCommand.RaiseCanExecuteChanged();
        return Task.CompletedTask;
    }

    private Task OnPeerJoinedAsync(PeerInfo peer, CancellationToken ct)
    {
        PeerSummaries.Add($"{peer.PeerId} ({peer.Role})");
        if (peer.Role == SessionRole.Client &&
            !PeerPermissions.Any(x => x.PeerId == peer.PeerId))
        {
            PeerPermissions.Add(new PeerPermissionItem(peer.PeerId, peer.PeerId,
                _coordinator.GetPeerPermissions(peer.PeerId),
                perms => _coordinator.SetPeerInputPermissionsAsync(peer.PeerId, perms, ct)));
            OnPropertyChanged(nameof(HasPeerPermissions));
        }
        return Task.CompletedTask;
    }

    private Task OnPeerLeftAsync(string peerId, CancellationToken ct)
    {
        for (var i = PeerPermissions.Count - 1; i >= 0; i--)
        {
            if (PeerPermissions[i].PeerId == peerId)
            {
                PeerPermissions.RemoveAt(i);
            }
        }
        OnPropertyChanged(nameof(HasPeerPermissions));
        for (var i = PeerSummaries.Count - 1; i >= 0; i--)
        {
            if (PeerSummaries[i].StartsWith(peerId, StringComparison.Ordinal))
            {
                PeerSummaries.RemoveAt(i);
            }
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopMediaPipelineAsync();
        await _coordinator.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
