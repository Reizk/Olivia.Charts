using System.Collections.Immutable;
using Olivia.Core.Charts;
using Olivia.Core.Streaming;

namespace Olivia.Core.Workspaces;

/// <summary>
/// D1's local acquisition and publication runtime. Two independent background timers use the injected
/// clock. Production directly appends immutable acquisitions to bounded retention: there is no raw
/// input backlog. Publication prepares one coherent eight-channel frame outside the state lock and
/// posts through a capacity-one mailbox. UI work only consumes immutable prepared geometry.
/// A session is single-use; closing awaits its workers and reopening uses a new generation.
/// </summary>
public sealed class RealtimeSensorSession : IAsyncDisposable
{
    private static long _nextGeneration;
    private readonly object _gate = new();
    private readonly RetainedRing<RealtimeSensorRow> _retention;
    private readonly LatestValueMailbox<RealtimeSensorFrame> _mailbox;
    private readonly RealtimeSensorGenerator _generator;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private Action<RealtimeSensorFrame>? _deliver;
    private Task _initialization = Task.CompletedTask, _production = Task.CompletedTask, _publication = Task.CompletedTask;
    private Task? _stopTask;
    private TaskCompletionSource? _preparation;
    private ImmutableArray<RealtimeSensorRow> _publishedRows = [];
    private long _publishedVersion;
    private long _lastKey, _lastTicks, _sourceVersion, _revision;
    private long _lastPreparedVersion = -1, _lastPreparedRevision = -1;
    private long _publishedFrames, _deliveredFrames, _discardedFrames;
    private bool _started, _stopped, _initialized, _publicationPaused, _displayPaused, _productionPaused;
    private bool _following = true;
    private int _pointBudget;
    private TimeWindow? _window;
    private TimeWindow? _extent;
    private Exception? _error;
    private RealtimeSensorFrame? _currentFrame;

    public RealtimeSensorSession(Action<Action> schedule, Action<RealtimeSensorFrame> deliver,
        RealtimeSensorOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        Options = options ?? new(); _timeProvider = timeProvider ?? TimeProvider.System;
        Generation = Interlocked.Increment(ref _nextGeneration);
        _retention = new(Options.Capacity); _generator = new(Options.RandomSeed);
        _pointBudget = Options.PointBudget; _deliver = deliver;
        _mailbox = new(schedule, Deliver);
    }

    public RealtimeSensorOptions Options { get; }
    public long Generation { get; }
    public long Revision { get { lock (_gate) return _revision; } }
    public TimeWindow? Window { get { lock (_gate) return _window; } }
    public RealtimeSensorFrame? CurrentFrame { get { lock (_gate) return _currentFrame; } }
    public RealtimeSensorStatistics Statistics { get { lock (_gate) return StatisticsLocked(); } }

    /// <summary>Returns after background initialization, not after the session ends. Repeated start is idempotent.</summary>
    public Task StartAsync()
    {
        lock (_gate)
        {
            if (_stopped) throw new InvalidOperationException("A stopped sensor session cannot restart; create a new session.");
            if (_started) return _initialization;
            _started = true;
            _initialization = Task.Run(Initialize);
            _production = Task.Run(() => RunLoopAsync(production: true));
            _publication = Task.Run(() => RunLoopAsync(production: false));
            return _initialization;
        }
    }

