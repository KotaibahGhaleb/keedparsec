using System.Text.Json;
using KeedParsecNative.Contracts.Signaling;
using KeedParsecNative.Core.Abstractions;

namespace KeedParsecNative.Signaling;

public static class SignalingClientExtensions
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Serializes <paramref name="payload"/> into the envelope's PayloadJson
    /// field and publishes it. Works with any ISignalingClient transport.
    /// </summary>
    public static Task PublishPayloadAsync<TPayload>(
        this ISignalingClient client,
        SignalingEnvelope envelope,
        TPayload payload,
        MqttQualityOfService qos = MqttQualityOfService.AtLeastOnce,
        CancellationToken cancellationToken = default)
        where TPayload : class
    {
        var withPayload = envelope with
        {
            PayloadJson = JsonSerializer.Serialize(payload, PayloadOptions)
        };
        return client.PublishAsync(withPayload, qos, cancellationToken);
    }
}
