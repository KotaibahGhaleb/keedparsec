using System.Runtime.InteropServices;
using KeedParsecNative.Core.Abstractions;
using KeedParsecNative.Input;

namespace KeedParsecNative.Input.Injection;

/// <summary>
/// Injects input events on Windows via SendInput. Relative mouse motion uses
/// MOUSEEVENTF_MOVE; keyboard uses KEYEVENTF_SCANCODE with virtual-key fallback.
/// </summary>
public sealed class Win32InputInjector : IInputInjector
{
    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;

    private const uint KeyEventFKeyUp = 0x0002;
    private const uint KeyEventFExtendedKey = 0x0001;

    private const uint MouseEventFMove = 0x0001;
    private const uint MouseEventFLeftDown = 0x0002;
    private const uint MouseEventFLeftUp = 0x0004;
    private const uint MouseEventFRightDown = 0x0008;
    private const uint MouseEventFRightUp = 0x0010;
    private const uint MouseEventFMiddleDown = 0x0020;
    private const uint MouseEventFMiddleUp = 0x0040;
    private const uint MouseEventFXDown = 0x0080;
    private const uint MouseEventFXUp = 0x0100;
    private const uint MouseEventFWheel = 0x0800;

    private readonly List<InputEvent> _pending = new();
    private readonly object _gate = new();
    private bool _disposed;

    public async Task InjectAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var events = InputPacketCodec.DecodeBatch(packet);
        if (events.Count == 0)
        {
            return;
        }

        var inputs = new List<INPUT>(events.Count * 2);
        foreach (var inputEvent in events)
        {
            AppendInputs(inputs, inputEvent);
        }

        if (inputs.Count > 0)
        {
            var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
            if (sent != inputs.Count)
            {
                // Partial injection: surface as an error rather than dropping silently.
                throw new InvalidOperationException(
                    $"SendInput injected {sent}/{inputs.Count} events (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _pending.Clear();
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private static void AppendInputs(List<INPUT> inputs, InputEvent inputEvent)
    {
        switch (inputEvent)
        {
            case KeyboardEvent key:
                inputs.Add(CreateKeyboardInput(key.VirtualKey, key.KeyDown));
                break;

            case MouseMoveEvent move:
                inputs.Add(new INPUT
                {
                    type = InputMouse,
                    U = new InputUnion
                    {
                        mi = new MOUSEINPUT
                        {
                            dx = move.DeltaX,
                            dy = move.DeltaY,
                            mouseData = 0,
                            dwFlags = MouseEventFMove,
                            time = 0,
                            dwExtraInfo = UIntPtr.Zero
                        }
                    }
                });
                break;

            case MouseButtonEvent button:
                var (downFlag, upFlag, xButtonData) = MapMouseButton(button.Button);
                if (downFlag == 0)
                {
                    break;
                }
                inputs.Add(new INPUT
                {
                    type = InputMouse,
                    U = new InputUnion
                    {
                        mi = new MOUSEINPUT
                        {
                            dx = 0,
                            dy = 0,
                            mouseData = xButtonData,
                            dwFlags = button.Down ? downFlag : upFlag,
                            time = 0,
                            dwExtraInfo = UIntPtr.Zero
                        }
                    }
                });
                break;

            case MouseWheelEvent wheel:
                inputs.Add(new INPUT
                {
                    type = InputMouse,
                    U = new InputUnion
                    {
                        mi = new MOUSEINPUT
                        {
                            dx = 0,
                            dy = 0,
                            mouseData = (uint)wheel.Delta,
                            dwFlags = MouseEventFWheel,
                            time = 0,
                            dwExtraInfo = UIntPtr.Zero
                        }
                    }
                });
                break;
        }
    }

    private static (uint Down, uint Up, uint XButtonData) MapMouseButton(byte button) => button switch
    {
        0 => (MouseEventFLeftDown, MouseEventFLeftUp, 0u),
        1 => (MouseEventFRightDown, MouseEventFRightUp, 0u),
        2 => (MouseEventFMiddleDown, MouseEventFMiddleUp, 0u),
        3 => (MouseEventFXDown, MouseEventFXUp, 0x0001), // XBUTTON1
        4 => (MouseEventFXDown, MouseEventFXUp, 0x0002), // XBUTTON2
        _ => (0u, 0u, 0u)
    };

    private static INPUT CreateKeyboardInput(ushort virtualKey, bool keyDown)
    {
        var flags = 0u;
        if (!keyDown)
        {
            flags |= KeyEventFKeyUp;
        }
        // Extended keys (arrows, Insert/Delete/Home/End/PgUp/PgDn, right modifiers,
        // numpad enter/divide) require the extended flag for correct scancode mapping.
        if (IsExtendedKey(virtualKey))
        {
            flags |= KeyEventFExtendedKey;
        }

        return new INPUT
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private static INPUT CreateMouseInput(uint flags)
    {
        return new INPUT
        {
            type = InputMouse,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private static bool IsExtendedKey(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or // PageUp/PageDown/End/Home
        0x2D or 0x2E or                 // Insert/Delete
        0x25 or 0x27 or                 // Left/Right arrow
        0x26 or 0x28 or                 // Up/Down arrow
        0x6F or 0x90 or                 // Numpad divide / NumLock
        0x5B or 0x5C or                 // LWin/RWin
        0xA3 or 0xA5 or                 // RControl/RMenu
        0x2C or 0x6A or 0x6B or 0x6D or // Numpad enter/separator/subtract/add
        0xBD or 0xBB or 0xDB or 0xDC or 0xDD or 0xBE or 0xBF or 0xC0 or // OEM punctuation
        0xC1;                           // OEM various

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
