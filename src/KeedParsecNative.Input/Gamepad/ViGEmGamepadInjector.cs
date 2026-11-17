using KeedParsecNative.Core.Abstractions;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace KeedParsecNative.Input.Gamepad;

/// <summary>
/// Injects gamepad state into Windows via a ViGEmBus virtual Xbox 360 controller.
/// Requires the ViGEmBus driver; when missing, IsAvailable is false and states
/// are dropped safely. Up to 4 controllers (one per peer slot).
/// </summary>
public sealed class ViGEmGamepadInjector : IGamepadInjector
{
    private readonly object _gate = new();
    private readonly Dictionary<int, (ViGEmClient Client, IXbox360Controller Controller)> _slots = new();
    private bool _driverPresent;
    private bool _driverProbed;
    private bool _disposed;

    public bool IsAvailable
    {
        get
        {
            ProbeDriver();
            return _driverPresent;
        }
    }

    private void ProbeDriver()
    {
        if (_driverProbed)
        {
            return;
        }
        _driverProbed = true;
        try
        {
            using var client = new ViGEmClient();
            _driverPresent = true;
        }
        catch
        {
            _driverPresent = false;
        }
    }

    public Task InjectAsync(GamepadState state, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAvailable)
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            var slot = Math.Clamp(state.Index, 0, 3);
            if (!_slots.TryGetValue(slot, out var pair))
            {
                var client = new ViGEmClient();
                var controller = client.CreateXbox360Controller();
                pair = (client, controller);
                _slots[slot] = pair;
            }

            var c = pair.Controller;
            c.SetAxisValue(Xbox360Axis.LeftThumbX, ToStick(state.Axes, 0));
            c.SetAxisValue(Xbox360Axis.LeftThumbY, ToStick(state.Axes, 1));
            c.SetAxisValue(Xbox360Axis.RightThumbX, ToStick(state.Axes, 2));
            c.SetAxisValue(Xbox360Axis.RightThumbY, ToStick(state.Axes, 3));
            c.SetSliderValue(Xbox360Slider.LeftTrigger, ToTrigger(state.Axes, 4));
            c.SetSliderValue(Xbox360Slider.RightTrigger, ToTrigger(state.Axes, 5));
            c.SetButtonsFull(ToButtonMask(state.Buttons));
        }
        return Task.CompletedTask;
    }

    /// <summary>Disconnects and removes the virtual controller for a peer slot.</summary>
    public void ReleaseSlot(int index)
    {
        lock (_gate)
        {
            if (_slots.Remove(Math.Clamp(index, 0, 3), out var pair))
            {
                try { pair.Controller.Disconnect(); } catch { }
                try { pair.Client.Dispose(); } catch { }
            }
        }
    }

    private static short ToStick(float[] axes, int i)
        => axes.Length > i ? (short)Math.Clamp(axes[i] * 32767f, short.MinValue, short.MaxValue) : (short)0;

    private static byte ToTrigger(float[] axes, int i)
        => axes.Length > i ? (byte)Math.Clamp(axes[i] * 255f, 0, 255) : (byte)0;

    /// <summary>
    /// GamepadState.Buttons uses the XInput wButtons bit order (14 buttons):
    /// Up, Down, Left, Right, Start, Back, L3, R3, LB, RB, A, B, X, Y.
    /// ViGEm SetButtonsFull accepts the same bit layout.
    /// </summary>
    private static ushort ToButtonMask(bool[] buttons)
    {
        ushort mask = 0;
        for (var i = 0; i < Math.Min(buttons.Length, 14); i++)
        {
            if (buttons[i])
            {
                mask |= (ushort)(1 << i);
            }
        }
        return mask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        lock (_gate)
        {
            foreach (var pair in _slots.Values)
            {
                try { pair.Controller.Disconnect(); } catch { }
                try { pair.Client.Dispose(); } catch { }
            }
            _slots.Clear();
        }
        await Task.CompletedTask;
    }
}
