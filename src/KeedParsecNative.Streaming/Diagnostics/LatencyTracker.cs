using System.Diagnostics;

namespace KeedParsecNative.Streaming.Diagnostics;

/// <summary>
/// Tracks per-frame pipeline latency. Frames are correlated FIFO by capture
/// timestamp; encode/decode stage durations are measured with
/// <see cref="Stopwatch"/> and end-to-end latency uses UTC ticks so it works
/// across machines (host and client clocks must be roughly synchronized for
/// absolute values; relative stage timings are always accurate).
/// </summary>
public sealed class FrameLatencyTracker
{
    private readonly object _gate = new();
    private readonly Queue<long> _pendingCaptureTicks = new();
    private readonly int _maxPending;

    private double _latencySumMs;
    private int _latencyCount;
    private double _lastLatencyMs;
    private double _lastEncodeMs;
    private double _lastDecodeMs;

    public FrameLatencyTracker(int maxPending = 240)
    {
        _maxPending = maxPending;
    }

    /// <summary>Call when a frame is captured; returns a token for correlation.</summary>
    public long OnCaptured()
    {
        var ticks = DateTime.UtcNow.Ticks;
        lock (_gate)
        {
            _pendingCaptureTicks.Enqueue(ticks);
            while (_pendingCaptureTicks.Count > _maxPending)
            {
                _pendingCaptureTicks.Dequeue();
            }
        }
        return ticks;
    }

    public void OnEncoded(long captureToken, double encodeDurationMs)
    {
        lock (_gate)
        {
            _lastEncodeMs = encodeDurationMs;
        }
    }

    public void OnDecoded(double decodeDurationMs)
    {
        lock (_gate)
        {
            _lastDecodeMs = decodeDurationMs;
        }
    }

    /// <summary>
    /// Call when a frame reaches the display. Returns end-to-end latency in ms,
    /// or null if no pending capture exists.
    /// </summary>
    public double? OnDisplayed()
    {
        lock (_gate)
        {
            if (_pendingCaptureTicks.Count == 0)
            {
                return null;
            }
            var captureTicks = _pendingCaptureTicks.Dequeue();
            var latency = (DateTime.UtcNow.Ticks - captureTicks) / (double)TimeSpan.TicksPerMillisecond;
            _latencySumMs += latency;
            _latencyCount++;
            _lastLatencyMs = latency;
            return latency;
        }
    }

    public double AverageLatencyMs
    {
        get { lock (_gate) { return _latencyCount == 0 ? 0 : _latencySumMs / _latencyCount; } }
    }

    public double LastLatencyMs
    {
        get { lock (_gate) { return _lastLatencyMs; } }
    }

    public double LastEncodeMs
    {
        get { lock (_gate) { return _lastEncodeMs; } }
    }

    public double LastDecodeMs
    {
        get { lock (_gate) { return _lastDecodeMs; } }
    }
}

/// <summary>High-resolution stopwatch helper for measuring stage durations.</summary>
public static class StageTimer
{
    public static long Start() => Stopwatch.GetTimestamp();

    public static double ElapsedMillisecondsSince(long startTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
