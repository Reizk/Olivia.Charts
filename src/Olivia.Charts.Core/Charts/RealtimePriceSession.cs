using Olivia.Core.Streaming;

namespace Olivia.Core.Charts;

/// <summary>A captured immutable source and its display-lifecycle token. Production advancing
/// after capture does not invalidate a frame; a newer captured source, pausing, resuming or stopping does.</summary>
public sealed record RealtimePriceFrame(PriceChartSource Source, long Generation, long PublicationRevision);

public readonly record struct RealtimePriceStatistics(int Capacity, int RetainedBars, long EvictedBars,
    long ProducedTicks, long SourceVersion, long RejectedClockTicks, long ReleasedBars,
    int ActiveWorkers, Exception? Error);

/// <summary>
/// Single-use, bounded synthetic Financial session with optional immutable demo-history seed.
/// Live ticks are NOT market data or an exact reproduction of the official tick generator. A cancellable 41 ms background delay replaces one immutable current-UTC-minute tail;
/// only a change of whole UTC minute completes a bar. Missed minutes are not fabricated.
/// Display pause never pauses acquisition. Only a UI-requested capture copies retained bars.
/// Restart means constructing a new session with a new dataset generation. The requested delay
/// follows each completed tick; scheduler/work overhead can cause drift, not a promised FPS.
/// </summary>
public sealed class RealtimePriceSession : IAsyncDisposable
{
    public static TimeSpan ProductionInterval { get; } = TimeSpan.FromMilliseconds(41);
    private static long _nextGeneration;
    private readonly object _gate = new();
    private readonly RetainedRing<PriceBar> _completed;
    private readonly TimeProvider _timeProvider;
    private readonly Random _random;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _production = Task.CompletedTask;
    private Task? _stopTask;
    private PriceBar? _tail;
    private PriceChartSource? _capturedSource;
    private long _lastTickUtcTicks, _sourceVersion, _publicationRevision, _producedTicks;
    private long _rejectedClockTicks, _releasedTailBars;
    private long _historyRevision, _capturedHistoryRevision = -1;
    private long _lastGeneratedKey;
    private int _activeWorkers;
    private bool _started, _running, _stopped, _displayPaused, _dirty = true;
    private Exception? _error;
    private readonly string _sourceLabel;

    public RealtimePriceSession(int capacity = PriceChartSource.MaximumBars, int initialBars = 240,
        TimeProvider? timeProvider = null, int seed = 3)
        : this(capacity, initialBars, timeProvider, seed, null) { }

    /// <summary>Seeds bounded acquisition with already validated immutable history. The final
    /// bar must have OHLC and volume, and cannot be in the clock's future. Future ticks are
    /// synthetic; seed values and original keys/offsets are not rewritten. New keys advance
    /// beyond the maximum seed key; a seed containing Int64.MaxValue cannot be extended.</summary>
    public RealtimePriceSession(PriceChartSource initialHistory, TimeProvider? timeProvider = null,
        int seed = 3, int capacity = PriceChartSource.MaximumBars)
        : this(capacity, 1, timeProvider, seed, initialHistory ?? throw new ArgumentNullException(nameof(initialHistory))) { }

    private RealtimePriceSession(int capacity, int initialBars, TimeProvider? timeProvider, int seed,
        PriceChartSource? initialHistory)
    {
        if (capacity is < 2 or > PriceChartSource.MaximumBars)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (initialBars < 1 || initialBars > capacity)
            throw new ArgumentOutOfRangeException(nameof(initialBars));
        Capacity = capacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _random = new(seed);
        _completed = new(capacity - 1);
        Generation = Interlocked.Increment(ref _nextGeneration);
        var now = _timeProvider.GetUtcNow().UtcTicks;
        _sourceLabel = initialHistory is null ? "Synthetic realtime OHLC + volume (not market data)"
            : "Demo history + synthetic live OHLC + volume (not market data)";
        if (initialHistory is not null)
        {
            if (initialHistory.Bars.IsEmpty || initialHistory.Bars.Length > capacity ||
                initialHistory.Bars[^1].IsGap || initialHistory.Bars[^1].Volume is null ||
                initialHistory.Bars[^1].Timestamp.UtcTicks > now || initialHistory.Bars.Any(b => b.Key == long.MaxValue))
                throw new ArgumentException("Seed history must fit retention, leave room for a new key, and end in a nonfuture OHLC bar with volume.", nameof(initialHistory));
            _lastGeneratedKey = initialHistory.Bars.Max(b => b.Key);
            _completed.Append(initialHistory.Bars.AsSpan(0, initialHistory.Bars.Length - 1));
            _tail = initialHistory.Bars[^1];
            _lastTickUtcTicks = now;
            return;
        }
        var minute = MinuteTicks(now);
        var history = (initialBars - 1L) * TimeSpan.TicksPerMinute;
        if (minute < history)
            throw new ArgumentOutOfRangeException(nameof(timeProvider), "The clock cannot represent the requested initial history.");
        var close = 100d;
        for (var index = 0; index < initialBars; index++)
        {
            var bar = NextBar(index + 1, minute - history + index * TimeSpan.TicksPerMinute, close);
            if (index + 1 == initialBars) _tail = bar;
            else _completed.Append([bar]);
            close = bar.Close!.Value;
        }
        _lastTickUtcTicks = now;
        _lastGeneratedKey = initialBars;
    }

