using System.Text.Json;
using KeedParsecNative.Contracts.Signaling;

namespace KeedParsecNative.Signaling;

/// <summary>
/// Serializes signaling envelopes. The payload object is embedded as a typed
/// JSON property keyed by message type so receivers can deserialize without
/// a second round-trip.
/// </summary>
public static class SignalingMessageSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>Serializes an envelope (with any already-populated PayloadJson) to UTF-8 JSON.</summary>
    public static byte[] Serialize(SignalingEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, Options);

    public static byte[] Serialize<TPayload>(SignalingEnvelope envelope, TPayload payload)
        where TPayload : class
    {
        var wire = envelope with { PayloadJson = JsonSerializer.Serialize(payload, Options) };
        return Serialize(wire);
    }

    public static SignalingEnvelope Deserialize(byte[] payload)
    {
        var envelope = JsonSerializer.Deserialize<SignalingEnvelope>(payload, Options)
            ?? throw new InvalidDataException("Empty or invalid signaling envelope.");
        return envelope;
    }

    public static TPayload? DeserializePayload<TPayload>(SignalingEnvelope envelope)
        where TPayload : class
    {
        if (string.IsNullOrEmpty(envelope.PayloadJson))
        {
            return null;
        }
        return JsonSerializer.Deserialize<TPayload>(envelope.PayloadJson, Options);
    }
}
