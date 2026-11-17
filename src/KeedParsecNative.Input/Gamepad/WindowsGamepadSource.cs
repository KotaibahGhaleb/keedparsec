using System.Runtime.InteropServices;
using KeedParsecNative.Core.Abstractions;

namespace KeedParsecNative.Input.Gamepad;

/// <summary>
/// Polls XInput gamepads (xinput1_4.dll) at a fixed rate and emits
/// <see cref="GamepadState"/> snapshots when the state changes.
/// </summary>
public sealed class WindowsGamepadSource : IGamepadSource
{
    private const int MaxControllers = 4;
    private const int AxisCount = 6;  // LX, LY, RX, RY, L2, R2
    private const int ButtonCount = 14; // standard mapping buttons

    private readonly int _pollRateHz;
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _pollTask;
    private bool _disposed;

    public WindowsGamepadSource(int pollRateHz = 60)
    {
        _pollRateHz = Math.Clamp(pollRateHz, 10, 240);
    }

    public event Func<GamepadState, CancellationToken, Task>? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pollTask ??= Task.Run(() => PollLoopAsync(_disposeCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_pollTask is null)
        {
            return;
        }
        _disposeCts.Cancel();
        try
        {
            await _pollTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _pollTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _disposeCts.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(1000.0 / _pollRateHz);
        var previous = new Dictionary<int, GamepadState>();

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                for (var index = 0; index < MaxControllers; index++)
                {
                    var state = ReadState(index);
                    if (state is null)
                    {
                        continue;
                    }

                    if (StateChanged is not null &&
                        (!previous.TryGetValue(index, out var last) || !StateEquals(last, state)))
                    {
                        await StateChanged.Invoke(state, cancellationToken).ConfigureAwait(false);
                    }
                    previous[index] = state;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static GamepadState? ReadState(int index)
    {
        var result = XInputGetState((uint)index, out var raw);
        if (result != 0)
        {
            return null; // controller not connected or slot empty
        }

        var axes = new[]
        {
            Normalize(raw.Gamepad.sThumbLX),
            Normalize(raw.Gamepad.sThumbLY),
            Normalize(raw.Gamepad.sThumbRX),
            Normalize(raw.Gamepad.sThumbRY),
            raw.Gamepad.bLeftTrigger / 255f,
            raw.Gamepad.bRightTrigger / 255f
        };

        var buttons = new bool[ButtonCount];
        var w = raw.Gamepad.wButtons;
        for (var i = 0; i < ButtonCount; i++)
        {
            buttons[i] = (w & (1 << i)) != 0;
        }

        return new GamepadState { Index = index, Axes = axes, Buttons = buttons };
    }

    private static float Normalize(short value) => Math.Clamp(value / 32767f, -1f, 1f);

    private static bool StateEquals(GamepadState a, GamepadState b)
    {
        if (a.Axes.Length != b.Axes.Length || a.Buttons.Length != b.Buttons.Length)
        {
            return false;
        }
        for (var i = 0; i < a.Axes.Length; i++)
        {
            if (a.Axes[i] != b.Axes[i])
            {
                return false;
            }
        }
        for (var i = 0; i < a.Buttons.Length; i++)
        {
            if (a.Buttons[i] != b.Buttons[i])
            {
                return false;
            }
        }
        return true;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }
}
