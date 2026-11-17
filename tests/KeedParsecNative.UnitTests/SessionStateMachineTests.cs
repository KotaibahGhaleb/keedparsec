using KeedParsecNative.Contracts.Session;
using KeedParsecNative.Core.Session;
using Xunit;

namespace KeedParsecNative.UnitTests;

public class SessionStateMachineTests
{
    private static SessionStateMachine Create() => new(new SessionInfo
    {
        RoomId = "ABC234",
        Role = SessionRole.Host,
        LocalPeerId = "host-1"
    });

    [Fact]
    public async Task HappyPath_ReachesConnected()
    {
        var sm = Create();
        await sm.BeginConnectAsync();
        await sm.MarkAuthenticatingAsync();
        await sm.MarkNegotiatingAsync("peer-1");
        await sm.MarkConnectedAsync("peer-1");

        Assert.Equal(SessionState.Connected, sm.Info.State);
        Assert.Equal("peer-1", sm.Info.RemotePeerId);
    }

    [Fact]
    public async Task InvalidTransition_Throws()
    {
        var sm = Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => sm.MarkConnectedAsync());
    }

    [Fact]
    public async Task FailurePath_TransitionsToFailedThenDisconnected()
    {
        var sm = Create();
        await sm.BeginConnectAsync();
        await sm.MarkFailedAsync("broker unreachable");
        Assert.Equal(SessionState.Failed, sm.Info.State);
        Assert.Equal("broker unreachable", sm.Info.FailureReason);

        await sm.ResetToDisconnectedAsync();
        Assert.Equal(SessionState.Disconnected, sm.Info.State);
    }

    [Fact]
    public async Task Reconnect_FromConnected()
    {
        var sm = Create();
        await sm.BeginConnectAsync();
        await sm.MarkAuthenticatingAsync();
        await sm.MarkNegotiatingAsync("peer-1");
        await sm.MarkConnectedAsync("peer-1");
        await sm.BeginReconnectAsync();
        Assert.Equal(SessionState.Reconnecting, sm.Info.State);
        await sm.MarkConnectedAsync("peer-1");
        Assert.Equal(SessionState.Connected, sm.Info.State);
    }

    [Fact]
    public async Task StateChanged_EventFires()
    {
        var sm = Create();
        var states = new List<SessionState>();
        sm.StateChanged += (current, _, _) =>
        {
            states.Add(current.State);
            return Task.CompletedTask;
        };

        await sm.BeginConnectAsync();
        await sm.MarkAuthenticatingAsync();

        Assert.Equal(new[] { SessionState.Connecting, SessionState.Authenticating }, states);
    }
}
