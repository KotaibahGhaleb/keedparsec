namespace KeedParsecNative.Streaming.Pipelines;

/// <summary>
/// Bounded single-slot pipe with latest-frame-wins semantics. Producers never
/// block; if the consumer is behind, older pending frames are dropped and the
/// newest frame is kept. Designed for video frames where freshness beats
/// completeness.
/// </summary>
public sealed class LatestFrameWinsPipe<T> : IDisposable where T : class
{
    private readonly object _gate = new();
    private T? _pending;
    private long _written;
    private long _dropped;
    private long _read;
    private bool _completed;

    public long Written => Interlocked.Read(ref _written);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Read => Interlocked.Read(ref _read);

    /// <summary>Writes a frame. If one is already pending, it is dropped.</summary>
    public void Write(T frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }
            if (_pending is not null)
            {
                Interlocked.Increment(ref _dropped);
            }
            _pending = frame;
            Interlocked.Increment(ref _written);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Marks the pipe complete; pending frames can still be drained.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Blocks until a frame is available or the pipe completes.</summary>
    public bool TryRead(out T? frame, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            while (_pending is null && !_completed)
            {
                if (cancellationToken.WaitHandle.WaitOne(50))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            frame = _pending;
            _pending = null;
            if (frame is not null)
            {
                Interlocked.Increment(ref _read);
            }
            return frame is not null;
        }
    }

    /// <summary>Non-blocking read of the latest frame, or null.</summary>
    public T? TryReadLatest()
    {
        lock (_gate)
        {
            var frame = _pending;
            _pending = null;
            if (frame is not null)
            {
                Interlocked.Increment(ref _read);
            }
            return frame;
        }
    }

    public void Dispose() => Complete();
}

/// <summary>
/// Small bounded ordered queue used for reliable data (e.g. input events).
/// Blocks producers when full to provide backpressure instead of drops.
/// </summary>
public sealed class BoundedBlockingQueue<T> : IDisposable
{
    private readonly Queue<T> _queue = new();
    private readonly object _gate = new();
    private readonly int _capacity;
    private bool _completed;

    public BoundedBlockingQueue(int capacity = 256)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        _capacity = capacity;
    }

    public int Count
    {
        get { lock (_gate) { return _queue.Count; } }
    }

    public void Enqueue(T item)
    {
        lock (_gate)
        {
            while (_queue.Count >= _capacity && !_completed)
            {
                Monitor.Wait(_gate);
            }
            if (_completed)
            {
                return;
            }
            _queue.Enqueue(item);
            Monitor.PulseAll(_gate);
        }
    }

    public bool TryDequeue(out T? item, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            while (_queue.Count == 0 && !_completed)
            {
                if (cancellationToken.WaitHandle.WaitOne(50))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            if (_queue.Count > 0)
            {
                item = _queue.Dequeue();
                Monitor.PulseAll(_gate);
                return true;
            }
            item = default;
            return false;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    public void Dispose() => Complete();
}
