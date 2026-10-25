using Olivia.Core.Charts;
using Olivia.Core.Snapshots;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class HistoryQueryLifetimeTests
{
    [Fact]
    public async Task DisplayPinsExactRawSourceAcrossReplacementAndReleasesItOnNextCommit()
    {
        using var store = NewStore();
        var first = Series(1);
        Publish(store, first);
        await using var session = new HistoryQuerySession(store);
        Assert.Equal(HistorySelectionStatus.NoSnapshot, session.TryFindNearest(5).Status);
        var result = await session.QueryAsync(first.Extent!.Value, 2, 1);
        Assert.Equal(HistoryQueryStatus.Applied, result.Status);
        Assert.Equal(new long[] { 100, 109 }, result.Snapshot!.Segments[0].Points.Select(point => point.Key));
        Assert.Equal(0, store.Statistics.QueryLeases);
        Assert.Equal(1, store.Statistics.DisplayLeases);
        var replacement = Series(2);
        Publish(store, replacement);
        var hit = session.TryFindNearest(5);
        Assert.Equal(HistorySelectionStatus.Found, hit.Status);
        Assert.Equal(1, hit.SourceVersion);
        Assert.Equal(105, hit.Sample!.Value.Key);
        Assert.Equal(5d, hit.Sample.Value.Value);
        Assert.Equal(2, store.Statistics.RetainedVersions);
        Assert.Equal(20, store.Statistics.RetainedItems);

        Assert.Equal(HistoryQueryStatus.Applied,
            (await session.QueryAsync(replacement.Extent!.Value, 2, 2)).Status);
        Assert.Equal(1, store.Statistics.RetainedVersions);
        Assert.Equal(1, store.Statistics.DisplayLeases);
        Assert.Equal(SnapshotAcquireStatus.Expired, store.Acquire(1, SnapshotOwner.Query).Status);
        Assert.Equal(205, session.TryFindNearest(5).Sample!.Value.Key);
        AssertCounters(session, accepted: 2, applied: 2);

        await session.StopAsync();
        Assert.Null(session.CurrentSnapshot);
        Assert.False(session.IsCurrent);
        Assert.Equal(0, store.Statistics.ActiveLeases);
        Assert.Equal(HistorySelectionStatus.Stopped, session.TryFindNearest(5).Status);
        // The session borrows the store, so stopping it cannot stop future publication.
        Publish(store, Series(3));
    }

    [Fact]
    public async Task SlowQueryRetainsItsOriginalSourceWhileUnpinnedLatestVersionsAreReplaced()
    {
        using var store = NewStore(maximumVersions: 2);
        var first = Series(1);
        Publish(store, first);
        var gate = NewGate();
        HistorySeries? observed = null;
        await using var session = new HistoryQuerySession(store, (source, _, _) =>
        {
            observed = source;
            return gate.Task;
        });
        var query = session.QueryAsync(first.Extent!.Value, 2, 1);
        Assert.Same(first, observed);
        Assert.Equal(1, store.Statistics.QueryLeases);
        AssertCounters(session, accepted: 1, inFlight: 1);
        Publish(store, Series(2));
        Publish(store, Series(3));
        Assert.Equal(2, store.Statistics.RetainedVersions);
        Assert.Equal(20, store.Statistics.RetainedItems);
        Assert.Equal(SnapshotAcquireStatus.Expired, store.Acquire(2, SnapshotOwner.Query).Status);
        gate.SetResult(first.Query(first.Extent.Value, 2));
        Assert.Equal(HistoryQueryStatus.Applied, (await query).Status);
        Assert.Equal(1, session.CurrentSnapshot!.SourceVersion);
        Assert.Equal(105, session.TryFindNearest(5).Sample!.Value.Key);
        Assert.Equal(0, store.Statistics.QueryLeases);
        Assert.Equal(1, store.Statistics.DisplayLeases);
    }

    [Fact]
    public async Task NewSourceCommitsWhileSupersededNonCooperativeProviderRemainsPinned()
    {
        using var store = NewStore();
        var first = Series(1);
        var second = Series(2);
        Publish(store, first);
        var oldGate = NewGate();
        await using var session = new HistoryQuerySession(store, (source, request, _) =>
            request.Revision == 1 ? oldGate.Task : Task.FromResult(source.Query(request.Window, request.PointBudget)));
        var old = session.QueryAsync(first.Extent!.Value, 2, 1);
        Publish(store, second);
        Assert.Equal(HistoryQueryStatus.Applied, (await session.QueryAsync(second.Extent!.Value, 2, 2)).Status);
        Assert.Equal(1, store.Statistics.QueryLeases);
        Assert.Equal(1, store.Statistics.DisplayLeases);
        AssertCounters(session, accepted: 2, applied: 1, inFlight: 1);
        oldGate.SetResult(first.Query(first.Extent.Value, 2));
        Assert.Equal(HistoryQueryStatus.Superseded, (await old).Status);
        Assert.Equal(2, session.CurrentSnapshot!.SourceVersion);
        Assert.Equal(205, session.TryFindNearest(5).Sample!.Value.Key);
        Assert.Equal(1, store.Statistics.RetainedVersions);
        Assert.Equal(0, store.Statistics.QueryLeases);
        AssertCounters(session, accepted: 2, applied: 1, aborted: 1);
    }

    [Fact]
    public async Task BusyAdmissionNeverAcquiresAnotherSourceOrInvalidatesCurrentDisplay()
    {
        using var store = NewStore(maximumLeases: 1);
        var source = Series(1);
        Publish(store, source);
        var calls = 0;
        await using var session = new HistoryQuerySession(store, (series, request, token) =>
        {
            calls++;
            return Task.FromResult(series.Query(request.Window, request.PointBudget, token));
        });
        using (var occupied = store.Acquire(1, SnapshotOwner.Pending).Lease!)
        {
            Assert.Equal(HistoryQueryStatus.Busy, (await session.QueryAsync(source.Extent!.Value, 2, 1)).Status);
            Assert.Equal(0, calls);
            Assert.Equal(0, session.Revision);
            AssertCounters(session, rejected: 1);
        }
        Assert.Equal(HistoryQueryStatus.Applied, (await session.QueryAsync(source.Extent!.Value, 2, 1)).Status);
        var display = session.CurrentSnapshot;
        for (var i = 0; i < 20; i++)
            Assert.Equal(HistoryQueryStatus.Busy, (await session.QueryAsync(source.Extent.Value, 3, 1)).Status);
        Assert.True(session.IsCurrent);
        Assert.Same(display, session.CurrentSnapshot);
        Assert.Equal(1, session.Revision);
        Assert.Equal(1, calls);
        Assert.Equal(1, store.Statistics.ActiveLeases);
        AssertCounters(session, accepted: 1, applied: 1, rejected: 21);
    }

    [Fact]
    public async Task ConcurrencyRejectionDoesNotQueueOrTakeExtraLeases()
    {
        using var store = NewStore();
        var source = Series(1);
        Publish(store, source);
        var gate = NewGate();
        await using var session = new HistoryQuerySession(store, (_, _, _) => gate.Task, maximumConcurrentQueries: 1);
        var query = session.QueryAsync(source.Extent!.Value, 2, 1);
        for (var i = 0; i < 20; i++)
            Assert.Equal(HistoryQueryStatus.Busy, (await session.QueryAsync(source.Extent.Value, 2, 1)).Status);
        Assert.Equal(1, store.Statistics.ActiveLeases);
        AssertCounters(session, accepted: 1, inFlight: 1, rejected: 20);
        gate.SetResult(source.Query(source.Extent.Value, 2));
        Assert.Equal(HistoryQueryStatus.Applied, (await query).Status);
        AssertCounters(session, accepted: 1, applied: 1, rejected: 20);
    }

    [Fact]
    public async Task MissingRetiredCancelledAndStoppedAdmissionsHaveExplicitResultsWithoutAcceptance()
    {
        using var store = NewStore();
        var source = Series(1);
        Publish(store, source);
        Publish(store, Series(2));
        await using var session = new HistoryQuerySession(store);
        Assert.Equal(HistoryQueryStatus.Expired, (await session.QueryAsync(source.Extent!.Value, 2, 1)).Status);
        Assert.Equal(HistoryQueryStatus.Expired, (await session.QueryAsync(source.Extent.Value, 2, 999)).Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(HistoryQueryStatus.Cancelled,
            (await session.QueryAsync(source.Extent.Value, 2, 2, cancellation.Token)).Status);
        store.Dispose();
        Assert.Equal(HistoryQueryStatus.Stopped, (await session.QueryAsync(source.Extent.Value, 2, 2)).Status);
        Assert.Equal(0, session.Revision);
        AssertCounters(session, rejected: 4);
    }

    [Fact]
    public async Task ResetExpiresLateResultButChargesItsSourceUntilProviderActuallyCompletes()
    {
        using var store = NewStore(maximumVersions: 1);
        var source = Series(1);
        Publish(store, source);
        var gate = NewGate();
        await using var session = new HistoryQuerySession(store, (_, _, _) => gate.Task);
        var query = session.QueryAsync(source.Extent!.Value, 2, 1);
        store.Reset();
        Assert.Equal(1, store.Statistics.QueryLeases);
        Assert.Equal(10, store.Statistics.RetainedItems);
        var newer = Series(2);
        Assert.Equal(SnapshotPublishStatus.BudgetExceeded, store.Publish(2, newer, newer.Count));
        Assert.False(query.IsCompleted);
        gate.SetResult(source.Query(source.Extent.Value, 2));
        Assert.Equal(HistoryQueryStatus.Expired, (await query).Status);
        Assert.Null(session.CurrentSnapshot);
        Assert.False(session.IsCurrent);
        Assert.Equal(0, store.Statistics.RetainedVersions);
        Assert.Equal(0, store.Statistics.RetainedItems);
        Assert.Equal(0, store.Statistics.ActiveLeases);
        AssertCounters(session, accepted: 1, aborted: 1);
        Publish(store, newer);
    }

    [Fact]
    public async Task StoreDisposalExpiresAnActiveQueryWithoutReleasingItsPinEarly()
    {
        using var store = NewStore();
        var source = Series(1);
        Publish(store, source);
        var gate = NewGate();
        await using var session = new HistoryQuerySession(store, (_, _, _) => gate.Task);
        var query = session.QueryAsync(source.Extent!.Value, 2, 1);
        store.Dispose();
        Assert.Equal(1, store.Statistics.QueryLeases);
        Assert.Equal(10, store.Statistics.RetainedItems);
        gate.SetResult(source.Query(source.Extent.Value, 2));
        Assert.Equal(HistoryQueryStatus.Expired, (await query).Status);
        Assert.Equal(0, store.Statistics.ActiveLeases);
        Assert.Equal(0, store.Statistics.RetainedVersions);
        AssertCounters(session, accepted: 1, aborted: 1);
    }

    [Fact]
    public async Task InvalidationRetainsStaleDisplayAndResetPreventsSelectionFromExpiredRawSource()
    {
        using var store = NewStore();
        var source = Series(1);
        Publish(store, source);
        await using var session = new HistoryQuerySession(store);
        await session.QueryAsync(source.Extent!.Value, 2, 1);
        var display = session.CurrentSnapshot;
        session.Invalidate();
        Assert.False(session.IsCurrent);
        Assert.Same(display, session.CurrentSnapshot);
        Assert.Equal(HistorySelectionStatus.Stale, session.TryFindNearest(5).Status);
        Assert.Equal(1, store.Statistics.DisplayLeases);
        store.Reset();
        Assert.Equal(HistorySelectionStatus.Expired, session.TryFindNearest(5).Status);
        Assert.Equal(1, store.Statistics.RetainedVersions);
        Assert.Equal(10, store.Statistics.RetainedItems);
        await session.StopAsync();
        Assert.Null(session.CurrentSnapshot);
        Assert.Equal(0, store.Statistics.ActiveLeases);
        Assert.Equal(0, store.Statistics.RetainedVersions);
        AssertCounters(session, accepted: 1, applied: 1);
    }

    [Fact]
    public async Task ResetMakesPreviouslyCurrentDisplayStaleEvenWithoutSessionInvalidation()
    {
        using var store = NewStore();
        var source = Series(1);
        Publish(store, source);
        await using var session = new HistoryQuerySession(store);
        await session.QueryAsync(source.Extent!.Value, 2, 1);
        Assert.True(session.IsCurrent);
        store.Reset();
        Assert.False(session.IsCurrent);
        Assert.Equal(HistorySelectionStatus.Expired, session.TryFindNearest(5).Status);
    }

    [Fact]
    public async Task StopClearsAppliedDisplayImmediatelyButWaitsForNonCooperativeActiveLease()
    {
        using var store = NewStore();
        var first = Series(1);
        var second = Series(2);
        Publish(store, first);
        var gate = NewGate();
        await using var session = new HistoryQuerySession(store, (source, request, _) =>
            request.Revision == 1 ? Task.FromResult(source.Query(request.Window, request.PointBudget)) : gate.Task);
        await session.QueryAsync(first.Extent!.Value, 2, 1);
        Publish(store, second);
        var query = session.QueryAsync(second.Extent!.Value, 2, 2);
        using var timeout = new CancellationTokenSource();
        var interruptedWait = session.StopAsync(timeout.Token);
        Assert.False(interruptedWait.IsCompleted);
        Assert.Null(session.CurrentSnapshot);
        Assert.False(session.IsCurrent);
        Assert.Equal(0, store.Statistics.DisplayLeases);
        Assert.Equal(1, store.Statistics.QueryLeases);
        Assert.Equal(1, store.Statistics.RetainedVersions);
        timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interruptedWait);
        Assert.Equal(1, store.Statistics.QueryLeases);
        AssertCounters(session, accepted: 2, applied: 1, inFlight: 1);
        var stop = session.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.Equal(HistoryQueryStatus.Stopped, (await session.QueryAsync(second.Extent.Value, 2, 2)).Status);
        gate.SetResult(second.Query(second.Extent.Value, 2));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HistoryQueryStatus.Stopped, (await query).Status);
        Assert.Null(session.CurrentSnapshot);
        Assert.Equal(0, store.Statistics.ActiveLeases);
        AssertCounters(session, accepted: 2, applied: 1, aborted: 1, rejected: 1);
    }

    [Fact]
    public async Task IgnoredCancellationAndInvalidationNeverCommitAndPreserveCounterConservation()
    {
        using var store = NewStore();
        var source = Series(1);
        Publish(store, source);
        var gates = new Queue<TaskCompletionSource<HistoryChartSnapshot>>([NewGate(), NewGate()]);
        var firstGate = gates.Peek();
        await using var session = new HistoryQuerySession(store, (_, _, _) => gates.Dequeue().Task);
        using var cancellation = new CancellationTokenSource();
        var first = session.QueryAsync(source.Extent!.Value, 2, 1, cancellation.Token);
        cancellation.Cancel();
        Assert.Equal(1, store.Statistics.QueryLeases);
        firstGate.SetResult(source.Query(source.Extent.Value, 2));
        Assert.Equal(HistoryQueryStatus.Cancelled, (await first).Status);
        Assert.Null(session.CurrentSnapshot);
        var secondGate = gates.Peek();
        var second = session.QueryAsync(source.Extent.Value, 2, 1);
        session.Invalidate();
        Assert.Equal(1, store.Statistics.QueryLeases);
        secondGate.SetResult(source.Query(source.Extent.Value, 2));
        Assert.Equal(HistoryQueryStatus.Superseded, (await second).Status);
        Assert.Null(session.CurrentSnapshot);
        Assert.Equal(0, store.Statistics.ActiveLeases);
        AssertCounters(session, accepted: 2, aborted: 2);
    }

    [Fact]
    public async Task ProviderFaultAndMismatchedSourceAreFailuresAndReleasePins()
    {
        using var store = NewStore();
        var source = Series(1);
        Publish(store, source);
        var expected = new InvalidOperationException("fixture failure");
        await using var session = new HistoryQuerySession(store, (_, request, _) => request.Revision == 1
            ? Task.FromException<HistoryChartSnapshot>(expected)
            : Task.FromResult(Series(999).Query(request.Window, request.PointBudget)));
        var failure = await session.QueryAsync(source.Extent!.Value, 2, 1);
        Assert.Equal(HistoryQueryStatus.Failed, failure.Status);
        Assert.Same(expected, failure.Error);
        Assert.Equal(0, store.Statistics.ActiveLeases);
        Assert.Equal(HistoryQueryStatus.VersionMismatch,
            (await session.QueryAsync(source.Extent.Value, 2, 1)).Status);
        Assert.Equal(0, store.Statistics.ActiveLeases);
        AssertCounters(session, accepted: 2, failed: 2);
    }

    [Fact]
    public async Task IncorrectPublishedSourceIdentityNeverCallsProvider()
    {
        using var store = NewStore();
        var source = Series(2);
        Assert.Equal(SnapshotPublishStatus.Published, store.Publish(1, source, source.Count));
        var called = false;
        await using var session = new HistoryQuerySession(store, (series, request, _) =>
        {
            called = true;
            return Task.FromResult(series.Query(request.Window, request.PointBudget));
        });
        Assert.Equal(HistoryQueryStatus.VersionMismatch,
            (await session.QueryAsync(source.Extent!.Value, 2, 1)).Status);
        Assert.False(called);
        Assert.Equal(0, store.Statistics.ActiveLeases);
        AssertCounters(session, accepted: 1, failed: 1);
    }

    [Fact]
    public Task CancellationCallbackCleanupKeepsPinAndInflightChargeAfterProviderCompletes() =>
        AssertCancellationCallbackCleanup(HistoryQueryStatus.Superseded);

    [Fact]
    public Task StopBeforeProviderCompletionKeepsPinAndInflightChargeThroughCancellationCleanup() =>
        AssertCancellationCallbackCleanup(HistoryQueryStatus.Stopped);

    private static async Task AssertCancellationCallbackCleanup(HistoryQueryStatus expectedStatus)
    {
        using var store = NewStore();
        var source = Series(1);
        Publish(store, source);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        var cancellation = Task.CompletedTask;
        var providerReturned = false;
        HistoryQuerySession session = null!;
        session = new HistoryQuerySession(store, (series, request, token) =>
        {
            token.Register(() =>
            {
                callbackEntered.TrySetResult();
                releaseCallback.Wait();
            });
            // A dedicated cancellation worker avoids depending on a spare pool thread while
            // this synchronous provider waits for the callback's explicit entry signal.
            cancellation = expectedStatus == HistoryQueryStatus.Stopped
                ? Task.Factory.StartNew(() => session.StopAsync(), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()
                : Task.Factory.StartNew(session.Invalidate, CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            // This task is already complete when ExecuteAsync first awaits it. The await
            // cannot suspend, so QueryAsync returns only after evaluation and blocked cleanup.
            // This does not rely on a TaskCompletionSource continuation being inlined.
            var completedProvider = Task.FromResult(series.Query(request.Window, request.PointBudget));
            providerReturned = true;
            return completedProvider;
        });
        try
        {
            var query = session.QueryAsync(source.Extent!.Value, 2, 1);
            // ExecuteAsync catches provider exceptions; do not let an abort status hide a
            // failed fixture gate or a provider that never returned its completed result.
            Assert.True(providerReturned, "The provider must return its already-completed task after callback entry.");
            Assert.True(callbackEntered.Task.IsCompletedSuccessfully);
            Assert.False(query.IsCompleted);
            Assert.Equal(1, session.InFlightCount);
            Assert.Equal(1, store.Statistics.QueryLeases);
            store.Reset();
            Assert.Equal(10, store.Statistics.RetainedItems);
            AssertCounters(session, accepted: 1, inFlight: 1);
            if (expectedStatus == HistoryQueryStatus.Stopped)
                Assert.Equal(HistorySelectionStatus.Stopped, session.TryFindNearest(5).Status);
            var stop = session.StopAsync();
            Assert.False(stop.IsCompleted);
            releaseCallback.Set();
            await Task.WhenAll(query, cancellation, stop).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expectedStatus, (await query).Status);
            Assert.Equal(0, store.Statistics.ActiveLeases);
            Assert.Equal(0, store.Statistics.RetainedItems);
            AssertCounters(session, accepted: 1, aborted: 1);
        }
        finally
        {
            releaseCallback.Set();
            await Task.WhenAll(cancellation, session.DisposeAsync().AsTask())
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task LinkedParentCancellationCleanupKeepsSourcePinnedWithoutHoldingSessionLock()
    {
        using var store = NewStore();
        var source = Series(1);
        Publish(store, source);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        using var parentCancellation = new CancellationTokenSource();
        var provider = new TaskCompletionSource<HistoryChartSnapshot>();
        HistoryQuerySession session = null!;
        session = new HistoryQuerySession(store, (_, _, token) =>
        {
            token.Register(() =>
            {
                callbackEntered.TrySetResult();
                releaseCallback.Wait();
                _ = session.Statistics;
            });
            return provider.Task;
        });
        var query = session.QueryAsync(source.Extent!.Value, 2, 1, parentCancellation.Token);
        var cancelling = Task.Run(() => parentCancellation.Cancel());
        Thread? completing = null;
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            completing = new Thread(() => provider.SetResult(source.Query(source.Extent.Value, 2))) { IsBackground = true };
            completing.Start();
            Assert.True(SpinWait.SpinUntil(() =>
                (completing.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
            Assert.Equal(1, await Task.Run(() => session.InFlightCount).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, store.Statistics.QueryLeases);
            Assert.False(query.IsCompleted);
            var stop = session.StopAsync();
            Assert.False(stop.IsCompleted);
            releaseCallback.Set();
            await Task.WhenAll(query, cancelling, stop).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(completing.Join(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, store.Statistics.ActiveLeases);
            AssertCounters(session, accepted: 1, aborted: 1);
        }
        finally
        {
            releaseCallback.Set();
            if (completing is null) provider.TrySetResult(source.Query(source.Extent!.Value, 2));
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task EmptyWindowAndGapSelectionNeverInventRawHits()
    {
        using var store = NewStore();
        var source = new HistorySeries([new HistorySample(1, 5, null)], 1);
        Publish(store, source);
        await using var session = new HistoryQuerySession(store);
        await session.QueryAsync(new TimeWindow(5, 6), 0, 1);
        Assert.Equal(HistorySelectionStatus.NotFound, session.TryFindNearest(5).Status);
        Assert.Equal(1, session.TryFindNearest(5, includeGaps: true).Sample!.Value.Key);
        await session.QueryAsync(new TimeWindow(6, 6), 0, 1);
        Assert.Equal(HistorySelectionStatus.NotFound, session.TryFindNearest(5, includeGaps: true).Status);
    }

    [Fact]
    public async Task LegacyProviderRemainsBorrowedAndStopAlsoClearsItsAppliedDisplay()
    {
        using var provider = new BorrowedProvider();
        await using var session = new HistoryQuerySession(provider.QueryAsync);
        await session.QueryAsync(new TimeWindow(0, 10), 2, 1);
        Assert.NotNull(session.CurrentSnapshot);
        Assert.Equal(HistorySelectionStatus.SourceUnavailable, session.TryFindNearest(5).Status);
        await session.StopAsync();
        Assert.Null(session.CurrentSnapshot);
        Assert.False(provider.IsDisposed);
        AssertCounters(session, accepted: 1, applied: 1);
    }

    private static VersionedSnapshotStore<HistorySeries> NewStore(int maximumVersions = 3, int maximumLeases = 8) =>
        new(new SnapshotBudget(maximumVersions, 100, maximumLeases));

    private static HistorySeries Series(long version) => new(Enumerable.Range(0, 10)
        .Select(index => new HistorySample(version * 100 + index, index, index)), version);

    private static void Publish(VersionedSnapshotStore<HistorySeries> store, HistorySeries source) =>
        Assert.Equal(SnapshotPublishStatus.Published, store.Publish(source.SourceVersion, source, source.Count));

    private static TaskCompletionSource<HistoryChartSnapshot> NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertCounters(HistoryQuerySession session, long accepted = 0, long applied = 0,
        long aborted = 0, long failed = 0, int inFlight = 0, long rejected = 0)
    {
        var statistics = session.Statistics;
        Assert.Equal(new HistoryQueryStatistics(accepted, applied, aborted, failed, inFlight, rejected), statistics);
        Assert.True(statistics.IsConserved);
        Assert.Equal(statistics.Accepted, statistics.Applied + statistics.Aborted + statistics.Failed + statistics.InFlight);
    }

    private sealed class BorrowedProvider : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public Task<HistoryChartSnapshot> QueryAsync(HistoryQueryRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Series(1).Query(request.Window, request.PointBudget, cancellationToken));
        public void Dispose() => IsDisposed = true;
    }
}
