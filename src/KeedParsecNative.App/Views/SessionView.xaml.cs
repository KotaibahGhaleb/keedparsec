using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KeedParsecNative.App.ViewModels;
using KeedParsecNative.Contracts.Session;
using KeedParsecNative.Core.Abstractions;

namespace KeedParsecNative.App.Views;

/// <summary>
/// Live session surface: renders decoded remote frames into a WriteableBitmap and
/// captures keyboard/mouse input, forwarding it to the host over the reliable /
/// unreliable data channels.
/// </summary>
public partial class SessionView : UserControl
{
    private WriteableBitmap? _bitmap;

    public SessionView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SessionViewModel vm)
        {
            vm.DecodedFrameReceived += OnDecodedFrame;
            UpdateInputGate();
        }
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SessionViewModel vm2)
            {
                vm2.DecodedFrameReceived += OnDecodedFrame;
            }
        };
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SessionViewModel vm)
        {
            vm.DecodedFrameReceived -= OnDecodedFrame;
        }
    }

    private bool InputEnabled =>
        DataContext is SessionViewModel vm &&
        vm.Role == SessionRole.Client &&
        vm.SessionState == SessionState.Connected;

    private void UpdateInputGate()
    {
        VideoSurface.IsEnabled = true; // keep focusable; gate inside handlers
    }

    private async Task OnDecodedFrame(VideoFrame frame, CancellationToken ct)
    {
        var data = frame.ManagedData;
        if (data is null || frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }
        await Dispatcher.InvokeAsync(() =>
        {
            var stride = frame.Stride > 0 ? frame.Stride : frame.Width * 4;
            if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
            {
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                VideoImage.Source = _bitmap;
                PlaceholderText.Visibility = Visibility.Collapsed;
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height),
                data, stride, 0);
        });
    }

    private Task SendInput(InputEvent ev, bool reliable)
    {
        if (DataContext is SessionViewModel vm && InputEnabled)
        {
            return vm.SendClientInputAsync(ev, reliable);
        }
        return Task.CompletedTask;
    }

    private void Surface_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Capture so drag keeps flowing even when the cursor leaves the surface.
        VideoSurface.CaptureMouse();
    }

    private Point? _lastMousePos;

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(VideoSurface);
        if (_lastMousePos is { } last)
        {
            var dx = (int)Math.Clamp(pos.X - last.X, short.MinValue, short.MaxValue);
            var dy = (int)Math.Clamp(pos.Y - last.Y, short.MinValue, short.MaxValue);
            if (dx != 0 || dy != 0)
            {
                _ = SendInput(new MouseMoveEvent(dx, dy), reliable: false);
            }
        }
        _lastMousePos = pos;
    }

    private void Surface_MouseButton(object sender, MouseButtonEventArgs e)
    {
        var down = e.LeftButton == MouseButtonState.Pressed;
        if (e.ChangedButton == MouseButton.Left)
        {
            _ = SendInput(new MouseButtonEvent(0, down), reliable: true);
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            _ = SendInput(new MouseButtonEvent(1, down), reliable: true);
        }
        else if (e.ChangedButton == MouseButton.Middle)
        {
            _ = SendInput(new MouseButtonEvent(2, down), reliable: true);
        }
    }

    private void Surface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _ = SendInput(new MouseWheelEvent(e.Delta), reliable: false);
    }

    private void Surface_Key(object sender, KeyEventArgs e)
    {
        var vk = KeyInterop.VirtualKeyFromKey(e.Key);
        if (vk == 0)
        {
            return;
        }
        _ = SendInput(new KeyboardEvent((ushort)vk, IsKeyDown(e)), reliable: true);
        e.Handled = true;
    }

    private static bool IsKeyDown(KeyEventArgs e) => e.RoutedEvent == Keyboard.KeyDownEvent;
}
