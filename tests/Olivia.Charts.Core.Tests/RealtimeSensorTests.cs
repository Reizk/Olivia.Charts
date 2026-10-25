using System.Collections.Concurrent;
using System.Collections.Immutable;
using Olivia.Core.Charts;
using Olivia.Core.Workspaces;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class RealtimeSensorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Official_scale_is_ten_thousand_eight_channel_rows_with_four_shared_time_panes()
    {
        var queue = new Queue<Action>();
        RealtimeSensorFrame? frame = null;
        await using var session = new RealtimeSensorSession(queue.Enqueue, value => frame = value,
            timeProvider: new ManualClock());
        session.Initialize();
        Assert.True(session.PublishTick());
        Drain(queue);
        Assert.NotNull(frame);
        Assert.Equal(10000, session.Statistics.RetainedRows);
        Assert.Equal(10000L, frame.SourceVersion);
        Assert.Equal(TimeSpan.FromSeconds(14).Ticks, frame.Window.DurationTicks);
        Assert.Equal(TimeSpan.FromMilliseconds(15), session.Options.ProductionInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(15), session.Options.PublicationInterval);
        Assert.Equal(934, frame.RawRows.Length);
        Assert.Equal(new[] { "Temperature", "Pressure", "Power", "Intensity" }, frame.Panes.Select(pane => pane.Name));
        Assert.Equal(8, frame.Series.Length);
        Assert.All(frame.Series, series =>
        {
            Assert.Equal(frame.SourceVersion, series.SourceVersion);
            Assert.Equal(frame.Window, series.Window);
            Assert.Equal(934, series.RawSampleCount);
            Assert.Equal(ChartBudgetStatus.Satisfied, series.BudgetStatus);
        });
        Assert.All(frame.Panes, pane =>
        {
            Assert.Equal(2, pane.Series.Length);
            Assert.Equal(frame.Window, pane.Geometry.Window);
            Assert.Equal(frame.SourceVersion, pane.Geometry.SourceVersion);
            Assert.All(pane.Series.SelectMany(series => series.Nodes), node =>
                Assert.Equal(pane.Geometry.Normalize(node.UtcTicks, node.Value), node.Position));
        });
        var old = frame;
        session.ProduceTick(); session.PublishTick(); Drain(queue);
        Assert.Equal(10000, session.Statistics.RetainedRows);
        Assert.Equal(1L, session.Statistics.EvictedRows);
        Assert.Equal(10001L, frame!.SourceVersion);
        Assert.Equal(10000L, old.RawRows[^1].Key);
        Assert.Equal(10000L, old.FindNearest(7, Epoch.UtcTicks)!.Value.Key);
    }

    [Fact]
    public async Task Original_extrema_gaps_and_omitted_point_readback_have_an_independent_oracle()
    {
        var queue = new Queue<Action>();
        RealtimeSensorFrame? frame = null;
        await using var session = new RealtimeSensorSession(queue.Enqueue, value => frame = value,
            new(initialSampleCount: 0, pointBudget: 9), new ManualClock());
        session.Initialize();
        double?[] values = [-5, 0, 18, 1, null, 6, -50, 7, double.PositiveInfinity, 0, 3, 4, -20];
        for (var i = 0; i < values.Length; i++) session.Append(Row(i + 1, i, values[i]));
        session.SetWindow(new(Epoch.UtcTicks, Epoch.AddSeconds(13).UtcTicks));
        session.PublishTick(); Drain(queue);
        var snapshot = frame!.Series[0];
        Assert.Equal(new long[] { 1, 3, 4, 6, 7, 8, 10, 12, 13 },
            snapshot.Segments.SelectMany(segment => segment.Points).Select(point => point.Key));
        Assert.Equal(3, snapshot.Segments.Length);
        Assert.Equal(9, snapshot.RequiredPointBudget);
        Assert.Equal(2, snapshot.Gaps.Length);
        Assert.Equal(HistoryGapReason.Missing, snapshot.Gaps[0].Reasons);
        Assert.Equal(HistoryGapReason.Infinite, snapshot.Gaps[1].Reasons);
        Assert.Equal(new HistorySample(2, Epoch.AddSeconds(1).UtcTicks, 0), frame.FindNearest(0, Epoch.AddSeconds(1).UtcTicks));
        Assert.DoesNotContain(snapshot.Segments.SelectMany(segment => segment.Points), point => point.Key == 2);
        Assert.Equal(5L, frame.FindNearest(0, Epoch.AddSeconds(4).UtcTicks, includeGaps: true)!.Value.Key);
        Assert.Equal(4L, frame.FindNearest(0, Epoch.AddSeconds(4).UtcTicks)!.Value.Key);
        Assert.Equal(new HistoryPlotValueDomain(-50, 112), frame.Panes[0].Geometry.RawDomain);
        Assert.Equal(6, frame.Panes[0].Series[0].DataEdges.Length);
        Assert.Equal(3, frame.Panes[0].Series[0].Markers.Length);
        var frozen = frame;
        session.SetPointBudget(2); session.PublishTick(); Drain(queue);
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, frame!.Series[0].BudgetStatus);
        Assert.Empty(frame.Series[0].Segments);
        Assert.Equal(13, frame.RawRows.Length);
        Assert.Equal(9, frozen.Series[0].PointCount);
        Assert.Equal(-50d, frame.FindNearest(0, Epoch.AddSeconds(6).UtcTicks)!.Value.Value);
    }

    [Fact]
    public async Task Publication_pause_keeps_production_bounded_and_resume_catches_up_without_raw_backlog()
    {
        var queue = new Queue<Action>();
        RealtimeSensorFrame? frame = null;
        await using var session = new RealtimeSensorSession(queue.Enqueue, value => frame = value,
            new(capacity: 4, initialSampleCount: 0), new ManualClock());
        session.Initialize();
        for (var i = 0; i < 4; i++) session.Append(Row(i + 1, i, i));
        session.PublishTick(); Drain(queue);
        var frozen = frame;
        session.PausePublication();
        for (var i = 4; i < 40; i++) session.Append(Row(i + 1, i, i));
        Assert.False(session.PublishTick());
        Assert.Equal(4, session.Statistics.RetainedRows);
        Assert.Equal(36L, session.Statistics.EvictedRows);
        Assert.Equal(40L, session.Statistics.ProducedRows);
        Assert.Equal(1L, session.Statistics.PublishedFrames);
        Assert.Same(frozen, frame);
        // An explicit viewport edit during official pause uses only the frozen published source.
        session.SetWindow(new(Epoch.AddSeconds(1).UtcTicks, Epoch.AddSeconds(3).UtcTicks));
        Assert.True(session.PublishTick()); Drain(queue);
        Assert.Equal(4L, frame!.SourceVersion);
        Assert.Equal(new long[] { 2, 3 }, frame.RawRows.Select(row => row.Key));
        session.ResumePublication(); session.FollowLatest(); session.PublishTick(); Drain(queue);
        Assert.Equal(40L, frame.SourceVersion);
        Assert.Equal(new long[] { 39, 40 }, frame.RawRows.Select(row => row.Key));
        Assert.Equal(3L, frozen!.FindNearest(0, Epoch.AddSeconds(2).UtcTicks)!.Value.Key);
    }

    [Fact]
    public async Task Display_pause_coalesces_only_frames_and_production_pause_stops_acquisitions()
    {
        var queue = new Queue<Action>();
        RealtimeSensorFrame? frame = null;
        await using var session = new RealtimeSensorSession(queue.Enqueue, value => frame = value,
            new(initialSampleCount: 0), new ManualClock());
        session.Initialize(); session.PauseDisplay();
        for (var i = 0; i < 30; i++) { session.ProduceTick(); session.PublishTick(); }
        Assert.Null(frame);
        Assert.Empty(queue);
        Assert.Equal(30L, session.Statistics.ProducedRows);
        Assert.Equal(30, session.Statistics.RetainedRows);
        Assert.Equal(1, session.Statistics.PendingFrames);
        Assert.Equal(29L, session.Statistics.CoalescedFrames);
        session.ResumeDisplay();
        Assert.Single(queue); Drain(queue);
        Assert.Equal(30L, frame!.SourceVersion);
        session.PauseProduction();
        Assert.False(session.ProduceTick());
        Assert.False(session.PublishTick());
        Assert.Equal(30L, session.Statistics.ProducedRows);
        session.ResumeProduction();
        Assert.True(session.ProduceTick()); session.PublishTick(); Drain(queue);
        Assert.Equal(31L, frame.SourceVersion);
    }

    [Fact]
    public async Task Manual_window_survives_eviction_and_revision_gate_rejects_old_prepared_delivery()
    {
        var queue = new Queue<Action>();
        RealtimeSensorFrame? frame = null;
        await using var session = new RealtimeSensorSession(queue.Enqueue, value => frame = value,
            new(capacity: 5, initialSampleCount: 0), new ManualClock());
        session.Initialize();
        for (var i = 0; i < 5; i++) session.Append(Row(i + 1, i, i));
        session.PublishTick();
        var manual = new TimeWindow(Epoch.AddSeconds(1).UtcTicks, Epoch.AddSeconds(3).UtcTicks);
        session.SetWindow(manual);
        Drain(queue);
        Assert.Null(frame);
        Assert.Equal(1L, session.Statistics.DiscardedFrames);
        session.PublishTick(); Drain(queue);
        Assert.Equal(manual, frame!.Window);
        for (var i = 5; i < 12; i++) session.Append(Row(i + 1, i, i));
        session.PublishTick(); Drain(queue);
        Assert.Equal(manual, frame.Window);
        Assert.False(frame.IsFollowingLatest);
        Assert.True(frame.IsWindowOutsideRetention);
        Assert.Empty(frame.RawRows);
        session.FollowLatest(); session.PublishTick(); Drain(queue);
        Assert.True(frame.IsFollowingLatest);
        Assert.Equal(manual.DurationTicks, frame.Window.DurationTicks);
        Assert.NotEmpty(frame.RawRows);
    }

    [Fact]
    public async Task Zoom_is_bounded_at_fifty_seconds_and_same_time_ties_keep_the_original_lowest_key()
    {
        var queue = new Queue<Action>();
        RealtimeSensorFrame? frame = null;
        await using var session = new RealtimeSensorSession(queue.Enqueue, value => frame = value,
            new(initialSampleCount: 0), new ManualClock());
        session.Initialize();
        session.Append(Row(50, 0, 20));
        session.Append(Row(70, 0, 30));
        session.Append(Row(90, 150, 40));
        Assert.Throws<ArgumentException>(() => session.Append(Row(90, 151, 90)));
        Assert.Throws<ArgumentException>(() => session.Append(Row(100, 149, 90)));
        Assert.Equal(3L, session.Statistics.ProducedRows);
        session.SetWindow(new(Epoch.UtcTicks, Epoch.AddSeconds(1).UtcTicks));
        session.PublishTick(); Drain(queue);
        Assert.Equal(50L, frame!.FindNearest(0, Epoch.UtcTicks)!.Value.Key);
        session.Zoom(.0001, Epoch.UtcTicks); session.PublishTick(); Drain(queue);
        Assert.Equal(TimeSpan.FromSeconds(50).Ticks, frame.Window.DurationTicks);
        Assert.False(frame.IsFollowingLatest);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetWindow(new(Epoch.UtcTicks, Epoch.AddSeconds(51).UtcTicks)));
        session.Pan(long.MaxValue); session.PublishTick(); Drain(queue);
        Assert.Equal(Epoch.AddSeconds(150).UtcTicks + 1, frame.Window.EndTicks);
        session.FollowLatest(); session.ProduceTick(); session.PublishTick(); Drain(queue);
        Assert.Equal(91L, frame.RawRows[^1].Key);
        Assert.Equal(TimeSpan.FromSeconds(50).Ticks, frame.Window.DurationTicks);
    }

    [Fact]
    public async Task Background_stop_joins_and_disposes_both_timers_and_late_callbacks_cannot_reopen_a_view()
    {
        var clock = new ManualClock();
        var queue = new ConcurrentQueue<Action>();
        var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = 0;
        await using var session = new RealtimeSensorSession(action => { queue.Enqueue(action); notification.TrySetResult(); },
            _ => delivered++, new(initialSampleCount: 10), clock);
        await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await clock.BothTimersCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await notification.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, clock.LiveTimers);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        while (queue.TryDequeue(out var action)) action();
        Assert.Equal(0, delivered);
        Assert.Equal(0, clock.LiveTimers);
        Assert.True(session.Statistics.IsStopped);
        Assert.Equal(0, session.Statistics.RetainedRows);
        Assert.Equal(10L, session.Statistics.ReleasedRows);
        Assert.Equal(0, session.Statistics.PendingFrames);
        Assert.Null(session.CurrentFrame);
        Assert.False(session.ProduceTick());
        Assert.False(session.PublishTick());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
        var reopenedQueue = new Queue<Action>();
        RealtimeSensorFrame? reopened = null;
        await using var fresh = new RealtimeSensorSession(reopenedQueue.Enqueue, value => reopened = value,
            new(initialSampleCount: 1), clock);
        fresh.Initialize(); fresh.PublishTick(); Drain(reopenedQueue);
        Assert.True(fresh.Generation > session.Generation);
        Assert.Equal(fresh.Generation, reopened!.Generation);
    }

    [Fact]
    public async Task Delivery_fault_is_observable_and_reentrant_close_cannot_restore_the_old_frame()
    {
        var queue = new Queue<Action>();
        await using var failed = new RealtimeSensorSession(queue.Enqueue, _ => throw new InvalidOperationException("display broke"),
            new(initialSampleCount: 1), new ManualClock());
        failed.Initialize(); failed.PublishTick(); Drain(queue);
        Assert.IsType<InvalidOperationException>(failed.Statistics.Error);
        Assert.Null(failed.CurrentFrame);
        RealtimeSensorSession? closing = null;
        closing = new(queue.Enqueue, frame => { _ = closing!.StopAsync(); }, new(initialSampleCount: 1), new ManualClock());
        await using (closing)
        {
            closing.Initialize(); closing.PublishTick(); Drain(queue);
            Assert.True(closing.Statistics.IsStopped);
            Assert.Null(closing.CurrentFrame);
        }
    }

    [Fact]
    public async Task Background_timer_creation_fault_is_reported_and_shutdown_still_joins_workers()
    {
        var clock = new FailingClock();
        await using var session = new RealtimeSensorSession(_ => { }, _ => { },
            new(initialSampleCount: 1), clock);
        await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await clock.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<InvalidOperationException>(session.Statistics.Error);
        Assert.Equal("timer unavailable", session.Statistics.Error!.Message);
        Assert.True(session.Statistics.IsStopped);
        Assert.Equal(0, session.Statistics.RetainedRows);
        // A failed PeriodicTimer(TimeProvider) constructor on .NET 8 used to leave a
        // finalizer that crashed the process after the assertions had passed.
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private sealed class FailingClock : TimeProvider
    {
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => Epoch;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Attempted.TrySetResult();
            throw new InvalidOperationException("timer unavailable");
        }
    }

    private static RealtimeSensorRow Row(long key, int second, double? first) => new(key,
        Epoch.AddSeconds(second).UtcTicks,
        ImmutableArray.Create(first, (double?)(100 + second), 200 + second, 300 + second,
            400 + second, 500 + second, 600 + second, 700 + second));

    private static void Drain(Queue<Action> queue) { while (queue.TryDequeue(out var action)) action(); }

    private sealed class ManualClock : TimeProvider
    {
        private int _liveTimers, _createdTimers;
        public TaskCompletionSource BothTimersCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LiveTimers => Volatile.Read(ref _liveTimers);
        public override DateTimeOffset GetUtcNow() => Epoch;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _liveTimers);
            if (Interlocked.Increment(ref _createdTimers) == 2) BothTimersCreated.TrySetResult();
            return new ManualTimer(this);
        }
        private sealed class ManualTimer(ManualClock owner) : ITimer
        {
            private int _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;
            public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._liveTimers); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
