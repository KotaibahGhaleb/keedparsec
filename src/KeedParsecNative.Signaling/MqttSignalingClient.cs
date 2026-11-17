using System.Text;
using KeedParsecNative.Contracts.Signaling;
using KeedParsecNative.Core.Abstractions;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace KeedParsecNative.Signaling;

/// <summary>
/// MQTT-based signaling client. Each room maps to a single MQTT topic;
/// envelopes are JSON and carry the sender id for filtering.
/// </summary>
public sealed class MqttSignalingClient : ISignalingClient
{
    private readonly MqttFactory _factory = new();
    private readonly IMqttClient _client;
    private readonly object _subscriptionLock = new();
    private readonly HashSet<string> _subscriptions = new(StringComparer.Ordinal);

    public MqttSignalingClient()
    {
        _client = _factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnApplicationMessageReceivedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    public event Func<SignalingEnvelope, CancellationToken, Task>? MessageReceived;
    public event Func<ConnectionState, CancellationToken, Task>? ConnectionStateChanged;

    public async Task ConnectAsync(Uri brokerUri, string clientId, CancellationToken cancellationToken = default)
    {
        var useTls = brokerUri.Scheme.Equals("mqtts", StringComparison.OrdinalIgnoreCase);
        var port = brokerUri.IsDefaultPort ? (useTls ? 8883 : 1883) : brokerUri.Port;

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(brokerUri.Host, port)
            .WithClientId(clientId)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithProtocolVersion(MqttProtocolVersion.V500);

        if (useTls)
        {
            builder = builder.WithTlsOptions(new MqttClientTlsOptions { UseTls = true });
        }

        if (!string.IsNullOrEmpty(brokerUri.UserInfo))
        {
            var parts = brokerUri.UserInfo.Split(':', 2);
            builder = builder.WithCredentials(parts[0], parts.Length > 1 ? parts[1] : string.Empty);
        }

        await RaiseConnectionStateAsync(ConnectionState.Connecting, cancellationToken).ConfigureAwait(false);

        try
        {
            await _client.ConnectAsync(builder.Build(), cancellationToken).ConfigureAwait(false);
            await RaiseConnectionStateAsync(ConnectionState.Connected, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RaiseConnectionStateAsync(ConnectionState.Failed, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_client.IsConnected)
        {
            await _client.DisconnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        await RaiseConnectionStateAsync(ConnectionState.Disconnected, cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishAsync(
        SignalingEnvelope envelope,
        MqttQualityOfService qos = MqttQualityOfService.AtLeastOnce,
        CancellationToken cancellationToken = default)
    {
        // PublishAsync on the envelope without a payload overload: callers that
        // need payloads should use the typed helper below. Raw envelope publish
        // is kept for control messages (Ping/Pong/Leave).
        var bytes = SignalingMessageSerializer.Serialize(envelope);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(SignalingTopics.RoomSignalTopic(envelope.RoomId))
            .WithPayload(bytes)
            .WithQualityOfServiceLevel(MapQos(qos))
            .Build();

        await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task SubscribeAsync(
        string topicFilter,
        MqttQualityOfService qos = MqttQualityOfService.AtLeastOnce,
        CancellationToken cancellationToken = default)
    {
        var already = false;
        lock (_subscriptionLock)
        {
            already = !_subscriptions.Add(topicFilter);
        }
        if (already)
        {
            return;
        }

        var options = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(topicFilter, MapQos(qos))
            .Build();

        await _client.SubscribeAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _client.Dispose();
        }
    }

    private async Task OnApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        if (MessageReceived is null)
        {
            return;
        }

        try
        {
        var payload = e.ApplicationMessage.PayloadSegment.ToArray();
        var envelope = SignalingMessageSerializer.Deserialize(payload);
        await MessageReceived.Invoke(envelope, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Signaling deserialize failed: {ex.Message}");
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        var state = e.ClientWasConnected ? ConnectionState.Disconnected : ConnectionState.Failed;
        return RaiseConnectionStateAsync(state, CancellationToken.None);
    }

    private Task RaiseConnectionStateAsync(ConnectionState state, CancellationToken cancellationToken)
    {
        if (ConnectionStateChanged is null)
        {
            return Task.CompletedTask;
        }
        return ConnectionStateChanged.Invoke(state, cancellationToken);
    }

    private static MqttQualityOfServiceLevel MapQos(MqttQualityOfService qos) => qos switch
    {
        MqttQualityOfService.AtMostOnce => MqttQualityOfServiceLevel.AtMostOnce,
        MqttQualityOfService.ExactlyOnce => MqttQualityOfServiceLevel.ExactlyOnce,
        _ => MqttQualityOfServiceLevel.AtLeastOnce
    };
}
