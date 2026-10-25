using System.Diagnostics;
using System.Text.Json;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceScaleTests
{
    [Fact]
    public void Official_reader_reverses_exact_rows_preserves_offset_and_subminute_time_and_skips_Sunday()
    {
        var end = new DateTimeOffset(2026, 10, 26, 0, 0, 40, TimeSpan.FromHours(8)).AddTicks(7);
        using var reader = new StringReader("11,14,9,13,17\n" +
            string.Concat(Enumerable.Repeat("2,4,1,3,5\n", 17999)) + "not read\n");
        var source = PriceFinancialCsv.ReadInitial(reader, end);
        Assert.Equal(18000, source.Bars.Length);
        Assert.Equal("not read", reader.ReadLine());
        Assert.Equal(new PriceBar(18000, end.AddMinutes(-1).AddDays(-2), 11, 14, 9, 13, 17), source.Bars[^1]);
        Assert.Equal(TimeSpan.FromHours(8), source.Bars[^1].Timestamp.Offset);
        Assert.Equal(1, source.Bars[0].Key);
        Assert.DoesNotContain(source.Bars, b => b.Timestamp.DayOfWeek == DayOfWeek.Sunday);
        Assert.All(source.Bars.Zip(source.Bars.Skip(1)), pair => Assert.True(pair.First.Timestamp < pair.Second.Timestamp));
        Assert.Throws<FormatException>(() => PriceFinancialCsv.ReadInitial(new StringReader("2,1,0,3,5\n"), end));
        Assert.Throws<FormatException>(() => PriceFinancialCsv.ReadInitial(new StringReader("2,4,1,3,5\n"), end));
    }

    [Fact]
    public async Task Full_capacity_tail_capture_copies_once_and_retains_older_frames_through_rollover()
    {
        var clock = new Clock();
        await using var session = new RealtimePriceSession(initialBars: 18000, timeProvider: clock);
        session.Start();
        var old = session.CaptureFrame()!.Source;
        var oldTail = old.Bars[^1];
        session.Tick();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var next = session.CaptureFrame()!.Source;
        var allocation = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocation, 1, 2_000_000); // 18k x 104-byte bars plus bounded metadata, not three arrays + hash set.
        Assert.Equal(old.Bars.Take(17999), next.Bars.Take(17999));
        Assert.Equal(oldTail, old.Bars[^1]);
        Assert.True(next.Bars[^1].Volume > oldTail.Volume);
        clock.Advance(TimeSpan.FromMinutes(1)); session.Tick();
        var rolled = session.CaptureFrame()!.Source;
        Assert.Equal(18000, rolled.Bars.Length);
        Assert.Equal(2, rolled.Bars[0].Key);
        Assert.Equal(18001, rolled.Bars[^1].Key);
        Assert.Equal(1, session.Statistics.EvictedBars);
        Assert.Equal(next.Bars[^1], rolled.Bars[^2]);
        Assert.Equal(oldTail, old.Bars[^1]);
    }

    [Theory]
    [InlineData(PriceTimeInterval.Minute1)]
    [InlineData(PriceTimeInterval.Minute5)]
    [InlineData(PriceTimeInterval.Minute15)]
    [InlineData(PriceTimeInterval.Minute30)]
    [InlineData(PriceTimeInterval.Hour1)]
    [InlineData(PriceTimeInterval.Hour4)]
    public async Task Incremental_aggregation_and_indicators_match_full_calculation_after_tail_updates_and_eviction(PriceTimeInterval interval)
    {
        var clock = new Clock();
        await using var session = new RealtimePriceSession(512, 512, clock);
        var aggregateCache = new PriceAggregationCache();
        var indicators = new PriceIndicatorCache();
        session.Start();
        for (var frame = 0; frame < 5; frame++)
        {
            if (frame == 3) clock.Advance(TimeSpan.FromMinutes(1));
            session.Tick();
            var source = session.CaptureFrame()!.Source;
            var actual = aggregateCache.Aggregate(source, interval);
            var expected = PriceTimeAggregator.Aggregate(source, interval);
            Assert.Same(source, actual.OriginalSource);
            Assert.Equal(expected.Source.SeriesId, actual.Source.SeriesId);
            Assert.Equal(expected.Source.SourceVersion, actual.Source.SourceVersion);
            Assert.Equal(expected.Source.Bars.ToArray(), actual.Source.Bars.ToArray());
            Assert.Equal(expected.Segments.ToArray(), actual.Segments.ToArray());
            AssertIndicators(indicators, actual.Source);
        }
        aggregateCache.Clear(); indicators.Clear();
        var last = session.CaptureFrame(force: true)!.Source;
        AssertIndicators(indicators, aggregateCache.Aggregate(last, interval).Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Raw_cache_preserves_gap_warmup_flat_windows_and_does_not_trust_equal_versions(bool gap)
    {
        var clock = new Clock();
        var bars = Enumerable.Range(0, 32).Select(i => new PriceBar(i + 1,
            clock.GetUtcNow().AddMinutes(i - 31), 100, 100, 100, 100, 1)).ToArray();
        if (gap) bars[^3] = new(bars[^3].Key, bars[^3].Timestamp, null, null, null, null);
        var seed = new PriceChartSource("seed", "seed", bars);
        await using var session = new RealtimePriceSession(seed, clock);
        session.Start();
        var cache = new PriceIndicatorCache();
        AssertIndicators(cache, session.CaptureFrame()!.Source);
        for (var tick = 0; tick < 3; tick++)
        {
            session.Tick(); AssertIndicators(cache, session.CaptureFrame()!.Source);
        }
        var original = session.CaptureFrame(force: true)!.Source;
        var changed = original.Bars.ToArray();
        changed[10] = new(changed[10].Key, changed[10].Timestamp, 70, 120, 60, 80, 9);
        var unrelated = new PriceChartSource(original.SeriesId, original.Label, changed,
            original.DatasetVersion, original.SourceVersion);
        AssertIndicators(cache, unrelated);
        AssertIndicators(cache, original);
    }

    [Fact]
    public async Task Seeded_history_preserves_nonordinal_keys_and_offsets_and_allocates_a_distinct_new_key()
    {
        var clock = new Clock();
        var now = clock.GetUtcNow();
        var first = new PriceBar(2, now.AddMinutes(-1).ToOffset(TimeSpan.FromHours(3)), 10, 12, 8, 11, 3);
        var last = new PriceBar(1, now.ToOffset(TimeSpan.FromHours(-7)), 11, 13, 9, 12, 4);
        await using var session = new RealtimePriceSession(new("seed", "seed", [first, last]), clock);
        session.Start(); var initial = session.CaptureFrame()!.Source;
        session.Tick(); var sameMinute = session.CaptureFrame()!.Source;
        Assert.Equal(first, sameMinute.Bars[0]);
        Assert.Equal(last.Timestamp.Offset, sameMinute.Bars[^1].Timestamp.Offset);
        Assert.Equal(last.Timestamp.Ticks, sameMinute.Bars[^1].Timestamp.Ticks);
        clock.Advance(TimeSpan.FromMinutes(1)); session.Tick();
        var appended = session.CaptureFrame()!.Source;
        Assert.Equal(new long[] { 2, 1, 3 }, appended.Bars.Select(b => b.Key));
        Assert.Equal(last, initial.Bars[^1]);
        Assert.Throws<ArgumentException>(() => new RealtimePriceSession(new PriceChartSource("seed", "seed",
            [new PriceBar(long.MaxValue, now, 1, 2, 0, 1, 1)]), clock));
        Assert.Throws<ArgumentException>(() => new RealtimePriceSession(new PriceChartSource("seed", "seed",
            [new PriceBar(1, clock.GetUtcNow().AddMinutes(1), 1, 2, 0, 1, 1)]), clock));
    }

    [Fact]
    public async Task Eighteen_thousand_bar_publication_comparison()
    {
        var output = Environment.GetEnvironmentVariable("OLIVIA_PRICE_PERF_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        var clock = new Clock();
        await using var session = new RealtimePriceSession(initialBars: 18000, timeProvider: clock);
        session.Start();
        var first = session.CaptureFrame()!.Source;
        var frames = new List<PriceChartSource>();
        for (var i = 0; i < 20; i++) { session.Tick(); frames.Add(session.CaptureFrame()!.Source); }
        var reports = new List<object>();
        foreach (var interval in new PriceTimeInterval?[] { null, PriceTimeInterval.Minute1, PriceTimeInterval.Minute5 })
        {
            var cache = new PriceIndicatorCache(); var aggregation = new PriceAggregationCache();
            Prepare(first, true); Prepare(first, false); // JIT/cache warm-up excluded.
            reports.Add(new { interval = interval?.ToString() ?? "Raw", full = Measure(false), incremental = Measure(true) });
            object Measure(bool incremental)
            {
                var times = new List<double>(); var allocated = 0L;
                foreach (var frame in frames)
                {
                    var bytes = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
                    Prepare(frame, incremental);
                    times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    allocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
                }
                return new { frames = times.Count, averageMs = times.Average(), maxMs = times.Max(), bytesPerFrame = allocated / times.Count };
            }
            void Prepare(PriceChartSource raw, bool incremental)
            {
                var source = interval is { } chosen ? (incremental ? aggregation.Aggregate(raw, chosen)
                    : PriceTimeAggregator.Aggregate(raw, chosen)).Source : raw;
                var bands = incremental ? cache.Bollinger(source) : PriceBollingerBands.Calculate(source);
                var cci = incremental ? cache.Cci(source) : PriceCommodityChannelIndex.Calculate(source);
                var wr = incremental ? cache.WilliamsR(source) : PriceWilliamsR.Calculate(source);
                var prices = source.Query(new(Math.Max(0, source.Bars.Length - 60), 60));
                var volume = PriceVolumeGeometry.Create(prices);
                Assert.Same(source, bands.Source); Assert.Same(source, cci.Source); Assert.Same(source, wr.Source);
                Assert.NotEmpty(prices.Candles); Assert.NotEmpty(volume.Columns);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { rawBars = 18000, visibleBars = 60,
            notes = "Sequential same-minute model preparation only; excludes source capture, Avalonia rendering, rollover and first frame. No FPS claim.", reports },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AssertIndicators(PriceIndicatorCache cache, PriceChartSource source)
    {
        var bb = cache.Bollinger(source); var bbFull = PriceBollingerBands.Calculate(source);
        Assert.Equal(bbFull.Points.ToArray(), bb.Points.ToArray()); Assert.Equal(bbFull.OverflowCount, bb.OverflowCount);
        var wr = cache.WilliamsR(source); var wrFull = PriceWilliamsR.Calculate(source);
        Assert.Equal(wrFull.Points.ToArray(), wr.Points.ToArray()); Assert.Equal(wrFull.FlatWindowCount, wr.FlatWindowCount);
        var cci = cache.Cci(source); var cciFull = PriceCommodityChannelIndex.Calculate(source);
        Assert.Equal(cciFull.Points.ToArray(), cci.Points.ToArray()); Assert.Equal(cciFull.FlatWindowCount, cci.FlatWindowCount);
        Assert.Same(source, bb.Source); Assert.Same(source, wr.Source); Assert.Same(source, cci.Source);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 23, 9, 0, 10, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
}
