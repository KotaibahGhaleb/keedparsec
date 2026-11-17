namespace KeedParsecNative.Contracts.Input;

public enum InputChannelKind
{
    /// <summary>Unordered, partially reliable channel for movement/state.</summary>
    Unreliable = 0,
    /// <summary>Reliable, ordered channel for key/button transitions and messages.</summary>
    Reliable = 1
}

/// <summary>
/// Compact binary input packet. Serialized little-endian; see
/// KeedParsecNative.Input for the codec implementation.
/// </summary>
public enum InputPacketKind : byte
{
    KeyboardKey = 1,
    MouseMoveRelative = 2,
    MouseMoveAbsolute = 3,
    MouseButton = 4,
    MouseWheel = 5,
    GamepadAxis = 6,
    GamepadButton = 7,
    ControllerState = 8
}

/// <summary>Per-peer input permissions granted by the host.</summary>
public sealed record PeerInputPermissions
{
    public bool AllowKeyboard { get; init; } = true;
    public bool AllowMouse { get; init; } = true;
    public bool AllowGamepad { get; init; } = true;

    public static readonly PeerInputPermissions All = new();
    public static readonly PeerInputPermissions None = new()
    {
        AllowKeyboard = false, AllowMouse = false, AllowGamepad = false
    };
}
