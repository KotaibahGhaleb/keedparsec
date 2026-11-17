using System.Net;
using KeedParsecNative.Contracts.Input;
using KeedParsecNative.Contracts.Streaming;
using KeedParsecNative.Core.Abstractions;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace KeedParsecNative.Streaming.Transport;

/// <summary>
/// Real WebRTC peer connection implemented on SIPSorcery (pure managed .NET).
/// Handles SDP offer/answer, trickle ICE (STUN/TURN), DTLS-SRTP, data
/// channels, and H.264 video / PCMU audio tracks.
/// </summary>
public sealed class SipsorceryMediaTransport : IMediaTransport
{
    private readonly List<RTCIceServer> _iceServers;
    private readonly RTCPeerConnection _pc;
    private readonly List<RTCDataChannel> _channels = new();
    private readonly object _channelGate = new();
    private readonly List<RTCIceCandidateInit> _pendingRemoteCandidates = new();
    private bool _remoteDescriptionSet;
    private uint _audioRtpTimestamp;
    private bool _disposed;

    public SipsorceryMediaTransport(IEnumerable<IceServerConfig>? turnServers = null,
        IEnumerable<string>? stunServers = null)
    {
        _iceServers = new List<RTCIceServer>();

        foreach (var stun in stunServers ?? new[] { "stun:stun.l.google.com:19302" })
        {
            _iceServers.Add(new RTCIceServer { urls = stun });
        }

        if (turnServers is not null)
        {
            foreach (var server in turnServers)
            {
                foreach (var url in server.Urls.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    _iceServers.Add(new RTCIceServer
                    {
                        urls = url.Trim(),
                        username = server.Username,
                        credential = server.Credential,
                        credentialType = RTCIceCredentialType.password
                    });
                }
            }
        }

        var config = new RTCConfiguration
        {
            iceServers = _iceServers,
            X_ICEIncludeAllInterfaceAddresses = true,
            X_GatherTimeoutMs = 3000
        };

        _pc = new RTCPeerConnection(config);
        _pc.onicecandidate += OnIceCandidate;
        _pc.onconnectionstatechange += OnConnectionStateChange;
        _pc.ondatachannel += OnRemoteDataChannel;
        _pc.OnVideoFrameReceived += OnVideoFrameReceived;
        _pc.OnAudioFrameReceived += OnAudioFrameReceived;

        // Negotiated media: H.264 video sendonly + PCMU audio sendrecv.
        var videoFormat = new VideoFormat(VideoCodecsEnum.H264, 102, 90000,
            "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f");
        var videoTrack = new MediaStreamTrack(videoFormat, MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(videoTrack);

        var audioFormat = new AudioFormat(AudioCodecsEnum.PCMU, 0, 8000, 1);
        var audioTrack = new MediaStreamTrack(audioFormat, MediaStreamStatusEnum.SendRecv);
        _pc.addTrack(audioTrack);
    }

    public event Func<LocalDescription, CancellationToken, Task>? LocalDescriptionReady;
    public event Func<IceCandidateInfo, CancellationToken, Task>? IceCandidateGathered;
    public event Func<ReadOnlyMemory<byte>, InputChannelKind, CancellationToken, Task>? DataChannelMessageReceived;
    public event Func<string, CancellationToken, Task>? ConnectionStateChanged;
    public event Func<ReadOnlyMemory<byte>, VideoFrameInfo, CancellationToken, Task>? RemoteVideoFrameReceived;
    public event Func<ReadOnlyMemory<byte>, AudioFrameInfo, CancellationToken, Task>? RemoteAudioSampleReceived;

    public async Task CreateOfferAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await CreateDataChannelsAsync(cancellationToken).ConfigureAwait(false);
        var offer = _pc.createOffer(new RTCOfferOptions { X_ExcludeIceCandidates = true });
        await _pc.setLocalDescription(offer).ConfigureAwait(false);
        await RaiseLocalDescriptionAsync(new LocalDescription
        {
            Sdp = offer.sdp,
            Type = offer.type.ToString().ToLowerInvariant()
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateAnswerAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var answer = _pc.createAnswer(new RTCAnswerOptions { X_ExcludeIceCandidates = true });
        await _pc.setLocalDescription(answer).ConfigureAwait(false);
        await RaiseLocalDescriptionAsync(new LocalDescription
        {
            Sdp = answer.sdp,
            Type = answer.type.ToString().ToLowerInvariant()
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task SetLocalDescriptionAsync(LocalDescription description, CancellationToken cancellationToken = default)
    {
        var type = description.Type == "offer" ? RTCSdpType.offer : RTCSdpType.answer;
        return _pc.setLocalDescription(new RTCSessionDescriptionInit { type = type, sdp = description.Sdp });
    }

    public Task SetRemoteDescriptionAsync(string sdp, string type, CancellationToken cancellationToken = default)
    {
        var sdpType = type == "offer" ? RTCSdpType.offer : RTCSdpType.answer;
        var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = sdpType, sdp = sdp });
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"setRemoteDescription failed: {result}");
        }

        // Flush ICE candidates that arrived before the remote description.
        lock (_channelGate)
        {
            _remoteDescriptionSet = true;
            foreach (var pending in _pendingRemoteCandidates)
            {
                _pc.addIceCandidate(pending);
            }
            _pendingRemoteCandidates.Clear();
        }
        return Task.CompletedTask;
    }

    public Task AddIceCandidateAsync(IceCandidateInfo candidate, CancellationToken cancellationToken = default)
    {
        // Candidate carries the RTCIceCandidateInit JSON produced by toJSON().
        if (!RTCIceCandidateInit.TryParse(candidate.Candidate, out var init))
        {
            return Task.CompletedTask;
        }

        lock (_channelGate)
        {
            if (!_remoteDescriptionSet)
            {
                _pendingRemoteCandidates.Add(init);
                return Task.CompletedTask;
            }
        }
        _pc.addIceCandidate(init);
        return Task.CompletedTask;
    }

    public Task SendDataChannelMessageAsync(ReadOnlyMemory<byte> payload, InputChannelKind channel, bool reliable, CancellationToken cancellationToken = default)
    {
        RTCDataChannel? dc;
        lock (_channelGate)
        {
            dc = _channels.FirstOrDefault(c => c.readyState == RTCDataChannelState.open &&
                (reliable ? !c.label.EndsWith("-ff", StringComparison.Ordinal) : c.label.EndsWith("-ff", StringComparison.Ordinal)));
        }
        dc?.send(payload.ToArray());
        return Task.CompletedTask;
    }

    public Task<StreamStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        // SIPSorcery does not expose a JS-style getStats() surface; report the
        // states we can observe truthfully.
        var iceState = _pc.iceConnectionState.ToString();
        var pcState = _pc.connectionState.ToString();
        return Task.FromResult(new StreamStats
        {
            ConnectionType = iceState.Contains("relay") ? ConnectionType.TurnRelay : ConnectionType.Unknown,
            FallbackReason = $"ice={iceState} pc={pcState}"
        });
    }

