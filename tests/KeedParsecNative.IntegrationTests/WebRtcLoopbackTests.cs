using KeedParsecNative.Contracts.Input;
using KeedParsecNative.Streaming.Transport;

namespace KeedParsecNative.IntegrationTests;

/// <summary>
/// End-to-end WebRTC test: two real peer connections on the same machine,
/// exchanging SDP + trickle ICE directly (no MQTT, no STUN — host candidates
/// only). Validates offer/answer, ICE connectivity, DTLS-SRTP setup, and
/// data channel message delivery.
/// </summary>
public sealed class WebRtcLoopbackTests
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private sealed class PeerHarness : IAsyncDisposable
    {
        public SipsorceryMediaTransport Transport { get; }
        public readonly TaskCompletionSource Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> States = new();

        public PeerHarness(SipsorceryMediaTransport transport)
        {
            Transport = transport;
            Transport.ConnectionStateChanged += OnStateAsync;
        }

        private Task OnStateAsync(string state, CancellationToken ct)
        {
            lock (States)
            {
                States.Add(state);
            }
            if (state == "connected")
            {
                Connected.TrySetResult();
            }
            else if (state == "failed" || state == "closed")
            {
                Connected.TrySetException(new InvalidOperationException($"Peer connection entered state '{state}'."));
            }
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await Transport.DisposeAsync();
        }
    }

    private static PeerHarness CreatePeer()
    {
        // No STUN/TURN: loopback host candidates only, fully offline.
        var transport = new SipsorceryMediaTransport(
            turnServers: null,
            stunServers: Array.Empty<string>());
        return new PeerHarness(transport);
    }

    private static void WireSignaling(PeerHarness offerer, PeerHarness answerer)
    {
        offerer.Transport.LocalDescriptionReady += async (desc, ct) =>
        {
            await answerer.Transport.SetRemoteDescriptionAsync(desc.Sdp, desc.Type, ct);
            await answerer.Transport.CreateAnswerAsync(ct);
        };
        answerer.Transport.LocalDescriptionReady += async (desc, ct) =>
        {
            await offerer.Transport.SetRemoteDescriptionAsync(desc.Sdp, desc.Type, ct);
        };
        offerer.Transport.IceCandidateGathered += async (c, ct) =>
        {
            await answerer.Transport.AddIceCandidateAsync(c, ct);
        };
        answerer.Transport.IceCandidateGathered += async (c, ct) =>
        {
            await offerer.Transport.AddIceCandidateAsync(c, ct);
        };
    }

    private static string StatesOf(PeerHarness h)
    {
        lock (h.States)
        {
            return string.Join(",", h.States);
        }
    }

    [Fact]
    public async Task OfferAnswerAndIce_TwoPeersOnLoopback_ReachConnected()
    {
        await using var offerer = CreatePeer();
        await using var answerer = CreatePeer();
        WireSignaling(offerer, answerer);

        await offerer.Transport.CreateOfferAsync();

        var timeout = Task.Delay(ConnectTimeout);
        var winner = await Task.WhenAny(
            Task.WhenAll(offerer.Connected.Task, answerer.Connected.Task),
            timeout);

        Assert.True(winner != timeout && winner.IsCompletedSuccessfully,
            $"Peers did not connect within {ConnectTimeout}. " +
            $"Offerer states: [{StatesOf(offerer)}] " +
            $"Answerer states: [{StatesOf(answerer)}]");
    }

    [Fact]
    public async Task ConnectedPeers_ExchangeDataChannelMessage()
    {
        await using var offerer = CreatePeer();
        await using var answerer = CreatePeer();
        WireSignaling(offerer, answerer);

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        answerer.Transport.DataChannelMessageReceived += (data, kind, ct) =>
        {
            received.TrySetResult(data.ToArray());
            return Task.CompletedTask;
        };

        await offerer.Transport.CreateOfferAsync();
        await Task.WhenAll(offerer.Connected.Task, answerer.Connected.Task)
            .WaitAsync(ConnectTimeout);

        var payload = new byte[] { 0x01, 0x02, 0x03, 0xAA, 0xBB };
        // Data channel may still be opening; retry briefly.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            await offerer.Transport.SendDataChannelMessageAsync(payload, InputChannelKind.Reliable, reliable: true);
            var done = await Task.WhenAny(received.Task, Task.Delay(500));
            if (done == received.Task)
            {
                break;
            }
            Assert.True(DateTime.UtcNow < deadline, "Data channel message was not delivered.");
        }

        Assert.Equal(payload, await received.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }
}