    public int Capacity { get; }
    public long Generation { get; }
    public bool IsRunning { get { lock (_gate) return _running; } }
    public bool IsDisplayPaused { get { lock (_gate) return _displayPaused; } }
    public bool IsStopped { get { lock (_gate) return _stopped; } }
    public RealtimePriceStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                var retention = _completed.Statistics;
                return new(Capacity, retention.RetainedItems + (_tail.HasValue ? 1 : 0),
                    retention.EvictedItems, _producedTicks, _sourceVersion, _rejectedClockTicks,
                    retention.ReleasedItems + _releasedTailBars, _activeWorkers, _error);
            }
        }
    }

    /// <summary>Starts exactly one cancellable worker. Repeated start is harmless until stop;
    /// a stopped session is never resurrected. Timer faults are exposed in Statistics.Error.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_stopped) throw new InvalidOperationException("A stopped price session cannot restart; create a new session.");
            if (_started) return;
            _started = _running = true;
            try
            {
                _activeWorkers = 1;
                // Register the first delay before returning, so injected clocks can drive the real worker.
                _production = RunLoopAsync(_lifetime.Token);
            }
            catch (Exception error)
            {
                _error ??= error;
                MarkStoppedLocked();
            }
        }
    }

    /// <summary>The actual producer entry point, also usable with an injected manual clock.
    /// Equal clock instants may update the same tail; backward instants are rejected before
    /// random state, OHLC, volume, keys or source version are changed.</summary>
    public bool Tick()
    {
        lock (_gate)
        {
            if (!_running || _stopped) return false;
            var now = _timeProvider.GetUtcNow().UtcTicks;
            if (now < _lastTickUtcTicks)
            {
                _rejectedClockTicks++;
                return false;
            }
            var old = _tail!.Value;
            var minute = MinuteTicks(now);
            var version = checked(_sourceVersion + 1);
            var produced = checked(_producedTicks + 1);
            var sameMinute = minute == MinuteTicks(old.Timestamp.UtcTicks);
            var point = NextBar(sameMinute ? old.Key : checked(_lastGeneratedKey + 1), minute, old.Close!.Value);
            PriceBar next;
            if (sameMinute)
            {
                next = new(old.Key, old.Timestamp, old.Open,
                    Math.Max(old.High!.Value, point.High!.Value), Math.Min(old.Low!.Value, point.Low!.Value),
                    point.Close, old.Volume!.Value + point.Volume!.Value);
            }
            else
            {
                next = point;
                _completed.Append([old]);
                _historyRevision++;
                _lastGeneratedKey = next.Key;
            }
            _tail = next;
            _lastTickUtcTicks = now;
            _sourceVersion = version;
            _producedTicks = produced;
            _dirty = true;
            return true;
        }
    }

    public void PauseDisplay()
    {
        lock (_gate)
        {
            if (_stopped || _displayPaused) return;
            _displayPaused = true;
            _publicationRevision++;
        }
    }

    public void ResumeDisplay()
    {
        lock (_gate)
        {
            if (_stopped || !_displayPaused) return;
            _displayPaused = false;
            _publicationRevision++;
            _dirty = true;
        }
    }

    /// <summary>Pulls the latest bounded state, never a backlog. While paused even forced
    /// capture returns null. A forced unchanged capture reuses the immutable source identity.</summary>
    public RealtimePriceFrame? CaptureFrame(bool force = false)
    {
        lock (_gate)
        {
            if (!_running || _stopped || _displayPaused || (!force && !_dirty)) return null;
            if (_capturedSource is null || _capturedSource.SourceVersion != _sourceVersion)
            {
                if (_capturedSource is not null && _capturedHistoryRevision == _historyRevision)
                    _capturedSource = _capturedSource.ReplaceTail(_tail!.Value, _sourceVersion);
                else
                {
                    var bars = _completed.Snapshot().Add(_tail!.Value);
                    _capturedSource = new("synthetic-realtime-price", _sourceLabel,
                        bars, Generation, _sourceVersion);
                    _capturedHistoryRevision = _historyRevision;
                }
            }
            _dirty = false;
            return new(_capturedSource, Generation, _publicationRevision);
        }
    }

    public bool IsFrameCurrent(RealtimePriceFrame? frame)
    {
        lock (_gate)
            return frame is not null && _running && !_stopped && !_displayPaused &&
                frame.Generation == Generation && frame.PublicationRevision == _publicationRevision &&
                ReferenceEquals(frame.Source, _capturedSource) && frame.Source.DatasetVersion == Generation &&
                frame.Source.SeriesId == "synthetic-realtime-price";
    }

    /// <summary>Invalidates late frames and releases retention immediately, then cancels and
    /// joins the worker. Every stop/dispose caller awaits the same shutdown, without a timer delay.</summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopTask is not null) return _stopTask;
            MarkStoppedLocked();
            _stopTask = StopCoreAsync(_lifetime.CancelAsync(), _production);
            return _stopTask;
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task RunLoopAsync(CancellationToken cancellation)
    {
        try
        {
            while (true)
            {
                // One one-shot wait at a time, with no catch-up backlog. Task.Delay also avoids
                // PeriodicTimer's .NET 8 partially-constructed finalizer hazard if CreateTimer throws.
                await Task.Delay(ProductionInterval, _timeProvider, cancellation).ConfigureAwait(false);
                if (!Tick() && IsStopped) break;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate) _error ??= error;
        }
        finally
        {
            lock (_gate)
            {
                _activeWorkers = 0;
                MarkStoppedLocked();
            }
        }
    }

    private async Task StopCoreAsync(Task cancellation, Task production)
    {
        try { await Task.WhenAll(cancellation, production).ConfigureAwait(false); }
        catch (Exception error) { lock (_gate) _error ??= error; }
        finally
        {
            lock (_gate)
            {
                _production = Task.CompletedTask;
                _lifetime.Dispose();
            }
        }
    }

    private void MarkStoppedLocked()
    {
        if (_stopped) return;
        _stopped = true;
        _running = false;
        _publicationRevision++;
        _dirty = false;
        _capturedSource = null;
        if (_tail.HasValue) _releasedTailBars++;
        _tail = null;
        _completed.Clear();
    }

    private PriceBar NextBar(long key, long minuteTicks, double open)
    {
        // Deliberately invented deterministic demo values, not a replay of official CSV data.
        var close = Math.Max(1, open + (_random.NextDouble() - .5) * .4);
        var high = Math.Max(open, close) + _random.NextDouble() * .1;
        var low = Math.Min(open, close) - _random.NextDouble() * .1;
        var volume = _random.Next(1, 100);
        return new(key, new DateTimeOffset(minuteTicks, TimeSpan.Zero), open, high, low, close, volume);
    }

    private static long MinuteTicks(long utcTicks) => utcTicks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute;
}

