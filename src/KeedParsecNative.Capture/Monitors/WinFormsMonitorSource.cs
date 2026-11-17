using System.Drawing;
using System.Windows.Forms;
using KeedParsecNative.Contracts.Streaming;
using KeedParsecNative.Core.Abstractions;

namespace KeedParsecNative.Capture.Monitors;

/// <summary>Enumerates displays via WinForms Screen API.</summary>
public sealed class WinFormsMonitorSource
{
    public IReadOnlyList<MonitorInfo> Enumerate()
    {
        var result = new List<MonitorInfo>();
        var screens = Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            var bounds = s.Bounds;
            result.Add(new MonitorInfo
            {
                Index = i,
                DeviceName = s.DeviceName,
                Width = bounds.Width,
                Height = bounds.Height,
                X = bounds.X,
                Y = bounds.Y,
                IsPrimary = s.Primary
            });
        }
        return result;
    }

    public static VideoStreamConfig ConfigForMonitor(MonitorInfo monitor, VideoStreamConfig template)
    {
        // Match capture resolution to the monitor's current mode.
        return template with
        {
            Width = monitor.Width,
            Height = monitor.Height
        };
    }
}