    /// <summary>Manual-clock entry point. Hosts normally use StartAsync, which calls this off the UI thread.</summary>
    public void Initialize()
    {
        lock (_gate)
        {
            if (_initialized || _stopped) return;
            var now = _timeProvider.GetUtcNow().UtcTicks;
            var history = checked((long)Math.Max(0, Options.InitialSampleCount - 1) * Options.ProductionInterval.Ticks);
            if (now < history) throw new InvalidOperationException("The clock cannot represent the initial history interval.");
            var rows = new RealtimeSensorRow[Options.InitialSampleCount];
            for (var index = 0; index < rows.Length; index++)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                rows[index] = _generator.Next(index + 1, now - history + index * Options.ProductionInterval.Ticks);
            }
            if (rows.Length > 0)
            {
                _retention.Append(rows);
                _lastKey = rows[^1].Key; _lastTicks = rows[^1].UtcTicks;
                _sourceVersion = rows.Length;
                _extent = new(rows[0].UtcTicks, _lastTicks + 1);
                _window = Trailing(_extent.Value, Options.InitialWindow.Ticks);
            }
            _initialized = true;
        }
    }

    /// <summary>One acquisition tick. Missed timer ticks are not expanded into an unbounded catch-up queue.</summary>
    public bool ProduceTick()
    {
        lock (_gate)
        {
            if (!_initialized || _stopped || _productionPaused) return false;
            var now = _timeProvider.GetUtcNow().UtcTicks;
            // Timestamp monotonicity survives a wall-clock regression without sorting the retained source.
            var ticks = _sourceVersion == 0 ? now : Math.Max(now, checked(_lastTicks + Options.ProductionInterval.Ticks));
            if (ticks >= TimeWindow.MaximumExclusiveTicks) throw new InvalidOperationException("Sensor time exhausted.");
            AppendLocked(_generator.Next(checked(_lastKey + 1), ticks));
            return true;
        }
    }

    /// <summary>Deterministic/local source entry point. Rejects reused keys or out-of-order X before any mutation.</summary>
    public bool Append(RealtimeSensorRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_gate)
        {
            if (!_initialized) throw new InvalidOperationException("Initialize the session before supplying samples.");
            if (_stopped || _productionPaused) return false;
            if (row.Key <= _lastKey || _sourceVersion != 0 && row.UtcTicks < _lastTicks)
                throw new ArgumentException("Acquisitions must have increasing original keys and nondecreasing timestamps.", nameof(row));
            AppendLocked(row);
            return true;
        }
    }

    /// <summary>
    /// One background preparation tick. Can also be called synchronously by deterministic tests.
    /// At most one preparation is admitted. Publication pause freezes the source, while viewport edits
    /// may still be prepared from that frozen source. A manual window never moves because data arrived.
    /// </summary>
    public bool PublishTick()
    {
        ImmutableArray<RealtimeSensorRow> rows;
        long revision, version;
        int budget;
        TimeWindow window, extent;
        bool following, paused;
        RealtimeSensorStatistics statistics;
        TaskCompletionSource preparation;
        lock (_gate)
        {
            if (!_initialized || _stopped || _preparation is not null) return false;
            paused = _publicationPaused;
            if (paused)
            {
                if (_publishedRows.IsEmpty || _lastPreparedRevision == _revision) return false;
                rows = _publishedRows; version = _publishedVersion;
            }
            else
            {
                if (_sourceVersion == _lastPreparedVersion && _revision == _lastPreparedRevision) return false;
                // One array of row references, not eight full signal copies. No retained sample is sorted.
                rows = _retention.Snapshot(); version = _sourceVersion;
            }
            if (rows.IsEmpty) return false;
            extent = new(rows[0].UtcTicks, rows[^1].UtcTicks + 1);
            _extent = extent;
            if (_following || _window is null)
                _window = Trailing(extent, _window?.DurationTicks ?? Options.InitialWindow.Ticks);
            window = _window.Value; revision = _revision; budget = _pointBudget; following = _following;
            statistics = StatisticsLocked();
            preparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _preparation = preparation;
        }
        try
        {
            var frame = new RealtimeSensorFrame(Generation, revision, version, window, extent, following,
                rows, budget, statistics, _lifetime.Token);
            lock (_gate)
            {
                if (_stopped || revision != _revision || paused != _publicationPaused)
                { _discardedFrames++; return false; }
                if (!_mailbox.Publish(frame))
                {
                    _error ??= _mailbox.Statistics.Error;
                    _discardedFrames++;
                    return false;
                }
                _publishedRows = rows; _publishedVersion = version;
                _lastPreparedVersion = version; _lastPreparedRevision = revision;
                _publishedFrames++;
                return true;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        finally
        {
            lock (_gate) { _preparation = null; preparation.TrySetResult(); }
        }
    }

    public void PausePublication()
    {
        lock (_gate)
        {
            if (_stopped || _publicationPaused) return;
            _publicationPaused = true; _revision++;
            _lastPreparedRevision = _revision;
        }
    }
    public void ResumePublication()
    {
        lock (_gate)
        {
            if (_stopped || !_publicationPaused) return;
            _publicationPaused = false; _revision++;
        }
    }
    public void PauseDisplay()
    {
        lock (_gate) { if (_stopped) return; _displayPaused = true; _mailbox.Pause(); }
    }
    public void ResumeDisplay()
    {
        lock (_gate) { if (_stopped) return; _displayPaused = false; _mailbox.Resume(); }
    }
    public void PauseProduction() { lock (_gate) { if (!_stopped) _productionPaused = true; } }
    public void ResumeProduction() { lock (_gate) { if (!_stopped) _productionPaused = false; } }

    public void FollowLatest()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _following = true; _revision++;
        }
    }
    public void SetWindow(TimeWindow window)
    {
        if (window.IsEmpty || window.DurationTicks > Options.MaximumWindow.Ticks)
            throw new ArgumentOutOfRangeException(nameof(window), "A visible interval must be positive and at most 50 seconds.");
        lock (_gate)
        {
            if (_stopped) return;
            _window = !_publishedRows.IsEmpty && _extent is { } extent ? window.ClampTo(extent) : window;
            _following = false; _revision++;
        }
    }
    public void Zoom(double scale, long anchorTicks)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        lock (_gate)
        {
            if (_stopped || _window is not { } current || _extent is not { } extent) return;
            var next = current.Zoom(scale, anchorTicks, extent);
            if (next.DurationTicks > Options.MaximumWindow.Ticks)
            {
                var anchor = Math.Clamp(anchorTicks, current.StartTicks, current.EndTicks);
                var fraction = (anchor - current.StartTicks) / (double)Math.Max(1, current.DurationTicks);
                var start = Math.Clamp(anchor - (long)Math.Round(fraction * Options.MaximumWindow.Ticks),
                    extent.StartTicks, extent.EndTicks - Options.MaximumWindow.Ticks);
                next = new(start, start + Options.MaximumWindow.Ticks);
            }
            _window = next; _following = false; _revision++;
        }
    }
    public void Pan(long deltaTicks)
    {
        lock (_gate)
        {
            if (_stopped || _window is not { } current || _extent is not { } extent) return;
            _window = current.Pan(deltaTicks, extent); _following = false; _revision++;
        }
    }
    public void SetPointBudget(int pointBudget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pointBudget, 1);
        lock (_gate)
        {
            if (_stopped || _pointBudget == pointBudget) return;
            _pointBudget = pointBudget; _revision++;
        }
    }

    /// <summary>Invalidates queued/late commits immediately, then joins both timers and all accepted preparation.</summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopTask is not null) return _stopTask;
            _stopped = true; _revision++; _mailbox.Dispose(); _deliver = null; _currentFrame = null;
            var cancellation = _lifetime.CancelAsync();
            _stopTask = StopCoreAsync(cancellation, _preparation?.Task ?? Task.CompletedTask);
            return _stopTask;
        }
    }
    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task StopCoreAsync(Task cancellation, Task preparation)
    {
        try { await Task.WhenAll(cancellation, _initialization, _production, _publication, preparation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { lock (_gate) _error ??= error; }
        finally
        {
            lock (_gate)
            {
                _publishedRows = []; _retention.Clear(); _lifetime.Dispose();
            }
        }
    }

    private async Task RunLoopAsync(bool production)
    {
        try
        {
            await _initialization.ConfigureAwait(false);
            _lifetime.Token.ThrowIfCancellationRequested();
            using var timer = new CoalescingPeriodicTimer(production ? Options.ProductionInterval : Options.PublicationInterval, _timeProvider);
            if (!production) PublishTick();
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (production) ProduceTick(); else PublishTick();
                if (_mailbox.Statistics.Error is { } error) throw new InvalidOperationException("Sensor display delivery failed.", error);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate) _error ??= error;
            // Join is owned by the host/Stop task, never awaited from a worker included in that join.
            _ = StopAsync();
        }
    }

    private void AppendLocked(RealtimeSensorRow row)
    {
        _retention.Append([row]); _sourceVersion = checked(_sourceVersion + 1);
        _lastKey = row.Key; _lastTicks = row.UtcTicks;
        if (_window is null)
        {
            _extent = new(row.UtcTicks, row.UtcTicks + 1);
            _window = Trailing(_extent.Value, Options.InitialWindow.Ticks);
        }
    }
    private void Deliver(RealtimeSensorFrame frame)
    {
        lock (_gate)
        {
            if (_stopped || _displayPaused || frame.Generation != Generation || frame.Revision != _revision ||
                _currentFrame is { } current && frame.SourceVersion < current.SourceVersion)
            { _discardedFrames++; return; }
            // The callback is short and synchronous. Keeping the gate through commit makes Stop's
            // invalidation linear with delivery, including schedulers used by non-UI hosts.
            _deliver!(frame);
            if (!_stopped && frame.Revision == _revision) { _currentFrame = frame; _deliveredFrames++; }
            else _discardedFrames++;
        }
    }
    private RealtimeSensorStatistics StatisticsLocked()
    {
        var retention = _retention.Statistics; var mailbox = _mailbox.Statistics;
        return new(_sourceVersion, _publishedFrames, _deliveredFrames, _discardedFrames,
            retention.RetainedItems, retention.EvictedItems, retention.ReleasedItems,
            mailbox.HasValue ? 1 : 0, mailbox.Coalesced, _started, _stopped, _initialized,
            _publicationPaused, _displayPaused, _productionPaused, _following, _error ?? mailbox.Error);
    }
    private static TimeWindow Trailing(TimeWindow extent, long duration) =>
        new(Math.Max(0, extent.EndTicks - duration), extent.EndTicks);
}
