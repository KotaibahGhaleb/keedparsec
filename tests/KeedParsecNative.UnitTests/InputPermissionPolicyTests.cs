using KeedParsecNative.Contracts.Input;
using KeedParsecNative.Core.Abstractions;
using KeedParsecNative.Input;
using Xunit;

namespace KeedParsecNative.UnitTests;

public class InputPermissionPolicyTests
{
    private static readonly InputEvent[] AllEvents =
    {
        new KeyboardEvent(0x41, true),
        new MouseMoveEvent(10, -5),
        new MouseButtonEvent(0, true),
        new MouseWheelEvent(120),
        new GamepadAxisEvent(0, 1000),
        new GamepadButtonEvent(10, true)
    };

    [Fact]
    public void AllPermissions_PassesEverything()
    {
        var result = InputPermissionPolicy.Filter(AllEvents, PeerInputPermissions.All);
        Assert.Equal(AllEvents.Length, result.Count);
    }

    [Fact]
    public void KeyboardOnly_DropsMouseAndGamepad()
    {
        var perms = new PeerInputPermissions { AllowKeyboard = true, AllowMouse = false, AllowGamepad = false };
        var result = InputPermissionPolicy.Filter(AllEvents, perms);
        Assert.Single(result);
        Assert.IsType<KeyboardEvent>(result[0]);
    }

    [Fact]
    public void MouseOnly_DropsKeyboardAndGamepad()
    {
        var perms = new PeerInputPermissions { AllowKeyboard = false, AllowMouse = true, AllowGamepad = false };
        var result = InputPermissionPolicy.Filter(AllEvents, perms);
        Assert.Equal(3, result.Count);
        Assert.All(result, e => Assert.IsNotType<KeyboardEvent>(e));
        Assert.All(result, e => Assert.IsNotType<GamepadAxisEvent>(e));
        Assert.All(result, e => Assert.IsNotType<GamepadButtonEvent>(e));
    }

    [Fact]
    public void GamepadOnly_DropsKeyboardAndMouse()
    {
        var perms = new PeerInputPermissions { AllowKeyboard = false, AllowMouse = false, AllowGamepad = true };
        var result = InputPermissionPolicy.Filter(AllEvents, perms);
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.True(e is GamepadAxisEvent or GamepadButtonEvent));
    }

    [Fact]
    public void None_DropsEverything()
    {
        Assert.Empty(InputPermissionPolicy.Filter(AllEvents, PeerInputPermissions.None));
    }
}