    public Task SendVideoAsync(uint rtpDurationUnits, ReadOnlyMemory<byte> annexBAccessUnit, bool isKeyframe, CancellationToken cancellationToken = default)
    {
        var videoStream = _pc.VideoStreamList.FirstOrDefault();
        videoStream?.SendVideo(rtpDurationUnits, annexBAccessUnit.ToArray());
        return Task.CompletedTask;
    }

    public Task SendAudioAsync(uint rtpDurationUnits, ReadOnlyMemory<byte> encodedSample, CancellationToken cancellationToken = default)
    {
        var audioStream = _pc.AudioStreamList.FirstOrDefault();
        audioStream?.SendAudio(rtpDurationUnits, encodedSample.ToArray());
        return Task.CompletedTask;
    }

    private async Task CreateDataChannelsAsync(CancellationToken ct)
    {
        lock (_channelGate)
        {
            if (_channels.Count > 0)
            {
                return;
            }
        }

        var reliable = await _pc.createDataChannel("input", new RTCDataChannelInit
        {
            ordered = true
        }).ConfigureAwait(false);
        var unreliable = await _pc.createDataChannel("input-ff", new RTCDataChannelInit
        {
            ordered = false,
            maxRetransmits = 0
        }).ConfigureAwait(false);

        WireChannel(reliable);
        WireChannel(unreliable);

        lock (_channelGate)
        {
            _channels.Add(reliable);
            _channels.Add(unreliable);
        }
    }

