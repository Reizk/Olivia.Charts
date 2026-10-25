using Olivia.Core.Snapshots;

namespace Olivia.Core.Charts;

public readonly record struct HistoryQueryRequest(long Revision, TimeWindow Window, int PointBudget,
    long ExpectedSourceVersion);

/// <summary>
/// Applied owns the displayed source. Superseded, Cancelled, Stopped and Expired abort accepted work;
/// Failed and VersionMismatch fail accepted work. Admission can reject Cancelled, Busy, Stopped or Expired
/// without accepting a query or invalidating the display. Expired means the requested source is unavailable.
/// </summary>
public enum HistoryQueryStatus { Applied, Superseded, Cancelled, VersionMismatch, Busy, Stopped, Failed, Expired }

public sealed record HistoryQueryResult(HistoryQueryStatus Status, long Revision,
    HistoryChartSnapshot? Snapshot = null, Exception? Error = null);

/// <summary>
/// A coherent accounting snapshot. Accepted = Applied + Aborted + Failed + InFlight at all times.
/// Rejected requests were never accepted and do not advance the revision. Argument errors are not requests.
/// Applied is cumulative, including displays subsequently replaced or cleared.
/// </summary>
public readonly record struct HistoryQueryStatistics(long Accepted, long Applied, long Aborted, long Failed,
    int InFlight, long Rejected)
{
    public bool IsConserved => Accepted == Applied + Aborted + Failed + InFlight;
}

public enum HistorySelectionStatus { Found, NotFound, NoSnapshot, Stale, Expired, SourceUnavailable, Stopped }

/// <summary>A raw-source hit, never an approximation from downsampled display points.</summary>
public readonly record struct HistorySelectionResult(HistorySelectionStatus Status, long? SourceVersion = null,
    HistorySample? Sample = null);

