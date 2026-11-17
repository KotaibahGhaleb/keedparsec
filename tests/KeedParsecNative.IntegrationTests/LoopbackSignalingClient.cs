using KeedParsecNative.Contracts.Signaling;
using KeedParsecNative.Core.Abstractions;

namespace KeedParsecNative.IntegrationTests;

/// <summary>
/// In-memory ISignalingClient pair for integration tests. Publish on one
/// endpoint delivers the envelope to the other endpoint's subscribers.
/// </summary>
public sealed class LoopbackSignalingClient : ISignalingClient
{
    private LoopbackSignalingClient? _peer;
    private IReadOnlyList<LoopbackSignalingClient>? _broadcast;
    private bool _connected;

    public event Func<SignalingEnvelope, CancellationToken, Task>? MessageReceived;
    public event Func<ConnectionState, CancellationToken, Task>? ConnectionStateChanged;

    public static (LoopbackSignalingClient A, LoopbackSignalingClient B) CreatePair()
    {
        var a = new LoopbackSignalingClient();
        var b = new LoopbackSignalingClient();
        a._peer = b;
        b._peer = a;
        return (a, b);
    }

    /// <summary>
    /// Star topology: the host endpoint receives from every spoke; the host's
    /// publishes broadcast to all spokes (each spoke filters by TargetPeerId).
    /// </summary>
    public static (LoopbackSignalingClient Host, LoopbackSignalingClient C1, LoopbackSignalingClient C2) CreateStar()
    {
        var host = new LoopbackSignalingClient();
        var c1 = new LoopbackSignalingClient { _peer = host };
        var c2 = new LoopbackSignalingClient { _peer = host };
        host._broadcast = new[] { c1, c2 };
        return (host, c1, c2);
    }

    public Task ConnectAsync(Uri brokerUri, string clientId, CancellationToken cancellationToken = default)
    {
        _connected = true;
        return ConnectionStateChanged?.Invoke(ConnectionState.Connected, cancellationToken) ?? Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = false;
        return ConnectionStateChanged?.Invoke(ConnectionState.Disconnected, cancellationToken) ?? Task.CompletedTask;
    }

    public async Task PublishAsync(SignalingEnvelope envelope, MqttQualityOfService qos = MqttQualityOfService.AtLeastOnce, CancellationToken cancellationToken = default)
    {
        if (!_connected)
        {
            throw new InvalidOperationException("Not connected.");
        }
        if (_peer?.MessageReceived is { } handler)
        {
            await handler.Invoke(envelope, cancellationToken).ConfigureAwait(false);
        }
        if (_broadcast is not null)
        {
            foreach (var spoke in _broadcast)
            {
                if (spoke.MessageReceived is { } spokeHandler)
                {
                    await spokeHandler.Invoke(envelope, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    public Task SubscribeAsync(string topicFilter, MqttQualityOfService qos = MqttQualityOfService.AtLeastOnce, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _connected = false;
        return ValueTask.CompletedTask;
    }
}
