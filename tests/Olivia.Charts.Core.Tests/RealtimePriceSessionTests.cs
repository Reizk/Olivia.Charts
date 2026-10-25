using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class RealtimePriceSessionTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 22, 23, 59, 58, TimeSpan.Zero);

    [Fact]
    public async Task Initial_source_is_bounded_synthetic_deterministic_and_captured_only_when_running()
    {
        await using var first = new RealtimePriceSession(timeProvider: new ManualClock());
        await using var second = new RealtimePriceSession(timeProvider: new ManualClock());
        Assert.Null(first.CaptureFrame(force: true));
        first.Start(); second.Start();
        var frame = Assert.IsType<RealtimePriceFrame>(first.CaptureFrame());
        var other = second.CaptureFrame()!;
        Assert.Equal(240, frame.Source.Bars.Length);
        Assert.Equal(first.Generation, frame.Source.DatasetVersion);
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.Equal(0, frame.Source.SourceVersion);
        Assert.Equal(frame.Source.Bars.ToArray(), other.Source.Bars.ToArray());
        Assert.Contains("Synthetic", frame.Source.Label);
        Assert.Contains("not market data", frame.Source.Label);
        Assert.Equal(Epoch.UtcTicks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute,
            frame.Source.Bars[^1].Timestamp.UtcTicks);
        for (var index = 0; index < frame.Source.Bars.Length; index++)
        {
            var bar = frame.Source.Bars[index];
            Assert.Equal(index + 1L, bar.Key);
            Assert.Equal(TimeSpan.Zero, bar.Timestamp.Offset);
            Assert.Equal(0, bar.Timestamp.UtcTicks % TimeSpan.TicksPerMinute);
            Assert.False(bar.IsGap);
            Assert.True(bar.Volume > 0);
            if (index > 0) Assert.Equal(TimeSpan.FromMinutes(1), bar.Timestamp - frame.Source.Bars[index - 1].Timestamp);
        }
        Assert.Null(first.CaptureFrame());
        Assert.Same(frame.Source, first.CaptureFrame(force: true)!.Source);
        Assert.Equal(PriceChartSource.MaximumBars, first.Statistics.Capacity);
    }

    [Fact]
    public async Task Same_minute_replaces_immutable_tail_and_whole_UTC_minute_change_appends_once()
    {
        var clock = new ManualClock();
        await using var session = new RealtimePriceSession(4, 4, clock);
        session.Start();
        var original = session.CaptureFrame()!;
        var originalCopy = original.Source.Bars.ToArray();
        var initialTail = original.Source.Bars[^1];
        clock.Set(Epoch.AddSeconds(1));
        Assert.True(session.Tick());
        Assert.True(session.IsFrameCurrent(original)); // Background advancement alone permits the capture.
        var within = session.CaptureFrame()!;
        Assert.False(session.IsFrameCurrent(original)); // A newer captured source supersedes it.
        var tail = within.Source.Bars[^1];
        Assert.Equal(4, within.Source.Bars.Length);
        Assert.Equal(initialTail.Key, tail.Key);
        Assert.Equal(initialTail.Timestamp, tail.Timestamp);
        Assert.Equal(initialTail.Open, tail.Open);
        Assert.True(tail.High >= initialTail.High);
        Assert.True(tail.Low <= initialTail.Low);
        Assert.True(tail.Volume > initialTail.Volume);
        Assert.Equal(originalCopy.Take(3), within.Source.Bars.Take(3));

        clock.Set(Epoch.AddSeconds(2)); // UTC midnight changes the complete minute, not just local wall time.
        Assert.True(session.Tick());
        var midnight = session.CaptureFrame()!;
        Assert.Equal(tail, midnight.Source.Bars[^2]);
        Assert.Equal(5, midnight.Source.Bars[^1].Key);
        Assert.Equal(tail.Close, midnight.Source.Bars[^1].Open);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero), midnight.Source.Bars[^1].Timestamp);
        Assert.Equal(1, session.Statistics.EvictedBars);

        clock.Set(Epoch.AddHours(1).AddSeconds(2)); // Same minute-of-hour in a different hour is a NEW bar.
        Assert.True(session.Tick());
        var later = session.CaptureFrame()!;
        Assert.Equal(6, later.Source.Bars[^1].Key);
        Assert.Equal(4, later.Source.Bars.Length); // No fabricated 59-minute catch-up history.
        Assert.Equal(TimeSpan.FromHours(1), later.Source.Bars[^1].Timestamp - later.Source.Bars[^2].Timestamp);
        Assert.Equal(3, later.Source.SourceVersion);
        Assert.Equal(3, session.Statistics.ProducedTicks);
        Assert.Equal(2, session.Statistics.EvictedBars);
        Assert.Equal(originalCopy, original.Source.Bars.ToArray());
        Assert.Equal(tail, within.Source.Bars[^1]);
    }

    [Theory]
    [InlineData(4, 4, 20)]
    [InlineData(1024, 240, 1000)]
    public async Task Rolling_retention_stays_bounded_with_stable_unique_keys_and_strict_UTC_order(
        int capacity, int initialBars, int ticks)
    {
        var clock = new ManualClock();
        await using var session = new RealtimePriceSession(capacity, initialBars, clock);
        session.Start();
        var earliest = session.CaptureFrame()!;
        for (var minute = 1; minute <= ticks; minute++)
        {
            clock.Set(Epoch.AddMinutes(minute));
            Assert.True(session.Tick());
        }
        var current = session.CaptureFrame()!;
        var retained = Math.Min(capacity, initialBars + ticks);
        var lastKey = initialBars + ticks;
        Assert.Equal(Enumerable.Range(lastKey - retained + 1, retained).Select(key => (long)key),
            current.Source.Bars.Select(bar => bar.Key));
        Assert.Equal(retained, session.Statistics.RetainedBars);
        Assert.Equal(initialBars + ticks - retained, session.Statistics.EvictedBars);
        Assert.Equal(ticks, current.Source.SourceVersion);
        Assert.Equal(ticks, session.Statistics.ProducedTicks);
        Assert.Equal(Enumerable.Range(1, initialBars).Select(key => (long)key),
            earliest.Source.Bars.Select(bar => bar.Key));
        for (var index = 1; index < current.Source.Bars.Length; index++)
            Assert.True(current.Source.Bars[index].Timestamp > current.Source.Bars[index - 1].Timestamp);
    }

    [Fact]
    public async Task Backward_clock_is_rejected_without_advancing_random_state_or_mutating_source()
    {
        var clock = new ManualClock();
        var controlClock = new ManualClock();
        await using var session = new RealtimePriceSession(4, 4, clock, 17);
        await using var control = new RealtimePriceSession(4, 4, controlClock, 17);
        session.Start(); control.Start();
        var initial = session.CaptureFrame()!;
        clock.Set(Epoch.AddTicks(-1));
        Assert.False(session.Tick());
        clock.Set(Epoch.AddDays(-1));
        Assert.False(session.Tick());
        Assert.Equal(0, session.Statistics.SourceVersion);
        Assert.Equal(0, session.Statistics.ProducedTicks);
        Assert.Equal(2, session.Statistics.RejectedClockTicks);
        Assert.Null(session.CaptureFrame());
        Assert.Same(initial.Source, session.CaptureFrame(force: true)!.Source);
        clock.Set(Epoch.AddSeconds(1)); controlClock.Set(Epoch.AddSeconds(1));
        Assert.True(session.Tick()); Assert.True(control.Tick());
        Assert.Equal(control.CaptureFrame()!.Source.Bars.ToArray(), session.CaptureFrame()!.Source.Bars.ToArray());
        Assert.True(session.Tick()); // Equal instants may produce another trade in the same minute.
        Assert.Equal(2, session.Statistics.ProducedTicks);
    }

    [Fact]
    public async Task Pause_freezes_display_not_production_and_lifecycle_epochs_reject_late_frames()
    {
        var clock = new ManualClock();
        await using var session = new RealtimePriceSession(4, 4, clock);
        session.Start();
        var original = session.CaptureFrame()!;
        var oldBars = original.Source.Bars.ToArray();
        session.PauseDisplay(); session.PauseDisplay();
        Assert.True(session.IsRunning);
        Assert.True(session.IsDisplayPaused);
        Assert.False(session.IsFrameCurrent(original));
        clock.Set(Epoch.AddMinutes(1)); session.Tick();
        clock.Set(Epoch.AddMinutes(2)); session.Tick();
        Assert.Equal(2, session.Statistics.ProducedTicks);
        Assert.Null(session.CaptureFrame());
        Assert.Null(session.CaptureFrame(force: true));
        Assert.Equal(oldBars, original.Source.Bars.ToArray());
        session.ResumeDisplay(); session.ResumeDisplay();
        var resumed = session.CaptureFrame()!;
        Assert.False(session.IsDisplayPaused);
        Assert.Equal(original.Generation, resumed.Generation);
        Assert.Equal(original.PublicationRevision + 2, resumed.PublicationRevision);
        Assert.Equal(2, resumed.Source.SourceVersion);
        Assert.NotSame(original.Source, resumed.Source);
        Assert.False(session.IsFrameCurrent(original));
        Assert.True(session.IsFrameCurrent(resumed));
        Assert.Null(session.CaptureFrame());
        session.PauseDisplay(); session.ResumeDisplay();
        var unchanged = session.CaptureFrame()!;
        Assert.Same(resumed.Source, unchanged.Source);
        Assert.False(session.IsFrameCurrent(resumed));
        Assert.True(session.IsFrameCurrent(unchanged));
    }

    [Fact]
    public async Task Frame_validation_rejects_fabricated_or_superseded_sources_but_not_background_advancement()
    {
        var clock = new ManualClock();
        await using var session = new RealtimePriceSession(4, 4, clock);
        session.Start();
        var frame = session.CaptureFrame()!;
        var copy = new PriceChartSource(frame.Source.SeriesId, frame.Source.Label,
            frame.Source.Bars, frame.Source.DatasetVersion, frame.Source.SourceVersion);
        Assert.False(session.IsFrameCurrent(frame with { Source = copy }));
        Assert.False(session.IsFrameCurrent(frame with { Generation = frame.Generation + 1 }));
        Assert.False(session.IsFrameCurrent(frame with { PublicationRevision = frame.PublicationRevision + 1 }));
        Assert.False(session.IsFrameCurrent(null));
        clock.Set(Epoch.AddSeconds(1));
        session.Tick();
        Assert.True(session.IsFrameCurrent(frame));
        var newer = session.CaptureFrame()!;
        Assert.False(session.IsFrameCurrent(frame));
        Assert.True(session.IsFrameCurrent(newer));
        Assert.Same(newer.Source, session.CaptureFrame(force: true)!.Source);
        Assert.True(session.IsFrameCurrent(newer));
    }

    [Fact]
    public async Task Real_delay_worker_uses_41ms_clock_keeps_running_paused_and_joins_without_next_tick()
    {
        var clock = new ManualClock();
        await using var session = new RealtimePriceSession(4, 4, clock);
        session.Start(); session.Start();
        Assert.Equal(1, clock.CreatedTimers);
        Assert.Equal(1, clock.LiveTimers);
        Assert.Equal(TimeSpan.FromMilliseconds(41), clock.DueTime);
        Assert.Equal(Timeout.InfiniteTimeSpan, clock.Period); // Task.Delay creates cancellable one-shot timers.
        Assert.Equal(1, session.Statistics.ActiveWorkers);
        var original = session.CaptureFrame()!;
        await clock.FireAsync(TimeSpan.FromMilliseconds(41));
        Assert.Equal(1, session.Statistics.ProducedTicks);
        session.PauseDisplay();
        await clock.FireAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(2, session.Statistics.ProducedTicks);
        Assert.Null(session.CaptureFrame(force: true));
        var lateTimerCallback = await clock.CaptureLateCallbackAsync();
        Assert.Equal(3, clock.CreatedTimers); // Initial wait and one new wait after each completed tick.
        Assert.Equal(1, clock.LiveTimers);
        var stop = session.StopAsync();
        Assert.True(session.IsStopped);
        Assert.False(session.IsRunning);
        Assert.False(session.IsFrameCurrent(original));
        Assert.Equal(0, session.Statistics.RetainedBars);
        Assert.Same(stop, session.StopAsync());
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, clock.LiveTimers);
        Assert.Equal(0, session.Statistics.ActiveWorkers);
        Assert.Equal(4, session.Statistics.ReleasedBars);
        Assert.Null(session.Statistics.Error);
        lateTimerCallback(); // A callback already copied by the timer cannot restart production.
        Assert.False(session.Tick());
        Assert.Equal(2, session.Statistics.ProducedTicks);
        Assert.Null(session.CaptureFrame(force: true));
        Assert.Throws<InvalidOperationException>(session.Start);
        await session.DisposeAsync(); await session.DisposeAsync();
    }

    [Fact]
    public async Task Stop_before_start_releases_history_and_restart_is_a_distinct_generation()
    {
        var clock = new ManualClock();
        await using var stopped = new RealtimePriceSession(4, 4, clock);
        await stopped.StopAsync();
        Assert.Equal(0, stopped.Statistics.RetainedBars);
        Assert.Equal(4, stopped.Statistics.ReleasedBars);
        Assert.Equal(0, clock.CreatedTimers);
        Assert.Throws<InvalidOperationException>(stopped.Start);
        await using var first = new RealtimePriceSession(4, 4, clock);
        first.Start();
        var oldFrame = first.CaptureFrame()!;
        await first.StopAsync();
        await using var reopened = new RealtimePriceSession(4, 4, clock);
        reopened.Start();
        var freshFrame = reopened.CaptureFrame()!;
        Assert.NotEqual(oldFrame.Generation, freshFrame.Generation);
        Assert.NotEqual(oldFrame.Source.DatasetVersion, freshFrame.Source.DatasetVersion);
        Assert.False(reopened.IsFrameCurrent(oldFrame));
        Assert.False(first.IsFrameCurrent(freshFrame));
        Assert.True(reopened.IsFrameCurrent(freshFrame));
    }

    [Fact]
    public async Task Concurrent_captures_pause_ticks_and_close_cannot_resurrect_a_session()
    {
        var clock = new ManualClock();
        await using var session = new RealtimePriceSession(4, 4, clock);
        session.Start();
        var initial = session.CaptureFrame()!;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captures = Enumerable.Range(0, 8).Select(async index =>
        {
            await start.Task;
            if (index % 2 == 0) session.PauseDisplay(); else session.ResumeDisplay();
            session.Tick();
            return session.CaptureFrame(force: true);
        }).ToArray();
        var stops = Enumerable.Range(0, 3).Select(async _ =>
        {
            await start.Task;
            await session.DisposeAsync();
        }).ToArray();
        start.SetResult();
        var frames = await Task.WhenAll(captures).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.IsFrameCurrent(initial));
        Assert.All(frames, frame => Assert.False(session.IsFrameCurrent(frame)));
        Assert.True(session.IsStopped);
        Assert.Equal(0, session.Statistics.ActiveWorkers);
        Assert.Equal(0, session.Statistics.RetainedBars);
        Assert.Equal(0, clock.LiveTimers);
        Assert.Equal(1, clock.CreatedTimers);
        Assert.Null(session.CaptureFrame(force: true));
        session.ResumeDisplay(); session.PauseDisplay();
        Assert.False(session.Tick());
        Assert.Throws<InvalidOperationException>(session.Start);
    }

    [Fact]
    public async Task Timer_creation_and_worker_clock_faults_are_observable_and_shutdown_is_joined()
    {
        var creationClock = new ManualClock { FailTimerCreation = true };
        await using var creation = new RealtimePriceSession(4, 4, creationClock);
        creation.Start();
        Assert.True(creation.IsStopped);
        Assert.Equal("timer unavailable", creation.Statistics.Error!.Message);
        await creation.StopAsync();
        Assert.Equal(0, creation.Statistics.ActiveWorkers);
        Assert.Equal(0, creation.Statistics.RetainedBars);

        var clock = new ManualClock();
        await using var production = new RealtimePriceSession(4, 4, clock);
        production.Start();
        clock.FailClockRead = true;
        await clock.FireAsync(TimeSpan.FromMilliseconds(41));
        await production.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("clock unavailable", production.Statistics.Error!.Message);
        Assert.True(production.IsStopped);
        Assert.Equal(0, production.Statistics.ProducedTicks);
        Assert.Equal(0, production.Statistics.RetainedBars);
        Assert.Equal(0, production.Statistics.ActiveWorkers);
        Assert.Equal(0, clock.LiveTimers);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(PriceChartSource.MaximumBars + 1, 1)]
    [InlineData(4, 0)]
    [InlineData(4, 5)]
    public void Invalid_bounds_are_rejected(int capacity, int initialBars)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RealtimePriceSession(capacity, initialBars));

    [Fact]
    public void Unrepresentable_initial_history_is_rejected_without_fabricated_timestamps()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RealtimePriceSession(4, 4, new ManualClock(DateTimeOffset.MinValue)));

    [Fact]
    public void Viewport_preserves_anchor_and_every_requested_option_across_rolling_eviction()
    {
        var previous = Source(1, 2, 3, 4);
        var next = Source(2, 3, 4, 5);
        var domain = new HistoryValueDomain(90, 110);
        var options = new PriceChartOptions(2, 2, 1, domain, .31);
        var preserved = PriceRealtimeViewport.Preserve(previous, next, options);
        Assert.Equal(1, preserved.BarStart);
        Assert.Equal(previous.Bars[2].Timestamp, next.Bars[preserved.BarStart].Timestamp);
        Assert.Equal(options.BarCount, preserved.BarCount);
        Assert.Equal(options.GeometryBudget, preserved.GeometryBudget);
        Assert.Equal(options.FixedDomain, preserved.FixedDomain);
        Assert.Equal(options.BodyWidthFraction, preserved.BodyWidthFraction);
        var lost = PriceRealtimeViewport.Preserve(previous, Source(8, 9, 10, 11), options);
        Assert.Equal(0, lost.BarStart); // Lost anchor clamps explicitly to the first survivor.
        Assert.Equal(2, lost.BarCount);
        var full = new PriceChartOptions(0, PriceChartSource.MaximumBars, 6, domain, .3);
        Assert.Same(full, PriceRealtimeViewport.Preserve(previous, next, full));
    }

    [Fact]
    public void Viewport_does_not_invent_time_identity_for_new_dataset_series_or_unavailable_anchor()
    {
        var previous = Source(1, 2, 3, 4);
        var bars = Source(8, 9, 10, 11).Bars;
        var options = new PriceChartOptions(2, 2, 7, new HistoryValueDomain(90, 120), .6);
        var newDataset = new PriceChartSource(previous.SeriesId, "Synthetic", bars, 2, 0);
        var newSeries = new PriceChartSource("other", "Synthetic", bars, 1, 1);
        Assert.Same(options, PriceRealtimeViewport.Preserve(previous, newDataset, options));
        Assert.Same(options, PriceRealtimeViewport.Preserve(previous, newSeries, options));
        var unavailable = new PriceChartOptions(15, 0, 7, options.FixedDomain, .6);
        Assert.Same(unavailable, PriceRealtimeViewport.Preserve(previous, Source(2, 3, 4, 5), unavailable));
        var empty = new PriceChartSource(previous.SeriesId, "Synthetic", [], 1, 2);
        var emptyOptions = PriceRealtimeViewport.Preserve(previous, empty, options);
        Assert.Equal(0, emptyOptions.BarStart);
        Assert.Equal(options.BarCount, emptyOptions.BarCount);
        Assert.Same(options, PriceRealtimeViewport.Preserve(previous, previous, options));
    }

    [Fact]
    public async Task Fresh_realtime_originals_feed_six_aggregations_indicators_hover_and_current_close_without_mutating_paused_source()
    {
        var clock = new ManualClock();
        await using var session = new RealtimePriceSession(timeProvider: clock);
        session.Start();
        var original = session.CaptureFrame()!.Source;
        var before = original.Bars.ToArray();
        session.PauseDisplay();
        clock.Set(Epoch.AddMinutes(1));
        Assert.True(session.Tick());
        Assert.Null(session.CaptureFrame(force: true));
        // Inspection while paused only receives the still-published immutable source.
        var paused = PriceTimeAggregator.Aggregate(original, PriceTimeInterval.Minute5);
        Assert.Same(original, paused.OriginalSource);
        Assert.Equal(before[^1].Close, paused.Source.Bars[^1].Close);
        session.ResumeDisplay();
        var latest = session.CaptureFrame()!.Source;
        Assert.Equal(241, latest.Bars.Length);
        Assert.Equal(1, latest.SourceVersion);
        foreach (var interval in Enum.GetValues<PriceTimeInterval>())
        {
            var aggregate = PriceTimeAggregator.Aggregate(latest, interval);
            Assert.Same(latest, aggregate.OriginalSource);
            Assert.Equal(latest.DatasetVersion, aggregate.Source.DatasetVersion);
            Assert.Equal(latest.SourceVersion, aggregate.Source.SourceVersion);
            var expected = latest.Bars.GroupBy(bar => bar.Timestamp.UtcTicks /
                PriceTimeAggregator.GetDuration(interval).Ticks).ToArray();
            Assert.Equal(expected.Length, aggregate.Source.Bars.Length);
            for (var index = 0; index < expected.Length; index++)
            {
                var rows = expected[index].ToArray();
                var actual = aggregate.Source.Bars[index];
                Assert.Equal(rows[0].Key, actual.Key);
                Assert.Equal(rows[0].Timestamp, actual.Timestamp);
                Assert.Equal(rows[0].Open, actual.Open);
                Assert.Equal(rows.Max(bar => bar.High), actual.High);
                Assert.Equal(rows.Min(bar => bar.Low), actual.Low);
                Assert.Equal(rows[^1].Close, actual.Close);
                Assert.Equal(rows.Sum(bar => bar.Volume!.Value), actual.Volume);
                Assert.Equal(rows.Length, aggregate.Segments[index].OriginalCount);
            }
            var prices = aggregate.Source.Query(new(fixedDomain: new HistoryValueDomain(80, 120)));
            var bb = PriceBollingerBands.Calculate(aggregate.Source);
            var cci = PriceCommodityChannelIndex.Calculate(aggregate.Source);
            var wr = PriceWilliamsR.Calculate(aggregate.Source);
            Assert.Same(prices.Source, bb.Source);
            Assert.Same(prices.Source, cci.Source);
            Assert.Same(prices.Source, wr.Source);
            var volumes = PriceVolumeGeometry.Create(prices);
            var hover = PriceCrosshair.Query(prices, 1, volumes, bb, cci, wr, aggregate)!;
            Assert.Equal(aggregate.Source.Bars[^1], hover.Hit.Bar);
            Assert.Equal(aggregate.Segments[^1], hover.AggregateSegment);
            var current = PriceCurrentClose.Create(prices, aggregate);
            Assert.Same(latest, current.OriginalSource);
            Assert.Equal(latest.Bars[^1], current.OriginalHit!.Value.Bar);
            Assert.Equal(latest.SourceVersion, current.OriginalHit.Value.SourceVersion);
        }
        Assert.Equal(before, original.Bars.ToArray());
        Assert.Equal(before[^1].Close, paused.Source.Bars[^1].Close);
    }

    private static PriceChartSource Source(params int[] minutes) => new("test", "Synthetic",
        minutes.Select(minute => new PriceBar(minute, Epoch.AddMinutes(minute), 100, 101, 99, 100, 10)), 1, 1);

    /// <summary>Drives real Task.Delay one-shot callbacks, rather than replacing the session worker.
    /// Read/registration notifications and the session lock establish progress without polling/sleeps.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _now;
        private ManualTimer? _timer;
        private TaskCompletionSource<ManualTimer> _timerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _nextRead;
        private int _createdTimers, _liveTimers;
        public ManualClock() : this(Epoch) { }
        public ManualClock(DateTimeOffset now) => _now = now;
        public bool FailTimerCreation { get; init; }
        public bool FailClockRead { get; set; }
        public int CreatedTimers => Volatile.Read(ref _createdTimers);
        public int LiveTimers => Volatile.Read(ref _liveTimers);
        public TimeSpan DueTime { get; private set; }
        public TimeSpan Period { get; private set; }
        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                _nextRead?.TrySetResult();
                _nextRead = null;
                if (FailClockRead) throw new InvalidOperationException("clock unavailable");
                return _now;
            }
        }
        public void Set(DateTimeOffset now) { lock (_gate) _now = now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (FailTimerCreation) throw new InvalidOperationException("timer unavailable");
            lock (_gate)
            {
                DueTime = dueTime; Period = period;
                Interlocked.Increment(ref _createdTimers);
                Interlocked.Increment(ref _liveTimers);
                _timer = new(this, callback, state);
                if (_timerReady.Task.IsCompleted)
                    _timerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _timerReady.TrySetResult(_timer);
                return _timer;
            }
        }
        public async Task FireAsync(TimeSpan advance)
        {
            var fire = await CaptureLateCallbackAsync();
            Task observed;
            lock (_gate)
            {
                _now += advance;
                _nextRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
                observed = _nextRead.Task;
            }
            fire();
            await observed.WaitAsync(TimeSpan.FromSeconds(5));
        }
        public async Task<Action> CaptureLateCallbackAsync()
        {
            Task<ManualTimer> ready;
            lock (_gate) ready = _timerReady.Task;
            var timer = await ready.WaitAsync(TimeSpan.FromSeconds(5));
            return timer.CaptureCallback();
        }
        private void TimerFiring(ManualTimer timer)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(timer, _timer)) return;
                // Consumed one-shot callbacks cannot be fired twice while the worker registers its next wait.
                _timer = null;
                _timerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        private void TimerDisposed(ManualTimer timer)
        {
            lock (_gate)
            {
                Interlocked.Decrement(ref _liveTimers);
                if (!ReferenceEquals(timer, _timer)) return;
                _timer = null;
                _timerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            private TimerCallback? _callback = callback;
            private object? _state = state;
            public Action CaptureCallback()
            {
                var action = _callback!;
                var argument = _state;
                return () => { owner.TimerFiring(this); action(argument); };
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => _callback is not null;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _callback, null) is null) return;
                _state = null;
                owner.TimerDisposed(this);
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