/// <summary>
/// A bounded latest-query session. Cancellation requests save effort; revision and source checks provide
/// correctness even if the provider ignores cancellation. Providers and source stores are borrowed, never disposed.
/// A displayed snapshot can be retained while IsCurrent is false and must then be labelled stale.
/// Store-backed reads pin their exact source through provider and cancellation cleanup; a successful read
/// transfers that pin to the display until replacement or Stop. Store reset expires, but does not unpin, leases.
/// </summary>
public sealed class HistoryQuerySession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<HistoryQueryRequest, CancellationToken, Task<HistoryChartSnapshot>>? _provider;
    private readonly Func<HistorySeries, HistoryQueryRequest, CancellationToken, Task<HistoryChartSnapshot>>? _sourceProvider;
    private readonly VersionedSnapshotStore<HistorySeries>? _sources;
    private readonly Dictionary<long, Pending> _inFlight = new();
    private readonly int _maximumConcurrentQueries;
    private Pending? _latest;
    private HistoryChartSnapshot? _currentSnapshot;
    private SnapshotLease<HistorySeries>? _displaySource;
    private long _revision;
    private long _committedRevision = -1;
    private long _accepted;
    private long _applied;
    private long _aborted;
    private long _failed;
    private long _rejected;
    private bool _stopped;
    private Exception? _cancellationError;

    /// <summary>Compatibility path: source ownership and raw selection remain the provider's responsibility.</summary>
    public HistoryQuerySession(Func<HistoryQueryRequest, CancellationToken, Task<HistoryChartSnapshot>> provider,
        int maximumConcurrentQueries = 2)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (maximumConcurrentQueries < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentQueries));
        _provider = provider;
        _maximumConcurrentQueries = maximumConcurrentQueries;
    }

    public HistoryQuerySession(VersionedSnapshotStore<HistorySeries> sources,
        Func<HistorySeries, HistoryQueryRequest, CancellationToken, Task<HistoryChartSnapshot>>? provider = null,
        int maximumConcurrentQueries = 2)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (maximumConcurrentQueries < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentQueries));
        _sources = sources;
        _sourceProvider = provider ?? ((series, request, token) =>
            Task.FromResult(series.Query(request.Window, request.PointBudget, token)));
        _maximumConcurrentQueries = maximumConcurrentQueries;
    }

    public HistoryChartSnapshot? CurrentSnapshot { get { lock (_gate) return _currentSnapshot; } }
    public long Revision { get { lock (_gate) return _revision; } }
    public int InFlightCount { get { lock (_gate) return _inFlight.Count; } }
    public HistoryQueryStatistics Statistics
    {
        get { lock (_gate) return new(_accepted, _applied, _aborted, _failed, _inFlight.Count, _rejected); }
    }
    /// <summary>Reports a provider's throwing cancellation callback without abandoning accepted requests.</summary>
    public Exception? CancellationError { get { lock (_gate) return _cancellationError; } }
    public bool IsCurrent
    {
        get
        {
            lock (_gate)
                return !_stopped && _currentSnapshot is not null && _committedRevision == _revision &&
                    (_displaySource is null || _displaySource.TryGetValue(out _));
        }
    }

    /// <summary>
    /// Finds the exact nearest original sample within the current displayed window. A stale or expired
    /// display cannot silently select from a replacement source. Legacy providers have no owned raw source.
    /// </summary>
    public HistorySelectionResult TryFindNearest(long utcTicks, bool includeGaps = false)
    {
        if (utcTicks < 0 || utcTicks >= TimeWindow.MaximumExclusiveTicks)
            throw new ArgumentOutOfRangeException(nameof(utcTicks));
        lock (_gate)
        {
            if (_stopped) return new(HistorySelectionStatus.Stopped);
            if (_currentSnapshot is null) return new(HistorySelectionStatus.NoSnapshot);
            var version = _currentSnapshot.SourceVersion;
            if (_displaySource is not null && !_displaySource.TryGetValue(out _))
                return new(HistorySelectionStatus.Expired, version);
            if (_committedRevision != _revision) return new(HistorySelectionStatus.Stale, version);
            if (_displaySource is null) return new(HistorySelectionStatus.SourceUnavailable, version);
            if (!_displaySource.TryGetValue(out var source)) return new(HistorySelectionStatus.Expired, version);
            var sample = source!.FindNearest(utcTicks, _currentSnapshot.Window, includeGaps);
            return new(sample.HasValue ? HistorySelectionStatus.Found : HistorySelectionStatus.NotFound, version, sample);
        }
    }

    public Task<HistoryQueryResult> QueryAsync(TimeWindow window, int pointBudget, long expectedSourceVersion,
        CancellationToken cancellationToken = default)
    {
        if (pointBudget < 0) throw new ArgumentOutOfRangeException(nameof(pointBudget));
        if (expectedSourceVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedSourceVersion));
        Pending pending;
        Pending? old;
        lock (_gate)
        {
            if (_stopped) return RejectLocked(HistoryQueryStatus.Stopped);
            if (cancellationToken.IsCancellationRequested) return RejectLocked(HistoryQueryStatus.Cancelled);
            if (_inFlight.Count >= _maximumConcurrentQueries) return RejectLocked(HistoryQueryStatus.Busy);
            var revision = checked(_revision + 1);
            SnapshotLease<HistorySeries>? source = null;
            if (_sources is not null)
            {
                var acquisition = _sources.Acquire(expectedSourceVersion, SnapshotOwner.Query);
                if (acquisition.Status != SnapshotAcquireStatus.Acquired)
                    return RejectLocked(acquisition.Status switch
                    {
                        SnapshotAcquireStatus.Busy => HistoryQueryStatus.Busy,
                        SnapshotAcquireStatus.Stopped => HistoryQueryStatus.Stopped,
                        _ => HistoryQueryStatus.Expired
                    });
                source = acquisition.Lease!;
            }
            try
            {
                pending = new Pending(new HistoryQueryRequest(revision, window, pointBudget, expectedSourceVersion),
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken), cancellationToken, source);
            }
            catch
            {
                source?.Dispose();
                throw;
            }
            _revision = revision;
            old = _latest;
            _latest = pending;
            _inFlight.Add(revision, pending);
            _accepted++;
        }
        RequestCancellation(old);
        // Execute outside the lock: synchronous provider work and token callbacks are arbitrary host code.
        _ = ExecuteAsync(pending);
        return pending.Completion.Task;
    }

    /// <summary>Invalidates pending work and retains the last snapshot and its source pin as stale.</summary>
    public void Invalidate()
    {
        Pending? pending;
        lock (_gate)
        {
            if (_stopped) return;
            _revision = checked(_revision + 1);
            pending = _latest;
            _latest = null;
        }
        RequestCancellation(pending);
    }

    /// <summary>
    /// Clears and releases the display immediately, stops new reads and awaits all accepted reads,
    /// including providers ignoring cancellation and cancellation cleanup. A host timeout may cancel
    /// this wait; it does not release a running provider's pin or falsely report provider termination.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Pending[] pending;
        lock (_gate)
        {
            _stopped = true;
            _latest = null;
            _currentSnapshot = null;
            _committedRevision = -1;
            _displaySource?.Dispose();
            _displaySource = null;
            pending = _inFlight.Values.ToArray();
        }
        foreach (var query in pending) RequestCancellation(query);
        var completion = Task.WhenAll(pending.Select(query => query.Completion.Task));
        return cancellationToken.CanBeCanceled ? completion.WaitAsync(cancellationToken) : completion;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private Task<HistoryQueryResult> RejectLocked(HistoryQueryStatus status)
    {
        _rejected++;
        return Task.FromResult(new HistoryQueryResult(status, _revision));
    }

    private async Task ExecuteAsync(Pending pending)
    {
        HistoryChartSnapshot? snapshot = null;
        Exception? error = null;
        var sourceExpired = false;
        var sourceMismatch = false;
        try
        {
            pending.Cancellation.Token.ThrowIfCancellationRequested();
            if (pending.Source is { } lease)
            {
                if (!lease.TryGetValue(out var source)) sourceExpired = true;
                else if (source!.SourceVersion != pending.Request.ExpectedSourceVersion) sourceMismatch = true;
                else snapshot = await _sourceProvider!(source, pending.Request, pending.Cancellation.Token).ConfigureAwait(false);
            }
            else snapshot = await _provider!(pending.Request, pending.Cancellation.Token).ConfigureAwait(false);
            if (!sourceExpired && !sourceMismatch && snapshot is null)
                throw new InvalidOperationException("The history provider returned a null snapshot.");
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception exception) { error = exception; }

        HistoryQueryResult result;
        lock (_gate) result = EvaluateLocked(pending, snapshot, error, sourceExpired, sourceMismatch);
        // Do not hold _gate while callbacks run or linked-source Dispose waits for parent callbacks.
        // Keep the query AND its source pin in flight until all cancellation cleanup has completed.
        await pending.CleanupAsync().ConfigureAwait(false);
        lock (_gate)
        {
            // Previously determined abort/failure outcomes stay stable. A possible success must be
            // validated again: Stop, reset or a newer query may have arrived during cleanup.
            if (result.Status == HistoryQueryStatus.Applied)
                result = EvaluateLocked(pending, snapshot, error, sourceExpired, sourceMismatch);
            if (result.Status == HistoryQueryStatus.Applied)
            {
                if (pending.Source is not null && !pending.Source.TransferTo(SnapshotOwner.Display))
                    result = new HistoryQueryResult(HistoryQueryStatus.Expired, pending.Request.Revision);
                else
                {
                    var previousSource = _displaySource;
                    _displaySource = pending.Source;
                    pending.Source = null;
                    _currentSnapshot = snapshot;
                    _committedRevision = pending.Request.Revision;
                    previousSource?.Dispose();
                }
            }
            pending.Source?.Dispose();
            pending.Source = null;
            if (ReferenceEquals(_latest, pending)) _latest = null;
            _inFlight.Remove(pending.Request.Revision);
            if (result.Status == HistoryQueryStatus.Applied) _applied++;
            else if (result.Status is HistoryQueryStatus.Failed or HistoryQueryStatus.VersionMismatch) _failed++;
            else _aborted++;
            // Asynchronous continuations never execute user code under the lock.
            pending.Completion.TrySetResult(result);
        }
    }

    private HistoryQueryResult EvaluateLocked(Pending pending, HistoryChartSnapshot? snapshot, Exception? error,
        bool sourceExpired, bool sourceMismatch)
    {
        var revision = pending.Request.Revision;
        if (_stopped) return new(HistoryQueryStatus.Stopped, revision);
        if (revision != _revision) return new(HistoryQueryStatus.Superseded, revision);
        if (pending.IsCancellationRequested) return new(HistoryQueryStatus.Cancelled, revision);
        if (sourceExpired || (pending.Source is not null && !pending.Source.TryGetValue(out _)))
            return new(HistoryQueryStatus.Expired, revision);
        if (error is not null) return new(HistoryQueryStatus.Failed, revision, Error: error);
        if (sourceMismatch || snapshot!.SourceVersion != pending.Request.ExpectedSourceVersion)
            return new(HistoryQueryStatus.VersionMismatch, revision);
        if (snapshot.Window != pending.Request.Window || snapshot.PointBudget != pending.Request.PointBudget)
            return new(HistoryQueryStatus.Failed, revision,
                Error: new InvalidOperationException("The history provider returned a snapshot for a different query."));
        return new(HistoryQueryStatus.Applied, revision, snapshot);
    }

    private void RequestCancellation(Pending? pending)
    {
        if (pending is null || !pending.BeginCancellation()) return;
        try { pending.Cancellation.Cancel(); }
        catch (AggregateException error) { lock (_gate) _cancellationError = error; }
        finally { pending.EndCancellation(); }
    }

    private sealed class Pending(HistoryQueryRequest request, CancellationTokenSource cancellation,
        CancellationToken callerCancellation, SnapshotLease<HistorySeries>? source)
    {
        private readonly object _cancellationGate = new();
        private readonly TaskCompletionSource _cancellationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancellationCount;
        private bool _cleanupStarted;
        public HistoryQueryRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        // Linked disposal unregisters from the caller before the final commit check. Observe the
        // original token as well so cancellation in that gap is not lost.
        public bool IsCancellationRequested => Cancellation.IsCancellationRequested || callerCancellation.IsCancellationRequested;
        public SnapshotLease<HistorySeries>? Source { get; set; } = source;
        public TaskCompletionSource<HistoryQueryResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BeginCancellation()
        {
            lock (_cancellationGate)
            {
                if (_cleanupStarted) return false;
                _cancellationCount++;
                return true;
            }
        }

        public void EndCancellation()
        {
            lock (_cancellationGate)
            {
                _cancellationCount--;
                if (_cleanupStarted && _cancellationCount == 0) _cancellationFinished.TrySetResult();
            }
        }

        public async Task CleanupAsync()
        {
            Task completion;
            lock (_cancellationGate)
            {
                _cleanupStarted = true;
                completion = _cancellationCount == 0 ? Task.CompletedTask : _cancellationFinished.Task;
            }
            await completion.ConfigureAwait(false);
            Cancellation.Dispose();
        }
    }
}
