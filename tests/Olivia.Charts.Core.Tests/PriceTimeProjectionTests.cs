using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceTimeProjectionTests
{
    private static readonly DateTimeOffset Friday = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Weekend_padding_candles_volume_hit_and_hover_use_one_projection()
    {
        var source = Source(Bar(1, Friday), Bar(2, Friday.AddMinutes(5)),
            Bar(3, Friday.AddDays(3)), Bar(4, Friday.AddDays(3).AddMinutes(5)));
        var aggregation = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var snapshot = Query(aggregation, -2, 5);
        var volumes = PriceVolumeGeometry.Create(snapshot);
        Assert.Equal(4, snapshot.Candles.Length);
        for (var index = 0; index < 4; index++)
        {
            var expected = (index + 2) / 7d;
            var candle = snapshot.Candles[index];
            var column = volumes.Columns[index];
            Assert.Equal(expected, candle.CenterX, 12);
            Assert.Equal(expected, column.Body.Left + column.Body.Width / 2, 12);
            Assert.Equal(.7 / 7, candle.Body!.Value.Width, 12);
            Assert.Equal(.2 / 7, column.Body.Width, 12);
            Assert.Equal(candle.Hit, PriceChartGeometry.HitTest(snapshot, expected, .5));
            var hover = PriceCrosshair.Query(snapshot, expected, volumes, aggregation: aggregation);
            Assert.NotNull(hover);
            Assert.Equal(candle.Hit, hover.Hit);
            Assert.Equal(expected, hover.ScreenX, 12);
        }
        Assert.Null(PriceCrosshair.Query(snapshot, 0, aggregation: aggregation));
        Assert.Null(PriceCrosshair.Query(snapshot, 1, aggregation: aggregation));
        Assert.Equal(source.Bars, aggregation.OriginalSource.Bars);
    }

    [Fact]
    public void Same_bucket_gap_segments_overlap_without_changing_provenance_and_later_segment_wins()
    {
        var source = Source(Bar(1, Friday), new PriceBar(2, Friday.AddSeconds(10), null, null, null, null),
            Bar(3, Friday.AddSeconds(20)), Bar(4, Friday.AddMinutes(5)));
        var aggregate = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var snapshot = Query(aggregate, -2, 3);
        Assert.Equal(new[] { .4, .4, .6 }, snapshot.Candles.Select(c => c.CenterX));
        Assert.Equal(2, Assert.Single(snapshot.Gaps).Key);
        Assert.Equal(3, PriceCrosshair.Query(snapshot, .4, aggregation: aggregate)!.Hit.Key);
        Assert.Equal(3, PriceChartGeometry.HitTest(snapshot, .4, .5)!.Value.Key);
        var raw = source.Query();
        var first = PriceTrendGeometry.CreateAnchor(raw, 0, PriceTrendValueLevel.Close)!.Value;
        var second = PriceTrendGeometry.CreateAnchor(raw, 2, PriceTrendValueLevel.Close)!.Value;
        Assert.Equal(PriceTrendProjectionStatus.CollapsedAggregateSlot,
            PriceTrendGeometry.Project(new(1, first, second), snapshot, aggregate).Status);
        Assert.Equal(PriceTrendProjectionStatus.CollapsedAggregateSlot,
            PriceFibonacciRetracementGeometry.Project(new(1, first, second), snapshot, aggregate).Status);
        Assert.Equal(PriceTrendProjectionStatus.CollapsedAggregateSlot,
            PriceFibonacciFanGeometry.Project(new(1, first, second), snapshot, aggregate).Status);
        Assert.Equal(PriceTrendProjectionStatus.CollapsedAggregateSlot,
            PriceFibonacciArcGeometry.Project(new(1, first, second), snapshot, 700, 400, aggregate).Status);
    }

    [Fact]
    public void Trend_retracement_fan_and_arc_keep_original_anchors_at_shared_time_positions()
    {
        var source = Source(Bar(1, Friday), Bar(2, Friday.AddMinutes(5)), Bar(3, Friday.AddDays(3)));
        var raw = source.Query();
        var first = PriceTrendGeometry.CreateAnchor(raw, 0, PriceTrendValueLevel.Close)!.Value;
        var second = PriceTrendGeometry.CreateAnchor(raw, 2, PriceTrendValueLevel.Close)!.Value;
        var aggregate = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var snapshot = Query(aggregate, -2, 4);
        var trend = PriceTrendGeometry.Project(new(1, first, second), snapshot, aggregate);
        Assert.Equal(PriceTrendProjectionStatus.Visible, trend.Status);
        Assert.Equal(1 / 3d, trend.Segment!.Value.Start.X, 12);
        Assert.Equal(2 / 3d, trend.Segment.Value.End.X, 12);
        var retracement = PriceFibonacciRetracementGeometry.Project(new(1, first, second), snapshot, aggregate);
        Assert.All(retracement.Levels, level =>
        {
            Assert.Equal(1 / 3d, level.Segment!.Value.Start.X, 12);
            Assert.Equal(2 / 3d, level.Segment.Value.End.X, 12);
        });
        var fan = PriceFibonacciFanGeometry.Project(new(1, first, second), snapshot, aggregate);
        Assert.Equal(PriceTrendProjectionStatus.Visible, fan.Status);
        Assert.All(fan.Levels, level => Assert.Equal(1 / 3d, level.Segment!.Value.Start.X, 12));
        var arc = PriceFibonacciArcGeometry.Project(new(1, first, second) { ShowLevel100 = true }, snapshot, 600, 400, aggregate);
        Assert.Equal(200d, arc.Levels.Single(level => level.Ratio == 1).RadiusDips);
        Assert.Equal(first.Hit, trend.First!.Value.Hit);
    }

    [Fact]
    public void Fractional_range_clips_indicator_segments_and_edge_bodies_without_false_padding_hits()
    {
        var aggregate = PriceTimeAggregator.Aggregate(Source(Bar(1, Friday), Bar(2, Friday.AddMinutes(5)),
            Bar(3, Friday.AddMinutes(10))), PriceTimeInterval.Minute5);
        var snapshot = Query(aggregate, 0, 1.5);
        Assert.Equal(2, snapshot.BarCount);
        Assert.Equal(0, snapshot.Candles[0].Body!.Value.Left);
        Assert.Equal(.35 / 1.5, snapshot.Candles[0].Body!.Value.Width, 12);
        var crossing = snapshot.ClipValueSegment(1, 10, 2, 10, new(0, 20));
        Assert.Equal(2 / 3d, crossing!.Value.Start.X, 12);
        Assert.Equal(1, crossing.Value.End.X);
        Assert.Null(PriceCrosshair.Query(snapshot, 1, aggregation: aggregate));
    }

    [Fact]
    public void Rebase_over_evicted_weekend_preserves_fractional_view_and_old_snapshot()
    {
        var rows = new[] { Bar(1, Friday), Bar(2, Friday.AddMinutes(5)), Bar(3, Friday.AddDays(3)), Bar(4, Friday.AddDays(3).AddMinutes(5)) };
        var oldAggregate = PriceTimeAggregator.Aggregate(Source(rows), PriceTimeInterval.Minute5);
        var old = Query(oldAggregate, 1.25, 4.25);
        var nextAggregate = PriceTimeAggregator.Aggregate(Source(rows.Skip(2).Append(Bar(5, Friday.AddDays(3).AddMinutes(10))).ToArray()), PriceTimeInterval.Minute5);
        var next = old.Options.TimeViewport!.Rebase(nextAggregate)!;
        Assert.Equal(new PriceTimeAxisRange(-.75, 2.25), next.Range);
        var current = nextAggregate.Source.Query(old.Options.WithTimeViewport(next));
        Assert.Equal(old.GetNormalizedX(2), current.GetNormalizedX(0), 12);
        Assert.Equal(new PriceTimeAxisRange(1.25, 4.25), old.Options.TimeViewport.Range);
        Assert.Same(oldAggregate.Source, old.Source);
    }

    [Fact]
    public void Time_viewport_rejects_foreign_source_and_rebase_exits_on_interval_or_dataset_change()
    {
        var source = Source(Bar(1, Friday), Bar(2, Friday.AddMinutes(5)));
        var aggregation = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var snapshot = Query(aggregation, -2, 3);
        Assert.Throws<ArgumentException>(() => source.Query(snapshot.Options));
        Assert.Null(snapshot.Options.TimeViewport!.Rebase(PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute1)));
        var other = new PriceChartSource(source.SeriesId, source.Label, source.Bars, 2);
        Assert.Null(snapshot.Options.TimeViewport.Rebase(PriceTimeAggregator.Aggregate(other, PriceTimeInterval.Minute5)));
    }

    [Fact]
    public void Certified_tail_updates_reuse_axis_mapping_without_copying_full_history()
    {
        var source = Source(Enumerable.Range(0, 18000).Select(i => Bar(i, Friday.AddMinutes(i))).ToArray());
        var cache = new PriceAggregationCache();
        var aggregate = cache.Aggregate(source, PriceTimeInterval.Minute1);
        var time = Query(aggregate, 17940, 18000).Options.TimeViewport!;
        var tail = source.Bars[^1];
        var next = source.ReplaceTail(new(tail.Key, tail.Timestamp, tail.Open, tail.High, tail.Low, tail.Close, 101),
            source.SourceVersion + 1);
        var nextAggregate = cache.Aggregate(next, PriceTimeInterval.Minute1);
        _ = time.Rebase(nextAggregate); // Warm up the small immutable-wrapper path.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var rebased = time.Rebase(nextAggregate)!;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 8192, $"Tail-only projection allocated {allocated} bytes.");
        Assert.Same(nextAggregate, rebased.Axis.Aggregation);
        Assert.Equal(time.Range, rebased.Range);
        Assert.Equal(time.Axis.GetSegmentCoordinate(17999), rebased.Axis.GetSegmentCoordinate(17999));
        Assert.Equal(100d, time.Axis.Aggregation.Source.Bars[^1].Volume);
        Assert.Equal(101d, rebased.Axis.Aggregation.Source.Bars[^1].Volume);
    }

    [Fact]
    public void Time_tick_labels_do_not_invent_weekend_dates_or_duplicate_rounded_dates()
    {
        var aggregate = PriceTimeAggregator.Aggregate(Source(Bar(1, Friday), Bar(2, Friday.AddDays(3))), PriceTimeInterval.Minute5);
        var time = Query(aggregate, 0, 1).Options.TimeViewport!;
        var ticks = time.CreateTicks(6);
        Assert.Equal(new[] { Friday, Friday.AddDays(3) }, ticks.Select(t => t.Timestamp));
        Assert.All(ticks, t => Assert.InRange(t.Position, 0, 1));
    }

    private static PriceBar Bar(long key, DateTimeOffset date) => new(key, date, 9, 12, 8, 11, 100);
    private static PriceChartSource Source(params PriceBar[] bars) => new("time-projection", "Time projection", bars, 1);
    private static PriceChartSnapshot Query(PriceTimeAggregationSnapshot aggregate, double minimum, double maximum) =>
        aggregate.Source.Query(new(timeViewport: new(PriceTimeAxis.Create(aggregate), new(minimum, maximum))));
}
