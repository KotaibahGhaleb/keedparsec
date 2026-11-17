using KeedParsecNative.Contracts.Session;
using KeedParsecNative.Core.Abstractions;
using KeedParsecNative.Core.Session;
using KeedParsecNative.Signaling;
using KeedParsecNative.Streaming.Transport;
using Xunit;

namespace KeedParsecNative.IntegrationTests;

/// <summary>
/// End-to-end session tests over the in-memory loopback signaling transport.
/// Coordinators are given a real <see cref="SipsorceryMediaTransport"/>, so the
/// tests exercise the complete path: join auth, host approval, SDP offer /
/// answer exchange, trickle ICE, and an actual WebRTC peer connection on
/// loopback host candidates — no MQTT broker or STUN required.
/// </summary>
public class RoomSessionCoordinatorTests
{
    private static readonly Uri FakeBroker = new("mqtt://localhost:1883");

    private static IMediaTransport CreateTransport() =>
        new SipsorceryMediaTransport(turnServers: null, stunServers: Array.Empty<string>());

    private static JoinTokenService CreateTokens() =>
        new(System.Text.Encoding.UTF8.GetBytes("integration-test-secret-key-32bytes!!"));

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task HostAndClient_ApprovalFlow_ReachesConnected()
    {
        var (hostTransport, clientTransport) = LoopbackSignalingClient.CreatePair();
        var host = new RoomSessionCoordinator(hostTransport, CreateTokens(), requireHostApproval: true,
            mediaTransportFactory: CreateTransport);
        var client = new RoomSessionCoordinator(clientTransport, CreateTokens(), requireHostApproval: true,
            mediaTransportFactory: CreateTransport);

        string? approvalPeerId = null;
        string? approvalName = null;
        host.JoinApprovalRequested += (peerId, name, _) =>
        {
            approvalPeerId = peerId;
            approvalName = name;
            return Task.CompletedTask;
        };

        await host.StartHostingAsync(FakeBroker, "test");
        var roomCode = host.RoomCode!;

        var joinTask = client.JoinAsync(FakeBroker, "test", roomCode, "Test Client");

        Assert.True(await WaitUntilAsync(() => approvalPeerId is not null), "Host never saw join request.");
        Assert.Equal("Test Client", approvalName);

        await host.ApproveJoinAsync();
        await joinTask;

        Assert.True(await WaitUntilAsync(() =>
            client.Info.State == SessionState.Connected &&
            host.Info.State == SessionState.Connected, timeoutMs: 30000));

        Assert.Single(host.Peers);
        Assert.Equal(SessionRole.Client, host.Peers[0].Role);

        await client.LeaveAsync();
        await host.LeaveAsync();
        await host.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task HostAndClient_DenyFlow_ClientFails()
    {
        var (hostTransport, clientTransport) = LoopbackSignalingClient.CreatePair();
        var host = new RoomSessionCoordinator(hostTransport, CreateTokens(), requireHostApproval: true);
        var client = new RoomSessionCoordinator(clientTransport, CreateTokens(), requireHostApproval: true);

        await host.StartHostingAsync(FakeBroker, "test");

        var joinTask = client.JoinAsync(FakeBroker, "test", host.RoomCode!, "Denied Client");
        Assert.True(await WaitUntilAsync(() => host.PendingJoinPeerId is not null));

        await host.DenyJoinAsync();
        await joinTask;

        Assert.True(await WaitUntilAsync(() => client.Info.State == SessionState.Disconnected));
        Assert.Empty(host.Peers);

        await host.LeaveAsync();
        await host.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task HostAndClient_ValidToken_SkipsApproval()
    {
        var (hostTransport, clientTransport) = LoopbackSignalingClient.CreatePair();
        var host = new RoomSessionCoordinator(hostTransport, CreateTokens(), requireHostApproval: true,
            mediaTransportFactory: CreateTransport);
        var client = new RoomSessionCoordinator(clientTransport, CreateTokens(), requireHostApproval: true,
            mediaTransportFactory: CreateTransport);

        await host.StartHostingAsync(FakeBroker, "test");
        var token = host.CreateJoinToken(null, TimeSpan.FromMinutes(1));

        await client.JoinAsync(FakeBroker, "test", host.RoomCode!, "Token Client", joinToken: token);

        Assert.True(await WaitUntilAsync(() =>
            client.Info.State == SessionState.Connected && host.Info.State == SessionState.Connected,
            timeoutMs: 30000));
        Assert.Null(host.PendingJoinPeerId);
        Assert.Single(host.Peers);

        await client.LeaveAsync();
        await host.LeaveAsync();
        await host.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task InvalidRoomCode_JoinThrows()
    {
        var (hostTransport, _) = LoopbackSignalingClient.CreatePair();
        var host = new RoomSessionCoordinator(hostTransport, CreateTokens());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            host.JoinAsync(FakeBroker, "test", "BAD!", "x"));
        await host.DisposeAsync();
    }

    [Fact]
    public async Task DoubleStart_Throws()
    {
        var (hostTransport, _) = LoopbackSignalingClient.CreatePair();
        var host = new RoomSessionCoordinator(hostTransport, CreateTokens());
        await host.StartHostingAsync(FakeBroker, "test");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.StartHostingAsync(FakeBroker, "test"));
        await host.LeaveAsync();
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Host_TwoClients_MultiPeerTransports_AndCleanup()
    {
        var (hostTransport, client1Transport, client2Transport) = LoopbackSignalingClient.CreateStar();
        var host = new RoomSessionCoordinator(hostTransport, CreateTokens(), requireHostApproval: true,
            mediaTransportFactory: CreateTransport);
        var client1 = new RoomSessionCoordinator(client1Transport, CreateTokens(), requireHostApproval: true,
            mediaTransportFactory: CreateTransport);
        var client2 = new RoomSessionCoordinator(client2Transport, CreateTokens(), requireHostApproval: true,
            mediaTransportFactory: CreateTransport);

        await host.StartHostingAsync(FakeBroker, "test");

        // Client 1 joins with a valid token (skips approval queue).
        var token1 = host.CreateJoinToken(null, TimeSpan.FromMinutes(1));
        var join1 = client1.JoinAsync(FakeBroker, "test", host.RoomCode!, "Client One", joinToken: token1);
        Assert.True(await WaitUntilAsync(() => client1.Info.State == SessionState.Connected, 30000));
        await join1;

        // Client 2 joins the same room.
        var token2 = host.CreateJoinToken(null, TimeSpan.FromMinutes(1));
        var join2 = client2.JoinAsync(FakeBroker, "test", host.RoomCode!, "Client Two", joinToken: token2);
        Assert.True(await WaitUntilAsync(() => client2.Info.State == SessionState.Connected, 30000));
        await join2;

        Assert.True(await WaitUntilAsync(() => host.MediaTransports.Count == 2, 10000),
            $"Expected 2 peer transports, got {host.MediaTransports.Count}");
        Assert.Equal(2, host.Peers.Count);

        // Client 1 leaves: its transport must be disposed without breaking client 2.
        await client1.LeaveAsync();
        Assert.True(await WaitUntilAsync(() => host.MediaTransports.Count == 1, 10000),
            "Peer transport was not cleaned up on leave.");
        Assert.Single(host.Peers);
        Assert.Equal(SessionState.Connected, client2.Info.State);

        await client2.LeaveAsync();
        await host.LeaveAsync();
        await host.DisposeAsync();
        await client1.DisposeAsync();
        await client2.DisposeAsync();
    }
}
