namespace Olivia.Core.Charts;

/// <summary>Bounded synchronous surface lifecycle. The UI owns one 40 ms timer and captures
/// Generation in its callback. Unlike the financial display-only pause, surface pause stops
/// generation itself. No callbacks, threads or pending frame queue are retained by this class.</summary>
public sealed class RealtimeSurfaceSession : IDisposable
{
    public const int MinimumSize = 15;
    public const int MaximumSize = 100;
    public const int DefaultSize = 50;
    public static TimeSpan TickInterval { get; } = TimeSpan.FromMilliseconds(40);
    private const string SourceId = "synthetic-realtime-surface";
    private static long _nextGeneration;
    private readonly object _gate = new();
    private SurfaceDataGenerator? _generator;
    private SurfaceChartSource? _source;
    private long _generation, _version, _producedTicks;
    private int _size;
    private bool _started, _paused, _suspended, _stopped;

    public RealtimeSurfaceSession(int size = DefaultSize, int seed = 0)
    {
        ValidateSize(size);
        _size = size;
        _generator = new(size + 1, seed);
        _source = _generator.Generate(SourceId, _version);
        InvalidateGeneration();
    }

    public int Size { get { lock (_gate) return _size; } }
    public double MinValue { get { lock (_gate) return -((_size + 1) / 3) - 1.5; } }
    public double MaxValue { get { lock (_gate) return (_size + 1) / 3 + 1.5; } }
    public SurfaceChartSource? CurrentSource { get { lock (_gate) return _source; } }
    public long Generation { get { lock (_gate) return _generation; } }
    /// <summary>Accepted timer ticks only; initial and resize frames are not timer ticks.</summary>
    public long ProducedTicks { get { lock (_gate) return _producedTicks; } }
    public bool IsRunning { get { lock (_gate) return CanTick; } }
    public bool IsPaused { get { lock (_gate) return _paused; } }
    public bool IsSuspended { get { lock (_gate) return _suspended; } }
    public bool IsStopped { get { lock (_gate) return _stopped; } }
    private bool CanTick => _started && !_paused && !_suspended && !_stopped;

    public void Start()
    {
        lock (_gate)
        {
            ThrowIfStopped();
            if (_started) return;
            _started = true;
            InvalidateGeneration();
        }
    }

    /// <summary>Rejects a queued tick from any earlier lifecycle generation without advancing
    /// elevations, sensitivity, the random stream or the published immutable matrix.</summary>
    public bool Tick(long generation)
    {
        lock (_gate)
        {
            if (!CanTick || generation != _generation) return false;
            var version = checked(_version + 1);
            var ticks = checked(_producedTicks + 1);
            var source = _generator!.Generate(SourceId, version);
            _source = source;
            _version = version;
            _producedTicks = ticks;
            return true;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_stopped || _paused) return;
            _paused = true;
            InvalidateGeneration();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_stopped || !_paused) return;
            _paused = false;
            InvalidateGeneration();
        }
    }

    /// <summary>Unload/temporary detach stops work without changing the user's pause choice.</summary>
    public void Suspend()
    {
        lock (_gate)
        {
            if (_stopped || _suspended) return;
            _suspended = true;
            InvalidateGeneration();
        }
    }

    public void Restore()
    {
        lock (_gate)
        {
            if (_stopped || !_suspended) return;
            _suspended = false;
            InvalidateGeneration();
        }
    }

    /// <summary>Atomically publishes Size+1 arguments per axis and one newly generated frame,
    /// including while paused, then preserves running/paused/suspended state. No old tick may
    /// advance the resized matrix. Repeating the current size is a no-op.</summary>
    public void Resize(int size)
    {
        ValidateSize(size);
        lock (_gate)
        {
            ThrowIfStopped();
            if (size == _size) return;
            var version = checked(_version + 1);
            _generator!.Resize(size + 1);
            var source = _generator.Generate(SourceId, version);
            _source = source;
            _version = version;
            _size = size;
            InvalidateGeneration();
        }
    }

    /// <summary>Idempotent terminal close: invalidates queued callbacks and drops both the
    /// current snapshot and generator. Previously handed-out immutable sources remain valid.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _source = null;
            _generator = null;
            InvalidateGeneration();
        }
    }

    public void Dispose() => Stop();

    private void InvalidateGeneration() => _generation = Interlocked.Increment(ref _nextGeneration);
    private void ThrowIfStopped()
    {
        if (_stopped) throw new InvalidOperationException("A stopped surface session cannot restart; create a new session.");
    }
    private static void ValidateSize(int size)
    {
        if (size is < MinimumSize or > MaximumSize) throw new ArgumentOutOfRangeException(nameof(size));
    }
}
