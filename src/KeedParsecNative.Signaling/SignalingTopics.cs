namespace KeedParsecNative.Signaling;

/// <summary>
/// MQTT topic conventions. All room traffic is published to a single topic;
/// envelopes carry SenderId so peers can filter their own messages.
/// </summary>
public static class SignalingTopics
{
    public const string Root = "keedparsec/v1";

    public static string RoomSignalTopic(string roomId) =>
        $"{Root}/rooms/{Sanitize(roomId)}/signal";

    public static string RoomStateTopic(string roomId) =>
        $"{Root}/rooms/{Sanitize(roomId)}/state";

    public static string PresenceTopic(string roomId) =>
        $"{Root}/rooms/{Sanitize(roomId)}/presence/#";

    private static string Sanitize(string roomId)
    {
        // Room codes are 6-char alphanumeric; strip anything else defensively.
        var chars = roomId.Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant).ToArray();
        return chars.Length == 0 ? "invalid" : new string(chars);
    }
}