/// <summary>Preserves a displayed ordinal viewport across immutable versions of the same
/// dataset and series. A surviving first requested timestamp remains the first visible bar;
/// an evicted anchor clamps to the first survivor at or after it. Count and rendering options
/// never silently change. The full default range stays full; a different dataset retains the
/// requested ordinal options without pretending that its timestamps identify the same data.</summary>
public static class PriceRealtimeViewport
{
    public static PriceChartOptions Preserve(PriceChartSource previousDisplayedSource,
        PriceChartSource nextDisplayedSource, PriceChartOptions options)
    {
        ArgumentNullException.ThrowIfNull(previousDisplayedSource);
        ArgumentNullException.ThrowIfNull(nextDisplayedSource);
        ArgumentNullException.ThrowIfNull(options);
        // The exact aggregation is needed to rebase compressed buckets; PricePlot does
        // that atomically when publishing the new aggregated frame.
        if (options.TimeViewport is not null) return options;
        if (previousDisplayedSource.DatasetVersion != nextDisplayedSource.DatasetVersion ||
            previousDisplayedSource.SeriesId != nextDisplayedSource.SeriesId ||
            options.BarStart == 0 && options.BarCount == PriceChartSource.MaximumBars ||
            options.BarStart >= previousDisplayedSource.Bars.Length)
            return options;
        var anchor = previousDisplayedSource.Bars[options.BarStart].Timestamp.UtcTicks;
        var low = 0;
        var high = nextDisplayedSource.Bars.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (nextDisplayedSource.Bars[middle].Timestamp.UtcTicks < anchor) low = middle + 1;
            else high = middle;
        }
        return low == options.BarStart ? options : new(low, options.BarCount, options.GeometryBudget,
            options.FixedDomain, options.BodyWidthFraction);
    }
}
