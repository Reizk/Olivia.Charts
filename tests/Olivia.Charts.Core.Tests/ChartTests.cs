using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class ChartTests
{
    [Fact]
    public void SeriesCopiesInputAndSortsByTimestampThenStableKey()
    {
        var input = new[] { new HistorySample(7, 10, 7), new HistorySample(2, 10, 2), new HistorySample(4, 5, 4) };
        var series = new HistorySeries(input, 19);
        input[0] = new HistorySample(99, 0, 999);
        Assert.Equal(new long[] { 4, 2, 7 }, series.Samples.Select(sample => sample.Key));
        Assert.Equal(19, series.SourceVersion);
        Assert.Equal(new TimeWindow(5, 11), series.Extent);
        var copy = series.Samples.ToArray();
        copy[0] = input[0];
        Assert.Equal(4, series.Samples[0].Key);
        var window = series.ReadWindow(new TimeWindow(0, 20));
        Assert.Equal(series.Samples.ToArray(), window.Samples.ToArray());
    }

    [Fact]
    public void DuplicateKeysAreRejectedEvenAcrossDifferentTimes()
    {
        Assert.Throws<ArgumentException>(() => new HistorySeries([
            new HistorySample(4, 1, 1), new HistorySample(4, 20, 2)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistorySeries([], -1));
        var series = new HistorySeries([new HistorySample(long.MinValue, 0, 1), new HistorySample(long.MaxValue, 0, 2)]);
        Assert.Equal(long.MinValue, series.FindNearest(0)!.Value.Key);
    }

    [Fact]
    public void UtcDomainAllowsLastSampleAndRejectsOverflowInputs()
    {
        var last = DateTime.MaxValue.Ticks;
        var series = new HistorySeries([new HistorySample(1, last, 3)]);
        Assert.Equal(TimeWindow.MaximumExclusiveTicks, series.Extent!.Value.EndTicks);
        Assert.Single(series.ReadWindow(series.Extent.Value).Samples);
        Assert.Empty(series.ReadWindow(new TimeWindow(last, last)).Samples);
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistorySample(1, -1, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistorySample(1, long.MaxValue, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeWindow(0, long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeWindow(5, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => series.FindNearest(long.MinValue));
    }

    [Fact]
    public void EmptySourceAndEmptyWindowHaveNoFabricatedPoints()
    {
        var empty = new HistorySeries([]);
        Assert.Null(empty.Extent);
        Assert.Null(empty.FindNearest(0));
        var snapshot = empty.Query(new TimeWindow(0, 0), 0);
        Assert.Equal(ChartBudgetStatus.Satisfied, snapshot.BudgetStatus);
        Assert.Equal(0, snapshot.PointCount);
        Assert.Empty(snapshot.Segments);
        Assert.Empty(snapshot.Gaps);
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.Query(default, -1));
    }

    [Fact]
    public void HalfOpenWindowKeepsAllStartDuplicatesAndExcludesAllEndDuplicates()
    {
        var series = new HistorySeries([
            new HistorySample(1, 10, 1), new HistorySample(2, 10, 2),
            new HistorySample(3, 20, 3), new HistorySample(4, 20, 4)]);
        Assert.Equal(new long[] { 1, 2 }, series.ReadWindow(new TimeWindow(10, 20)).Samples.Select(sample => sample.Key));
        Assert.Empty(series.ReadWindow(new TimeWindow(11, 20)).Samples);
        Assert.Null(series.FindNearest(10, new TimeWindow(11, 20)));
    }

    [Fact]
    public void FiniteRunsKeepBoundariesAndExtremaAndDoNotCrossAnyGap()
    {
        var series = MakeSeries(4, 9, -7, 3, null, double.NaN, double.PositiveInfinity, 8, -5, 20, 2);
        var view = series.Query(series.Extent!.Value, 8);
        Assert.Equal(ChartBudgetStatus.Satisfied, view.BudgetStatus);
        Assert.Equal(8, view.RequiredPointBudget);
        Assert.Equal(8, view.PointCount);
        Assert.Equal(2, view.Segments.Length);
        Assert.Equal(new long[] { 0, 1, 2, 3 }, view.Segments[0].Points.Select(point => point.Key));
        Assert.Equal(new long[] { 7, 8, 9, 10 }, view.Segments[1].Points.Select(point => point.Key));
        var gap = Assert.Single(view.Gaps);
        Assert.Equal(3, gap.SampleCount);
        Assert.Equal(HistoryGapReason.Missing | HistoryGapReason.NotANumber | HistoryGapReason.Infinite, gap.Reasons);
        Assert.Equal(4, gap.RawStartIndex);
        Assert.Equal(7, gap.RawEndIndexExclusive);
        Assert.True(view.Segments[0].RawEndIndexExclusive <= gap.RawStartIndex);
        Assert.True(view.Segments[1].RawStartIndex >= gap.RawEndIndexExclusive);
    }

    [Fact]
    public void InsufficientBudgetIsExplicitAndNeverProducesMisleadingPartialLine()
    {
        var series = MakeSeries(2, 9, -8, 4, null, 6);
        var view = series.Query(series.Extent!.Value, 4);
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, view.BudgetStatus);
        Assert.Equal(5, view.RequiredPointBudget);
        Assert.Empty(view.Segments);
        Assert.Equal(0, view.PointCount);
        Assert.Single(view.Gaps);
        var zeroBudget = series.Query(series.Extent.Value, 0);
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, zeroBudget.BudgetStatus);
    }

    [Fact]
    public void AllGapsNeedNoFinitePointBudgetAndRetainInvalidOriginalValues()
    {
        var series = MakeSeries(null, double.NaN, double.NegativeInfinity);
        var view = series.Query(series.Extent!.Value, 0);
        Assert.Equal(ChartBudgetStatus.Satisfied, view.BudgetStatus);
        Assert.Equal(0, view.RequiredPointBudget);
        Assert.Equal(3, Assert.Single(view.Gaps).SampleCount);
        Assert.Null(series.FindNearest(1));
        Assert.True(double.IsNaN(series.FindNearest(1, includeGaps: true)!.Value.Value!.Value));
    }

    [Fact]
    public void ExactHitUsesRawPointEvenWhenDisplayOmitsIt()
    {
        var series = MakeSeries(0, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        var view = series.Query(series.Extent!.Value, 2);
        Assert.Equal(new long[] { 0, 9 }, view.Segments[0].Points.Select(point => point.Key));
        Assert.Equal(5, series.FindNearest(5)!.Value.Key);
        Assert.Equal(5d, series.FindNearest(5)!.Value.Value!.Value);
    }

    [Fact]
    public void ExactHitTieBreaksTimestampThenStableKey()
    {
        var series = new HistorySeries([
            new HistorySample(5, 10, 1), new HistorySample(2, 10, 2), new HistorySample(1, 20, 3)]);
        Assert.Equal(2, series.FindNearest(15)!.Value.Key);
        Assert.Equal(2, series.FindNearest(10)!.Value.Key);
        Assert.Equal(1, series.FindNearest(15, new TimeWindow(11, 21))!.Value.Key);
    }

    [Fact]
    public void SeededWindowsAndSamplingObeyBudgetOrderingRunsBoundariesAndExtrema()
    {
        var random = new Random(7919);
        for (var trial = 0; trial < 150; trial++)
        {
            var input = Enumerable.Range(0, random.Next(1, 400)).Select(index =>
                new HistorySample(index, random.Next(0, 1000), RandomValue(random))).ToArray();
            var series = new HistorySeries(input, trial);
            var start = random.Next(0, 1001);
            var window = new TimeWindow(start, random.Next(start, 1002));
            var oracle = series.Samples.Where(sample => window.Contains(sample.UtcTicks)).ToArray();
            Assert.Equal(oracle, series.ReadWindow(window).Samples.ToArray());
            var budget = random.Next(0, 150);
            var view = series.Query(window, budget);
            Assert.Equal(oracle.Length, view.RawSampleCount);
            Assert.InRange(view.PointCount, 0, budget);
            Assert.Equal(trial, view.SourceVersion);
            var repeated = series.Query(window, budget);
            Assert.Equal(view.Segments.SelectMany(segment => segment.Points).ToArray(),
                repeated.Segments.SelectMany(segment => segment.Points).ToArray());
            Assert.Equal(view.Gaps.ToArray(), repeated.Gaps.ToArray());
            if (view.BudgetStatus == ChartBudgetStatus.InsufficientBudget)
            {
                Assert.True(view.RequiredPointBudget > budget);
                Assert.Empty(view.Segments);
                continue;
            }
            Assert.Equal(Math.Min(budget, oracle.Count(sample => !sample.IsGap)), view.PointCount);
            var coveredFinite = new HashSet<int>();
            foreach (var segment in view.Segments)
            {
                var raw = series.Samples.Skip(segment.RawStartIndex)
                    .Take(segment.RawEndIndexExclusive - segment.RawStartIndex).ToArray();
                Assert.NotEmpty(raw);
                Assert.All(raw, sample => Assert.False(sample.IsGap));
                Assert.Equal(raw[0].Key, segment.Points[0].Key);
                Assert.Equal(raw[^1].Key, segment.Points[^1].Key);
                Assert.Contains(raw.Min(sample => sample.Value!.Value), segment.Points.Select(point => point.Value));
                Assert.Contains(raw.Max(sample => sample.Value!.Value), segment.Points.Select(point => point.Value));
                Assert.Equal(segment.Points.Select(point => point.RawIndex).Order(), segment.Points.Select(point => point.RawIndex));
                Assert.Equal(segment.Points.Length, segment.Points.Select(point => point.Key).Distinct().Count());
                foreach (var point in segment.Points)
                {
                    Assert.Equal(series.Samples[point.RawIndex], point.Sample);
                    Assert.True(window.Contains(point.UtcTicks));
                }
                for (var index = segment.RawStartIndex; index < segment.RawEndIndexExclusive; index++)
                    Assert.True(coveredFinite.Add(index));
            }
            Assert.Equal(oracle.Count(sample => !sample.IsGap), coveredFinite.Count);
            Assert.Equal(oracle.Count(sample => sample.IsGap), view.Gaps.Sum(gap => gap.SampleCount));
        }
    }

    [Fact]
    public void SeededExactHitMatchesFullScanOracleWithGapsDuplicatesAndWindows()
    {
        var random = new Random(65537);
        for (var trial = 0; trial < 100; trial++)
        {
            var source = Enumerable.Range(0, 200).Select(index =>
                new HistorySample(index - 100, random.Next(0, 500), RandomValue(random))).ToArray();
            var series = new HistorySeries(source);
            for (var probe = 0; probe < 25; probe++)
            {
                var start = random.Next(0, 501);
                var window = new TimeWindow(start, random.Next(start, 502));
                var target = random.Next(0, 550);
                foreach (var includeGaps in new[] { false, true })
                {
                    var candidates = source.Where(sample => window.Contains(sample.UtcTicks) && (includeGaps || !sample.IsGap))
                        .OrderBy(sample => Math.Abs(sample.UtcTicks - target)).ThenBy(sample => sample.UtcTicks).ThenBy(sample => sample.Key).ToArray();
                    HistorySample? expected = candidates.Length == 0 ? null : candidates[0];
                    Assert.Equal(expected, series.FindNearest(target, window, includeGaps));
                }
            }
        }
    }

    [Fact]
    public void PanAndZoomAreBoundedAndResistOverflowOrDegenerateExtents()
    {
        var extent = new TimeWindow(100, 1000);
        var window = new TimeWindow(300, 500);
        Assert.Equal(new TimeWindow(800, 1000), window.Pan(long.MaxValue, extent));
        Assert.Equal(new TimeWindow(100, 300), window.Pan(long.MinValue, extent));
        Assert.Equal(new TimeWindow(350, 450), window.Zoom(2, 400, extent));
        Assert.Equal(extent, window.Zoom(double.Epsilon, 400, extent));
        Assert.Equal(1, window.Zoom(double.MaxValue, 400, extent).DurationTicks);
        Assert.Equal(new TimeWindow(500, 500), window.Zoom(2, 400, new TimeWindow(500, 500)));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.Zoom(0, 400, extent));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.Zoom(double.NaN, 400, extent));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.Zoom(double.PositiveInfinity, 400, extent));
        var edge = new TimeWindow(DateTime.MaxValue.Ticks - 5, TimeWindow.MaximumExclusiveTicks);
        Assert.Equal(edge, edge.Pan(long.MaxValue, edge));
        Assert.Equal(1, edge.Zoom(double.MaxValue, DateTime.MaxValue.Ticks, edge).DurationTicks);
    }

    [Fact]
    public void SeededPanZoomAlwaysStayInsideExtent()
    {
        var random = new Random(104729);
        var extent = new TimeWindow(0, TimeWindow.MaximumExclusiveTicks);
        var current = extent;
        for (var i = 0; i < 500; i++)
        {
            current = current.Zoom(Math.Pow(10, random.NextDouble() * 6 - 3),
                random.NextInt64(0, TimeWindow.MaximumExclusiveTicks), extent);
            current = current.Pan(random.NextInt64(long.MinValue, long.MaxValue), extent);
            Assert.InRange(current.StartTicks, extent.StartTicks, extent.EndTicks - 1);
            Assert.InRange(current.EndTicks, current.StartTicks + 1, extent.EndTicks);
        }
    }

    [Fact]
    public void ZoomIdentityPreservesLargeTicksAndEmptyWindowsExactly()
    {
        var extent = new TimeWindow(0, TimeWindow.MaximumExclusiveTicks);
        var window = new TimeWindow(1000, 1000 + 9007199254740993L);
        Assert.Equal(window, window.Zoom(1d, 1000, extent));
        Assert.Equal(window, window.Zoom(1d, window.EndTicks, extent));
        var empty = new TimeWindow(DateTime.MaxValue.Ticks, DateTime.MaxValue.Ticks);
        Assert.Equal(empty, empty.Zoom(1d, 0, extent));
        var restricted = new TimeWindow(2000, window.EndTicks);
        Assert.Equal(window.ClampTo(restricted), window.Zoom(1d, 1000, restricted));
    }

    [Fact]
    public void SeededZoomIdentityNeverRoundsAnyTickAtAnyAnchor()
    {
        var random = new Random(130363);
        var extent = new TimeWindow(0, TimeWindow.MaximumExclusiveTicks);
        for (var i = 0; i < 300; i++)
        {
            var start = random.NextInt64(0, TimeWindow.MaximumExclusiveTicks);
            var end = random.NextInt64(start, TimeWindow.MaximumExclusiveTicks);
            var window = new TimeWindow(start, end);
            Assert.Equal(window, window.Zoom(1d, random.NextInt64(), extent));
        }
    }

    [Fact]
    public void ZoomUsesExactScaleRatioForLargeDurationsAndAdjacentToOneScales()
    {
        var extent = new TimeWindow(0, TimeWindow.MaximumExclusiveTicks);
        var window = new TimeWindow(1000, 1000 + 9007199254740993L);
        Assert.Equal(18014398509481986L, window.Zoom(0.5d, 1000, extent).DurationTicks);
        Assert.Equal(3002399751580331L, window.Zoom(3d, 1000, extent).DurationTicks);
        Assert.Equal(9007199254740991L, window.Zoom(Math.BitIncrement(1d), 1000, extent).DurationTicks);
        Assert.Equal(9007199254740994L, window.Zoom(Math.BitDecrement(1d), 1000, extent).DurationTicks);
    }

    [Fact]
    public void ZoomRoundsLargeAnchorPositionUsingExactIntegerRatio()
    {
        var extent = new TimeWindow(0, TimeWindow.MaximumExclusiveTicks);
        const long duration = 9007199254740993L;
        var window = new TimeWindow(1000, 1000 + duration);
        var result = window.Zoom(2d, window.EndTicks - 1, extent);
        // Duration/2 is halfway and rounds to the even integer. The anchor offset is
        // just above that integer minus one half, so it rounds to the full new duration.
        const long expectedDuration = 4503599627370496L;
        Assert.Equal(expectedDuration, result.DurationTicks);
        Assert.Equal(window.EndTicks - 1 - expectedDuration, result.StartTicks);
        Assert.Equal(window.EndTicks - 1, result.EndTicks);
    }

    [Fact]
    public void ZoomDurationAndAnchorHalfTicksUseNearestEvenRounding()
    {
        var extent = new TimeWindow(0, 100);
        Assert.Equal(new TimeWindow(10, 12), new TimeWindow(10, 15).Zoom(2d, 10, extent));
        Assert.Equal(new TimeWindow(10, 14), new TimeWindow(10, 17).Zoom(2d, 10, extent));
        Assert.Equal(new TimeWindow(11, 13), new TimeWindow(10, 14).Zoom(2d, 11, extent));
        Assert.Equal(new TimeWindow(11, 13), new TimeWindow(10, 14).Zoom(2d, 13, extent));
    }

    [Fact]
    public void PreCancelledRawAndDisplayQueriesThrowCancellation()
    {
        var series = MakeSeries(1, 2, 3);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => series.Query(series.Extent!.Value, 2, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => series.ReadWindow(series.Extent!.Value, cancellation.Token));
    }

    [Fact]
    public async Task LateCompletionIgnoringCancellationCannotReplaceLatestSnapshot()
    {
        var series = MakeSeries(1, 2, 3);
        var oldGate = NewGate();
        var newGate = NewGate();
        await using var session = new HistoryQuerySession((request, _) => request.Revision == 1 ? oldGate.Task : newGate.Task);
        var window = series.Extent!.Value;
        var oldRequest = session.QueryAsync(window, 2, 0);
        var newRequest = session.QueryAsync(window, 3, 0);
        var newer = series.Query(window, 3);
        newGate.SetResult(newer);
        Assert.Equal(HistoryQueryStatus.Applied, (await newRequest).Status);
        oldGate.SetResult(series.Query(window, 2));
        Assert.Equal(HistoryQueryStatus.Superseded, (await oldRequest).Status);
        Assert.Same(newer, session.CurrentSnapshot);
        Assert.True(session.IsCurrent);
        Assert.Equal(0, session.InFlightCount);
    }

    [Fact]
    public async Task QuerySessionBoundsInflightAndReportsBusyWithoutQueuing()
    {
        var series = MakeSeries(1, 2, 3);
        var gate = NewGate();
        var starts = 0;
        await using var session = new HistoryQuerySession((_, _) => { starts++; return gate.Task; }, 1);
        var pending = session.QueryAsync(series.Extent!.Value, 3, 0);
        for (var i = 0; i < 100; i++)
            Assert.Equal(HistoryQueryStatus.Busy, (await session.QueryAsync(series.Extent.Value, 3, 0)).Status);
        Assert.Equal(1, starts);
        Assert.Equal(1, session.InFlightCount);
        gate.SetResult(series.Query(series.Extent.Value, 3));
        Assert.Equal(HistoryQueryStatus.Applied, (await pending).Status);
    }

    [Fact]
    public async Task QuerySessionRejectsMismatchedSourceAndQueryVersions()
    {
        var series = new HistorySeries([new HistorySample(1, 1, 1)], 4);
        await using var session = new HistoryQuerySession((request, _) => Task.FromResult(series.Query(request.Window, request.PointBudget)));
        var result = await session.QueryAsync(series.Extent!.Value, 1, 3);
        Assert.Equal(HistoryQueryStatus.VersionMismatch, result.Status);
        Assert.Null(session.CurrentSnapshot);
        Assert.False(session.IsCurrent);
        await using var wrongWindow = new HistoryQuerySession((_, _) => Task.FromResult(series.Query(new TimeWindow(0, 10), 1)));
        Assert.Equal(HistoryQueryStatus.Failed, (await wrongWindow.QueryAsync(series.Extent.Value, 1, 4)).Status);
    }

    [Fact]
    public async Task CancellationAndFaultsRemainDistinctAndInvalidationMarksSnapshotStale()
    {
        var series = MakeSeries(1, 2, 3);
        await using var good = new HistoryQuerySession((request, _) => Task.FromResult(series.Query(request.Window, request.PointBudget)));
        Assert.Equal(HistoryQueryStatus.Applied, (await good.QueryAsync(series.Extent!.Value, 3, 0)).Status);
        good.Invalidate();
        Assert.False(good.IsCurrent);
        Assert.NotNull(good.CurrentSnapshot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(HistoryQueryStatus.Cancelled, (await good.QueryAsync(series.Extent.Value, 3, 0, cancellation.Token)).Status);
        var expected = new InvalidOperationException("fixture fault");
        await using var faulty = new HistoryQuerySession((_, _) => Task.FromException<HistoryChartSnapshot>(expected));
        var result = await faulty.QueryAsync(series.Extent.Value, 3, 0);
        Assert.Equal(HistoryQueryStatus.Failed, result.Status);
        Assert.Same(expected, result.Error);
    }

    [Fact]
    public async Task StopWaitsForIgnoredCancellationAndRejectsNewReads()
    {
        var series = MakeSeries(1, 2, 3);
        var gate = NewGate();
        await using var session = new HistoryQuerySession((_, _) => gate.Task);
        var query = session.QueryAsync(series.Extent!.Value, 3, 0);
        var stop = session.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.Equal(HistoryQueryStatus.Stopped, (await session.QueryAsync(series.Extent.Value, 3, 0)).Status);
        gate.SetResult(series.Query(series.Extent.Value, 3));
        await stop;
        Assert.Equal(HistoryQueryStatus.Stopped, (await query).Status);
        Assert.Null(session.CurrentSnapshot);
        await session.StopAsync();
    }

    [Fact]
    public async Task CancelledStopWaitDoesNotPretendProviderHasTerminated()
    {
        var series = MakeSeries(1);
        var gate = NewGate();
        await using var session = new HistoryQuerySession((_, _) => gate.Task);
        var query = session.QueryAsync(series.Extent!.Value, 1, 0);
        using var cancellation = new CancellationTokenSource();
        var stop = session.StopAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        Assert.Equal(1, session.InFlightCount);
        gate.SetResult(series.Query(series.Extent.Value, 1));
        await query;
        await session.StopAsync();
        Assert.Equal(0, session.InFlightCount);
    }

    [Fact]
    public async Task IgnoredExternalCancellationCannotCommitCurrentResult()
    {
        var series = MakeSeries(1);
        var gate = NewGate();
        await using var session = new HistoryQuerySession((_, _) => gate.Task);
        using var cancellation = new CancellationTokenSource();
        var query = session.QueryAsync(series.Extent!.Value, 1, 0, cancellation.Token);
        cancellation.Cancel();
        gate.SetResult(series.Query(series.Extent.Value, 1));
        Assert.Equal(HistoryQueryStatus.Cancelled, (await query).Status);
        Assert.Null(session.CurrentSnapshot);
    }

    [Fact]
    public async Task InvalidatedRequestCannotCommitWithoutReplacementRequest()
    {
        var series = MakeSeries(1);
        var gate = NewGate();
        await using var session = new HistoryQuerySession((_, _) => gate.Task);
        var query = session.QueryAsync(series.Extent!.Value, 1, 0);
        session.Invalidate();
        gate.SetResult(series.Query(series.Extent.Value, 1));
        Assert.Equal(HistoryQueryStatus.Superseded, (await query).Status);
        Assert.Null(session.CurrentSnapshot);
    }

    [Fact]
    public async Task ThrowingProviderCancellationCallbackDoesNotAbandonAcceptedRequests()
    {
        var series = MakeSeries(1);
        var oldGate = NewGate();
        await using var session = new HistoryQuerySession((request, token) =>
        {
            if (request.Revision != 1) return Task.FromResult(series.Query(request.Window, request.PointBudget));
            token.Register(() => throw new InvalidOperationException("fixture cancellation callback"));
            return oldGate.Task;
        });
        var old = session.QueryAsync(series.Extent!.Value, 1, 0);
        var current = session.QueryAsync(series.Extent.Value, 1, 0);
        Assert.Equal(HistoryQueryStatus.Applied, (await current).Status);
        Assert.IsType<AggregateException>(session.CancellationError);
        oldGate.SetResult(series.Query(series.Extent.Value, 1));
        Assert.Equal(HistoryQueryStatus.Superseded, (await old).Status);
        Assert.Equal(0, session.InFlightCount);
    }

    [Fact]
    public async Task LinkedCancellationCleanupDoesNotHoldSessionLockAndStopAwaitsIt()
    {
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var letCallbackReadState = new ManualResetEventSlim(false);
        // Intentionally inline continuations: the dedicated completing thread enters cleanup
        // synchronously, where linked-source disposal waits for the gated cancellation callback.
        var providerResult = new TaskCompletionSource<HistoryChartSnapshot>();
        using var parentCancellation = new CancellationTokenSource();
        HistoryQuerySession session = null!;
        session = new HistoryQuerySession((request, token) =>
        {
            token.Register(() =>
            {
                callbackEntered.TrySetResult();
                letCallbackReadState.Wait();
                _ = session.Revision;
            });
            return providerResult.Task;
        });
        var series = MakeSeries(1);
        var query = session.QueryAsync(series.Extent!.Value, 1, 0, parentCancellation.Token);
        var cancellation = Task.Run(() => parentCancellation.Cancel());
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completingThread = new Thread(() => providerResult.SetResult(series.Query(series.Extent.Value, 1)))
        {
            IsBackground = true
        };
        completingThread.Start();
        try
        {
            // This thread has no other blocking operations: reaching WaitSleepJoin proves
            // disposal is waiting for the callback, without a timing-dependent sleep.
            Assert.True(SpinWait.SpinUntil(() =>
                (completingThread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
            Assert.False(query.IsCompleted);
            Assert.Equal(1, await Task.Run(() => session.InFlightCount).WaitAsync(TimeSpan.FromSeconds(5)));
            var stop = session.StopAsync();
            Assert.False(stop.IsCompleted);
            letCallbackReadState.Set();
            await Task.WhenAll(query, cancellation, stop).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(completingThread.Join(TimeSpan.FromSeconds(5)));
            Assert.Equal(HistoryQueryStatus.Cancelled, (await query).Status);
            Assert.Equal(0, session.InFlightCount);
            await session.DisposeAsync();
        }
        finally
        {
            letCallbackReadState.Set();
        }
    }

    private static HistorySeries MakeSeries(params double?[] values) =>
        new(values.Select((value, index) => new HistorySample(index, index, value)));

    private static double? RandomValue(Random random) => random.Next(0, 15) switch
    {
        0 => null,
        1 => double.NaN,
        2 => double.PositiveInfinity,
        3 => double.NegativeInfinity,
        _ => random.Next(-1000, 1001)
    };

    private static TaskCompletionSource<HistoryChartSnapshot> NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
