using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceTimeAxisTests
{
    private static readonly DateTimeOffset Friday = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Weekend_compresses_to_one_step_and_external_padding_retains_elapsed_units()
    {
        var monday = Friday.AddDays(3);
        var aggregation = Aggregate(PriceTimeInterval.Minute5,
            Bar(1, Friday.AddSeconds(40)), Bar(2, Friday.AddMinutes(5).AddSeconds(40)),
            Bar(3, monday.AddSeconds(40)), Bar(4, monday.AddMinutes(5).AddSeconds(40)));
        var axis = PriceTimeAxis.Create(aggregation);

        Assert.Equal(4, axis.BucketCount);
        Assert.Same(aggregation, axis.Aggregation);
        Assert.Equal(new[] { 0, 1, 2, 3 }, Enumerable.Range(0, 4).Select(axis.GetSegmentCoordinate));
        Assert.Equal(-2d, axis.ToCoordinate(Friday.AddMinutes(-10).AddSeconds(59)));
        Assert.Equal(1d, axis.ToCoordinate(Friday.AddDays(1)));
        Assert.Equal(2d, axis.ToCoordinate(monday.AddMinutes(4).AddSeconds(59)));
        Assert.Equal(5d, axis.ToCoordinate(monday.AddMinutes(15).AddSeconds(40)));
        Assert.Equal(new PriceTimeAxisRange(-2, 5), axis.FinancialWholeRange);
        Assert.Equal(Friday.AddMinutes(-10), axis.ToTimestamp(-2));
        Assert.Equal(monday.AddMinutes(15), axis.ToTimestamp(5));
        // These are absent calendar labels, not an all-null OHLC row.
        Assert.False(axis.IsTimestampVisible(Friday.AddDays(1)));
        Assert.False(axis.IsTimestampVisible(Friday.AddMinutes(5).AddSeconds(41)));
        Assert.True(axis.IsTimestampVisible(Friday.AddMinutes(5).AddSeconds(40)));
        Assert.True(axis.IsTimestampVisible(Friday.AddMinutes(4)));
        Assert.True(axis.IsTimestampVisible(Friday.AddMinutes(-1)));
        Assert.True(axis.IsTimestampVisible(monday.AddDays(1)));
    }

    [Theory]
    [InlineData(1.25, 5, false)]
    [InlineData(1.5, 5, false)]
    [InlineData(1.50001, 0, true)]
    [InlineData(1.75, 0, true)]
    [InlineData(2, 0, true)]
    public void Inverse_inside_a_missing_interval_snaps_to_boundary_without_inventing_weekend_time(
        double coordinate, int minutes, bool monday)
    {
        var axis = PriceTimeAxis.Create(Aggregate(PriceTimeInterval.Minute5,
            Bar(1, Friday), Bar(2, Friday.AddMinutes(5)), Bar(3, Friday.AddDays(3))));
        Assert.Equal(Friday.AddDays(monday ? 3 : 0).AddMinutes(minutes), axis.ToTimestamp(coordinate));
    }

    [Theory]
    [InlineData(PriceTimeInterval.Minute1, 1, .49, 0)]
    [InlineData(PriceTimeInterval.Minute1, 1, .5, 1)]
    [InlineData(PriceTimeInterval.Minute5, 5, .5, 3)]
    [InlineData(PriceTimeInterval.Minute15, 15, .5, 8)]
    [InlineData(PriceTimeInterval.Minute30, 30, .25, 8)]
    [InlineData(PriceTimeInterval.Hour1, 60, .5, 60)]
    [InlineData(PriceTimeInterval.Hour4, 240, .375, 120)]
    public void Inverse_inside_a_run_rounds_to_base_unit_not_bucket_multiplier(
        PriceTimeInterval interval, int durationMinutes, double coordinate, int expectedMinutes)
    {
        var axis = PriceTimeAxis.Create(Aggregate(interval,
            Bar(1, Friday.AddSeconds(17)), Bar(2, Friday.AddMinutes(durationMinutes).AddSeconds(17))));
        Assert.Equal(Friday.AddMinutes(expectedMinutes), axis.ToTimestamp(coordinate));
        Assert.Equal(0d, axis.ToCoordinate(Friday.AddMinutes(durationMinutes).AddTicks(-1)));
        Assert.Equal(1d, axis.ToCoordinate(Friday.AddMinutes(durationMinutes)));
    }

    [Fact]
    public void Several_gap_split_segments_share_one_time_coordinate_without_mutating_source()
    {
        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        var aggregation = Aggregate(PriceTimeInterval.Minute5,
            new PriceBar(9, Friday.AddSeconds(10).ToOffset(TimeSpan.FromHours(8)), negativeZero, 2, -1, 1, 2),
            Gap(2, Friday.AddSeconds(20)), Bar(7, Friday.AddMinutes(1)),
            Gap(3, Friday.AddMinutes(2)), Bar(8, Friday.AddMinutes(3)), Bar(1, Friday.AddMinutes(5)));
        var original = aggregation.OriginalSource.Bars.ToArray();
        var derived = aggregation.Source.Bars.ToArray();
        var axis = PriceTimeAxis.Create(aggregation);

        Assert.Equal(6, aggregation.Segments.Length);
        Assert.Equal(2, axis.BucketCount);
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 1 }, Enumerable.Range(0, 6).Select(axis.GetSegmentCoordinate));
        Assert.True(axis.IsTimestampVisible(Friday.AddSeconds(20)));
        Assert.Equal(0d, axis.ToCoordinate(original[0].Timestamp));
        Assert.Equal(original, aggregation.OriginalSource.Bars.ToArray());
        Assert.Equal(derived, aggregation.Source.Bars.ToArray());
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(aggregation.Source.Bars[0].Open!.Value));
        Assert.Equal(TimeSpan.FromHours(8), aggregation.OriginalSource.Bars[0].Timestamp.Offset);
    }

    [Fact]
    public void Zoom_limit_checks_numeric_span_even_when_candle_count_is_small_or_large()
    {
        var bars = Enumerable.Range(0, 120).Select(i => i % 2 == 0
            ? Bar(i, Friday.AddSeconds(i)) : Gap(i, Friday.AddSeconds(i))).ToArray();
        var axis = PriceTimeAxis.Create(Aggregate(PriceTimeInterval.Minute5, bars));
        Assert.Equal(120, axis.Aggregation.Segments.Length);
        Assert.Equal(1, axis.BucketCount);
        Assert.True(axis.FinancialWholeRange!.IsFinancialZoomAllowed);
        Assert.False(new PriceTimeAxisRange(-100, 1).IsFinancialZoomAllowed);
        Assert.True(new PriceTimeAxisRange(-.25, 89.75).IsFinancialZoomAllowed);
        Assert.False(new PriceTimeAxisRange(-.25, Math.BitIncrement(89.75)).IsFinancialZoomAllowed);
    }

    [Theory]
    [InlineData(PriceTimeInterval.Minute1, 300, 10)]
    [InlineData(PriceTimeInterval.Minute5, 60, 2)]
    [InlineData(PriceTimeInterval.Minute15, 60, 2)]
    [InlineData(PriceTimeInterval.Minute30, 60, 2)]
    [InlineData(PriceTimeInterval.Hour1, 60, 2)]
    [InlineData(PriceTimeInterval.Hour4, 60, 2)]
    public void Initial_date_bounds_have_distinct_numeric_units_and_whole_range_margins(
        PriceTimeInterval interval, int expectedLength, int rightUnits)
    {
        var duration = PriceTimeAggregator.GetDuration(interval);
        var original = Enumerable.Range(0, 101).Select(i => Bar(i,
            Friday.AddTicks((i - 100L) * duration.Ticks).AddSeconds(40))).ToArray();
        var aggregation = Aggregate(interval, original);
        var view = PriceFinancialViewport.CreateInitial(aggregation);
        Assert.Equal(new PriceTimeAxisRange(100 + rightUnits - expectedLength, 100 + rightUnits), view.RequestedAxisRange);
        Assert.Equal(new PriceTimeAxisRange(-2, 102), view.TimeAxis.FinancialWholeRange);
        Assert.Equal(101, view.TimeAxis.BucketCount);
        // The shared projection now renders requested padding rather than stretching slots.
        var query = aggregation.Source.Query(view.Options);
        Assert.Equal(interval == PriceTimeInterval.Minute1 ? 190d / 300 : 0, query.Candles[0].CenterX);
        Assert.Equal(1 - rightUnits / (double)expectedLength, query.Candles[^1].CenterX, 12);
        Assert.Equal(interval != PriceTimeInterval.Minute1, view.RequestedAxisRange!.IsFinancialZoomAllowed);
    }

    [Fact]
    public void Sparse_initial_range_is_compressed_and_keeps_two_future_units()
    {
        var view = PriceFinancialViewport.CreateInitial(Aggregate(PriceTimeInterval.Minute5,
            Bar(0, Friday.AddDays(-1)), Bar(1, Friday.AddMinutes(-60)), Bar(2, Friday.AddSeconds(40))));
        Assert.Equal(new PriceTimeAxisRange(0, 4), view.RequestedAxisRange);
        Assert.Equal(3, view.TimeAxis.BucketCount);
        Assert.Equal(2, view.Options.BarCount);
        Assert.Equal(0d, view.TimeAxis.ToCoordinate(view.RequestedMinimumUtc!.Value));
    }

    [Fact]
    public void Range_zoom_and_pan_preserve_anchor_and_do_not_silently_apply_interaction_policy()
    {
        var range = new PriceTimeAxisRange(-10, 90);
        var zoom = range.Zoom(2, .25);
        Assert.Equal(new PriceTimeAxisRange(2.5, 52.5), zoom);
        Assert.Equal(range.Denormalize(.25), zoom.Denormalize(.25));
        Assert.Equal(new PriceTimeAxisRange(5, 55), zoom.Pan(2.5));
        Assert.Equal(-.25, zoom.Normalize(-10));
        Assert.Equal(1.25, zoom.Normalize(65));
        Assert.False(range.IsFinancialZoomAllowed);
        Assert.True(zoom.IsFinancialZoomAllowed);
        Assert.False(zoom.Zoom(.5).IsFinancialZoomAllowed);
        Assert.Equal(range, zoom.Zoom(.5, .25));
    }

    [Fact]
    public void Empty_history_single_bucket_and_date_boundaries_are_explicit()
    {
        var empty = PriceTimeAxis.Create(Aggregate(PriceTimeInterval.Minute5));
        Assert.True(empty.IsEmpty);
        Assert.Null(empty.ToCoordinate(Friday));
        Assert.Null(empty.ToTimestamp(0));
        Assert.Null(empty.FinancialWholeRange);
        Assert.False(empty.IsTimestampVisible(Friday));
        Assert.Null(PriceFinancialViewport.CreateInitial(empty.Aggregation).RequestedAxisRange);

        var single = PriceTimeAxis.Create(Aggregate(PriceTimeInterval.Minute5, Gap(1, Friday)));
        Assert.Equal(1, single.BucketCount);
        Assert.Equal(new PriceTimeAxisRange(-2, 2), single.FinancialWholeRange);
        Assert.Equal(Friday, single.ToTimestamp(0));
        Assert.Equal(Friday.AddMinutes(3), single.ToTimestamp(.5));
        Assert.Null(single.ToTimestamp(double.MaxValue));
        Assert.Null(single.ToTimestamp(-double.MaxValue));

        var minimum = PriceTimeAxis.Create(Aggregate(PriceTimeInterval.Minute1, Bar(1, DateTimeOffset.MinValue)));
        Assert.Equal(DateTimeOffset.MinValue, minimum.ToTimestamp(0));
        Assert.Null(minimum.ToTimestamp(-.01));
        var maximum = PriceTimeAxis.Create(Aggregate(PriceTimeInterval.Minute1, Bar(1, DateTimeOffset.MaxValue)));
        Assert.Equal(0d, maximum.ToCoordinate(DateTimeOffset.MaxValue));
        Assert.Equal(maximum.GetBucketStart(0), maximum.ToTimestamp(0));
        Assert.Null(maximum.ToTimestamp(1));
        Assert.Null(maximum.ToTimestamp(.5)); // rounding must not cross year 9999 silently
    }

    [Fact]
    public void Nonfinite_and_degenerate_ranges_or_navigation_are_rejected()
    {
        var axis = PriceTimeAxis.Create(Aggregate(PriceTimeInterval.Minute1));
        Assert.Throws<ArgumentNullException>(() => PriceTimeAxis.Create(null!));
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => axis.ToTimestamp(invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PriceTimeAxisRange(invalid, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PriceTimeAxisRange(0, invalid));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceTimeAxisRange(1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceTimeAxisRange(2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceTimeAxisRange(-double.MaxValue, double.MaxValue));
        var range = new PriceTimeAxisRange(0, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => range.Zoom(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => range.Zoom(2, 1.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => range.Pan(double.NaN));
    }

    [Fact]
    public void Full_official_scale_remains_bounded_and_originals_map_to_their_bucket()
    {
        using var csv = new StringReader(string.Concat(Enumerable.Repeat("2,4,1,3,5\n", 18000)));
        var source = PriceFinancialCsv.ReadInitial(csv, Friday.AddDays(3).AddSeconds(40));
        var aggregation = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var axis = PriceTimeAxis.Create(aggregation);
        Assert.Equal(18000, source.Bars.Length);
        Assert.InRange(axis.BucketCount, 3600, 3604);
        foreach (var pair in aggregation.Segments.Select((segment, index) => (segment, index)))
        {
            Assert.Equal((double)axis.GetSegmentCoordinate(pair.index), axis.ToCoordinate(pair.segment.Bar.Timestamp));
            Assert.Equal(pair.segment.BucketStartUtc, axis.ToTimestamp(axis.GetSegmentCoordinate(pair.index)));
        }
        var preserved = axis.ToCoordinate(source.Bars[^1].Timestamp);
        // A new source/map has independent coordinates, never rebases an older axis in place.
        var later = PriceTimeAxis.Create(Aggregate(PriceTimeInterval.Minute5,
            Bar(1, Friday.AddYears(1)), Bar(2, Friday.AddYears(1).AddDays(1))));
        Assert.Equal(1d, later.ToCoordinate(later.Aggregation.OriginalSource.Bars[^1].Timestamp));
        Assert.Equal(preserved, axis.ToCoordinate(source.Bars[^1].Timestamp));
    }

    private static PriceTimeAggregationSnapshot Aggregate(PriceTimeInterval interval, params PriceBar[] bars) =>
        PriceTimeAggregator.Aggregate(new PriceChartSource("axis", "Axis test", bars), interval);
    private static PriceBar Bar(long key, DateTimeOffset time) => new(key, time, 10, 12, 8, 11, 3);
    private static PriceBar Gap(long key, DateTimeOffset time) => new(key, time, null, null, null, null);
}