    private void WireChannel(RTCDataChannel dc)
    {
        dc.onmessage += (channel, protocol, data) =>
        {
            var kind = channel.label.EndsWith("-ff", StringComparison.Ordinal)
                ? InputChannelKind.Unreliable
                : InputChannelKind.Reliable;
            DataChannelMessageReceived?.Invoke(data, kind, CancellationToken.None);
        };
        dc.onerror += err => System.Diagnostics.Debug.WriteLine($"[rtc] datachannel error: {err}");
    }

    private void OnRemoteDataChannel(RTCDataChannel dc)
    {
        WireChannel(dc);
        lock (_channelGate)
        {
            _channels.Add(dc);
        }
    }

    private void OnIceCandidate(RTCIceCandidate candidate)
    {
        if (candidate is null || IceCandidateGathered is null)
        {
            return;
        }
        var json = candidate.toJSON();
        IceCandidateGathered.Invoke(new IceCandidateInfo
        {
            Candidate = json,
            SdpMid = candidate.sdpMid,
            SdpMLineIndex = candidate.sdpMLineIndex
        }, CancellationToken.None);
    }

    private void OnConnectionStateChange(RTCPeerConnectionState state)
    {
        var mapped = state switch
        {
            RTCPeerConnectionState.connected => "connected",
            RTCPeerConnectionState.connecting => "connecting",
            RTCPeerConnectionState.disconnected => "disconnected",
            RTCPeerConnectionState.failed => "failed",
            RTCPeerConnectionState.closed => "closed",
            _ => "new"
        };
        ConnectionStateChanged?.Invoke(mapped, CancellationToken.None);
    }

    private void OnVideoFrameReceived(IPEndPoint? endPoint, uint timestamp, byte[] frame, VideoFormat format)
    {
        RemoteVideoFrameReceived?.Invoke(frame, new VideoFrameInfo
        {
            RtpTimestamp = timestamp,
            Width = 0,
            Height = 0
        }, CancellationToken.None);
    }

    private void OnAudioFrameReceived(EncodedAudioFrame frame)
    {
        if (frame.EncodedAudio is null || frame.EncodedAudio.Length == 0)
        {
            return;
        }
        var format = frame.AudioFormat;
        RemoteAudioSampleReceived?.Invoke(frame.EncodedAudio, new AudioFrameInfo
        {
            RtpTimestamp = _audioRtpTimestamp += (uint)Math.Max(1, frame.DurationMilliSeconds * 8),
            SampleRate = format.ClockRate > 0 ? format.ClockRate : 8000,
            Channels = format.ChannelCount > 0 ? format.ChannelCount : 1
        }, CancellationToken.None);
    }

    private async Task RaiseLocalDescriptionAsync(LocalDescription description, CancellationToken ct)
    {
        if (LocalDescriptionReady is not null)
        {
            await LocalDescriptionReady.Invoke(description, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        RTCDataChannel[] channels;
        lock (_channelGate)
        {
            channels = _channels.ToArray();
            _channels.Clear();
        }
        _ = channels; // channels close with the peer connection

        try
        {
            _pc.close();
        }
        catch
        {
        }
        _pc.Dispose();
        await Task.CompletedTask;
    }
}
