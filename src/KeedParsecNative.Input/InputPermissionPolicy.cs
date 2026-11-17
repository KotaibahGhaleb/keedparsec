using KeedParsecNative.Contracts.Input;
using KeedParsecNative.Core.Abstractions;

namespace KeedParsecNative.Input;

/// <summary>
/// Host-side enforcement of per-peer input permissions. Pure function over a
/// decoded event batch so it is unit-testable without a transport.
/// </summary>
public static class InputPermissionPolicy
{
    /// <summary>Returns the subset of events the peer is allowed to inject.</summary>
    public static IReadOnlyList<InputEvent> Filter(IReadOnlyList<InputEvent> events, PeerInputPermissions permissions)
    {
        var allowed = new List<InputEvent>(events.Count);
        foreach (var e in events)
        {
            var permit = e switch
            {
                KeyboardEvent => permissions.AllowKeyboard,
                MouseMoveEvent or MouseButtonEvent or MouseWheelEvent => permissions.AllowMouse,
                GamepadAxisEvent or GamepadButtonEvent => permissions.AllowGamepad,
                _ => false
            };
            if (permit)
            {
                allowed.Add(e);
            }
        }
        return allowed;
    }
}
