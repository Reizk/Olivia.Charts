using System.Globalization;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceFinancialViewportTests
{
    [Theory]
    [InlineData(PriceTimeInterval.Minute1, 5, "2026-10-22T07:10:00Z", "2026-10-22T12:10:00Z")]
    [InlineData(PriceTimeInterval.Minute5, 5, "2026-10-22T07:10:00Z", "2026-10-22T12:10:00Z")]
    [InlineData(PriceTimeInterval.Minute15, 15, "2026-10-21T21:30:00Z", "2026-10-22T12:30:00Z")]
    [InlineData(PriceTimeInterval.Minute30, 30, "2026-10-21T07:00:00Z", "2026-10-22T13:00:00Z")]
    [InlineData(PriceTimeInterval.Hour1, 60, "2026-10-20T02:00:00Z", "2026-10-22T14:00:00Z")]
    [InlineData(PriceTimeInterval.Hour4, 240, "2026-10-12T20:00:00Z", "2026-10-22T20:00:00Z")]
    public void EveryInitialWindowMatchesIndependentUtcGoldens(PriceTimeInterval interval,
        int effectiveMinutes, string minimum, string maximum)
    {
        var original = Source(Bar(0, Last));
        var aggregation = PriceTimeAggregator.Aggregate(original, interval);

        var plan = PriceFinancialViewport.CreateInitial(aggregation);

        Assert.Same(aggregation, plan.Aggregation);
        Assert.Same(aggregation.Source, plan.Source);
        Assert.Same(original, plan.Aggregation.OriginalSource);
        Assert.Equal(TimeSpan.FromMinutes(effectiveMinutes), plan.EffectiveUnit);
        Assert.Equal(Utc(minimum), plan.RequestedMinimumUtc);
        Assert.Equal(Utc(maximum), plan.RequestedMaximumUtc);
        Assert.Equal(TimeSpan.Zero, plan.RequestedMinimumUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, plan.RequestedMaximumUtc!.Value.Offset);
        Assert.Equal(0, plan.Options.BarStart);
        Assert.Equal(1, plan.Options.BarCount);
        Assert.Equal(PriceChartOptions.Default.GeometryBudget, plan.Options.GeometryBudget);
        Assert.Null(plan.Options.FixedDomain);
        Assert.Equal(PriceChartOptions.Default.BodyWidthFraction, plan.Options.BodyWidthFraction);
    }

    [Fact]
    public void LastOriginalTimestampNotLastAggregateSegmentHeadAnchorsTheWindow()
    {
        var original = Source(Bar(10, Utc("2026-10-22T11:45:00Z")),
            Bar(20, Utc("2026-10-22T11:59:45Z")));
        var aggregation = PriceTimeAggregator.Aggregate(original, PriceTimeInterval.Minute15);
        var segment = Assert.Single(aggregation.Segments);
        Assert.Equal(Utc("2026-10-22T11:45:00Z"), segment.Bar.Timestamp);
        Assert.Equal(2, segment.OriginalCount);

        var plan = PriceFinancialViewport.CreateInitial(aggregation);

        Assert.Equal(Utc("2026-10-21T21:29:45Z"), plan.RequestedMinimumUtc);
        Assert.Equal(Utc("2026-10-22T12:29:45Z"), plan.RequestedMaximumUtc);
        Assert.Equal(1, plan.Options.BarCount);
        Assert.Same(aggregation.Source, plan.Source);
    }

    [Fact]
    public void AFinalExplicitGapStillAnchorsTheWindowWithoutInventingAPrice()
    {
        var original = Source(Bar(10, Last.AddMinutes(-1)), Gap(20, Last));
        var aggregation = PriceTimeAggregator.Aggregate(original, PriceTimeInterval.Minute5);

        var plan = PriceFinancialViewport.CreateInitial(aggregation);
        var query = plan.Source.Query(plan.Options);

        Assert.Equal(Utc("2026-10-22T07:10:00Z"), plan.RequestedMinimumUtc);
        Assert.Equal(Utc("2026-10-22T12:10:00Z"), plan.RequestedMaximumUtc);
        Assert.Equal(2, query.BarCount);
        Assert.Equal(10, Assert.Single(query.Candles).Hit.Key);
        Assert.Equal(original.Bars[1], Assert.Single(query.Gaps).Bar);
    }

    [Fact]
    public void MinuteOneAndFiveShareThreeHundredMinutesAndRenderPaddingWithoutARowCap()
    {
        var dense = DenseSource();
        var sparse = Source(Bar(0, Last.AddDays(-1)), Bar(1, Utc("2026-10-22T07:09:00Z")),
            Bar(2, Utc("2026-10-22T07:10:00Z")), Bar(3, Utc("2026-10-22T09:00:00Z")), Bar(4, Last));
        var one = PriceFinancialViewport.CreateInitial(PriceTimeAggregator.Aggregate(dense, PriceTimeInterval.Minute1));
        var five = PriceFinancialViewport.CreateInitial(PriceTimeAggregator.Aggregate(dense, PriceTimeInterval.Minute5));
        var sparseOne = PriceFinancialViewport.CreateInitial(PriceTimeAggregator.Aggregate(sparse, PriceTimeInterval.Minute1));
        var sparseFive = PriceFinancialViewport.CreateInitial(PriceTimeAggregator.Aggregate(sparse, PriceTimeInterval.Minute5));

        foreach (var plan in new[] { one, five, sparseOne, sparseFive })
        {
            Assert.Equal(Utc("2026-10-22T07:10:00Z"), plan.RequestedMinimumUtc);
            Assert.Equal(Utc("2026-10-22T12:10:00Z"), plan.RequestedMaximumUtc);
            Assert.Equal(TimeSpan.FromMinutes(300), plan.RequestedMaximumUtc!.Value - plan.RequestedMinimumUtc!.Value);
            var query = plan.Source.Query(plan.Options);
            Assert.Equal(plan.Options.BarCount, query.Candles.Length);
            Assert.Equal(0, query.Candles[0].CenterX);
            Assert.Equal((query.BarCount - 1) / plan.RequestedAxisRange!.Length, query.Candles[^1].CenterX, 12);
            Assert.Equal(Last, query.Candles[^1].Hit.Timestamp);
            Assert.Empty(query.Gaps);
        }
        Assert.Equal(601, one.Source.Bars.Length);
        Assert.Equal(310, one.Options.BarStart);
        Assert.Equal(291, one.Options.BarCount);
        Assert.Equal(121, five.Source.Bars.Length);
        Assert.Equal(62, five.Options.BarStart);
        Assert.Equal(59, five.Options.BarCount);
        Assert.Equal(2, sparseOne.Options.BarStart);
        Assert.Equal(3, sparseOne.Options.BarCount);
        Assert.Equal(2, sparseFive.Options.BarStart);
        Assert.Equal(3, sparseFive.Options.BarCount);
    }

    [Fact]
    public void AHalfOpenBucketEndingExactlyAtTheRequestedMinimumIsExcluded()
    {
        var boundary = Utc("2026-10-22T07:10:00Z");
        var original = Source(Bar(10, boundary.AddTicks(-1)), Bar(20, boundary),
            Bar(30, boundary.AddMinutes(5).AddTicks(-1)), Bar(40, Last));
        var aggregation = PriceTimeAggregator.Aggregate(original, PriceTimeInterval.Minute5);

        var plan = PriceFinancialViewport.CreateInitial(aggregation);
        var query = plan.Source.Query(plan.Options);

        Assert.Equal(boundary, plan.RequestedMinimumUtc);
        Assert.Equal(boundary.UtcTicks, aggregation.Segments[0].BucketEndUtcTicksExclusive);
        Assert.Equal(boundary, aggregation.Segments[1].BucketStartUtc);
        Assert.Equal(1, plan.Options.BarStart);
        Assert.Equal(2, plan.Options.BarCount);
        Assert.Equal(new long[] { 20, 40 }, query.Candles.Select(candle => candle.Hit.Key));
        Assert.Equal(2, aggregation.Segments[1].OriginalCount);
        Assert.Equal(original.Bars[1].Timestamp, query.Candles[0].Hit.Timestamp);
    }

    [Fact]
    public void AnIntersectingBucketRetainsAllItsSegmentsAndGapsBeforeTheMinimum()
    {
        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        var original = new PriceChartSource("original-financial", "Original financial", [
            Bar(100, Utc("2026-10-22T07:09:59Z")),
            new(101, Utc("2026-10-22T07:10:00Z").ToOffset(TimeSpan.FromHours(8)), negativeZero, negativeZero, 0, 0, negativeZero),
            Gap(102, Utc("2026-10-22T07:10:10Z").ToOffset(TimeSpan.FromHours(-4))),
            Bar(103, Utc("2026-10-22T07:11:00Z"), 23),
            Gap(104, Utc("2026-10-22T07:11:10Z")),
            Bar(105, Utc("2026-10-22T07:12:00Z"), 25),
            Bar(106, Utc("2026-10-22T07:14:59Z"), 26),
            Bar(107, Last, 27),
            Bar(108, Utc("2026-10-22T12:02:30Z"), 28)], 17, 23);
        var aggregation = PriceTimeAggregator.Aggregate(original, PriceTimeInterval.Minute5);
        var originalBefore = original.Bars.ToArray();
        var derivedBefore = aggregation.Source.Bars.ToArray();
        var segmentsBefore = aggregation.Segments.ToArray();
        var originalBits = original.Bars.SelectMany(Bits).ToArray();
        var derivedBits = aggregation.Source.Bars.SelectMany(Bits).ToArray();

        var plan = PriceFinancialViewport.CreateInitial(aggregation);
        var query = plan.Source.Query(plan.Options);

        Assert.Equal(Utc("2026-10-22T07:12:30Z"), plan.RequestedMinimumUtc);
        Assert.Equal(Utc("2026-10-22T12:12:30Z"), plan.RequestedMaximumUtc);
        Assert.Equal(1, query.BarStart);
        Assert.Equal(6, query.BarCount);
        Assert.Equal(new long[] { 101, 103, 105, 107 }, query.Candles.Select(candle => candle.Hit.Key));
        Assert.Equal(new long[] { 102, 104 }, query.Gaps.Select(gap => gap.Key));
        Assert.All(query.Gaps, gap => Assert.True(gap.Timestamp < plan.RequestedMinimumUtc!.Value));
        Assert.Equal(Utc("2026-10-22T07:10:00Z"), aggregation.Segments[query.BarStart].BucketStartUtc);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 7 }, aggregation.Segments.Skip(query.BarStart).Select(segment => segment.OriginalStartIndex));
        Assert.Equal(new[] { 1, 1, 1, 1, 2, 2 }, aggregation.Segments.Skip(query.BarStart).Select(segment => segment.OriginalCount));
        Assert.Equal(Enumerable.Range(1, 8), aggregation.Segments.Skip(query.BarStart).SelectMany(segment =>
            Enumerable.Range(segment.OriginalStartIndex, segment.OriginalCount)));
        Assert.Same(aggregation, plan.Aggregation);
        Assert.Same(aggregation.Source, plan.Source);
        Assert.Same(original, plan.Aggregation.OriginalSource);
        Assert.Equal(17, query.DatasetVersion);
        Assert.All(query.Candles, candle => Assert.Equal(23, candle.Hit.SourceVersion));
        Assert.Equal(originalBefore, original.Bars.ToArray());
        Assert.Equal(derivedBefore, plan.Source.Bars.ToArray());
        Assert.Equal(segmentsBefore, aggregation.Segments.ToArray());
        Assert.Equal(originalBits, original.Bars.SelectMany(Bits).ToArray());
        Assert.Equal(derivedBits, plan.Source.Bars.SelectMany(Bits).ToArray());
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(query.Candles[0].Hit.Bar.Open!.Value));
        Assert.Equal(TimeSpan.FromHours(8), query.Candles[0].Hit.Timestamp.Offset);
        Assert.Equal(TimeSpan.FromHours(-4), query.Gaps[0].Timestamp.Offset);
    }

    [Theory]
    [InlineData(PriceTimeInterval.Minute1)]
    [InlineData(PriceTimeInterval.Minute5)]
    [InlineData(PriceTimeInterval.Minute15)]
    [InlineData(PriceTimeInterval.Minute30)]
    [InlineData(PriceTimeInterval.Hour1)]
    [InlineData(PriceTimeInterval.Hour4)]
    public void ShortHistoryIsNotExtendedAndEmptyHistoryHasNoRequestedBounds(PriceTimeInterval interval)
    {
        var shortAggregation = PriceTimeAggregator.Aggregate(Source(Bar(10, Last.AddMinutes(-1)), Bar(20, Last)), interval);
        var shortPlan = PriceFinancialViewport.CreateInitial(shortAggregation);
        var emptyAggregation = PriceTimeAggregator.Aggregate(Source(), interval);
        var emptyPlan = PriceFinancialViewport.CreateInitial(emptyAggregation,
            new(barStart: 42, barCount: 100, geometryBudget: 5, fixedDomain: new(1, 20), bodyWidthFraction: .25));

        Assert.Equal(0, shortPlan.Options.BarStart);
        Assert.Equal(2, shortPlan.Options.BarCount);
        Assert.Equal(2, shortPlan.Source.Bars.Length);
        Assert.Equal(2, shortPlan.Source.Query(shortPlan.Options).Candles.Length);
        Assert.True(shortPlan.RequestedMinimumUtc!.Value < shortAggregation.OriginalSource.Bars[0].Timestamp);
        Assert.Null(emptyPlan.RequestedMinimumUtc);
        Assert.Null(emptyPlan.RequestedMaximumUtc);
        Assert.Equal(shortPlan.EffectiveUnit, emptyPlan.EffectiveUnit);
        Assert.Equal(0, emptyPlan.Options.BarStart);
        Assert.Equal(0, emptyPlan.Options.BarCount);
        Assert.Equal(5, emptyPlan.Options.GeometryBudget);
        Assert.Equal(new HistoryValueDomain(1, 20), emptyPlan.Options.FixedDomain);
        Assert.Equal(.25, emptyPlan.Options.BodyWidthFraction);
        Assert.Same(emptyAggregation.Source, emptyPlan.Source);
        var emptyQuery = emptyPlan.Source.Query(emptyPlan.Options);
        Assert.Equal(0, emptyQuery.BarCount);
        Assert.Empty(emptyQuery.Candles);
        Assert.Empty(emptyQuery.Gaps);
        Assert.Null(emptyQuery.Domain);
    }

    [Theory]
    [InlineData(-840)]
    [InlineData(-330)]
    [InlineData(345)]
    [InlineData(840)]
    public void OriginalOffsetsNeverChangeTheRequestedUtcInterval(int offsetMinutes)
    {
        var original = Source(Bar(10, Last.ToOffset(TimeSpan.FromMinutes(offsetMinutes))));
        var plan = PriceFinancialViewport.CreateInitial(PriceTimeAggregator.Aggregate(original, PriceTimeInterval.Minute1));

        Assert.Equal(Utc("2026-10-22T07:10:00Z"), plan.RequestedMinimumUtc);
        Assert.Equal(Utc("2026-10-22T12:10:00Z"), plan.RequestedMaximumUtc);
        Assert.Equal(TimeSpan.Zero, plan.RequestedMinimumUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, plan.RequestedMaximumUtc!.Value.Offset);
        Assert.Equal(TimeSpan.FromMinutes(offsetMinutes), plan.Source.Bars[0].Timestamp.Offset);
        Assert.Equal(original.Bars[0].Timestamp.Ticks, plan.Source.Bars[0].Timestamp.Ticks);
    }

    [Fact]
    public void ValidUtcBoundsAreAcceptedEvenWhenArithmeticAtTheOriginalOffsetWouldOverflow()
    {
        var nearLocalMaximum = new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.FromHours(14));
        var nearLocalMinimum = new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.FromHours(-14));
        var upper = PriceFinancialViewport.CreateInitial(PriceTimeAggregator.Aggregate(Source(Bar(1, nearLocalMaximum)), PriceTimeInterval.Minute1));
        var lower = PriceFinancialViewport.CreateInitial(PriceTimeAggregator.Aggregate(Source(Bar(2, nearLocalMinimum)), PriceTimeInterval.Minute1));

        Assert.Equal(Utc("9999-12-31T05:09:59Z"), upper.RequestedMinimumUtc);
        Assert.Equal(Utc("9999-12-31T10:09:59Z"), upper.RequestedMaximumUtc);
        Assert.Equal(Utc("0001-01-01T09:10:00Z"), lower.RequestedMinimumUtc);
        Assert.Equal(Utc("0001-01-01T14:10:00Z"), lower.RequestedMaximumUtc);
        Assert.Equal(TimeSpan.Zero, upper.RequestedMaximumUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, lower.RequestedMinimumUtc!.Value.Offset);
        Assert.Equal(nearLocalMaximum.Ticks, upper.Source.Bars[0].Timestamp.Ticks);
        Assert.Equal(nearLocalMinimum.Ticks, lower.Source.Bars[0].Timestamp.Ticks);
    }

    [Theory]
    [InlineData(PriceTimeInterval.Minute1, 5)]
    [InlineData(PriceTimeInterval.Minute5, 5)]
    [InlineData(PriceTimeInterval.Minute15, 15)]
    [InlineData(PriceTimeInterval.Minute30, 30)]
    [InlineData(PriceTimeInterval.Hour1, 60)]
    [InlineData(PriceTimeInterval.Hour4, 240)]
    public void UnrepresentableBoundsAreRejectedNotClampedIncludingOneTickPastEachLimit(
        PriceTimeInterval interval, int effectiveMinutes)
    {
        var firstValidLast = DateTimeOffset.MinValue.AddMinutes(58 * effectiveMinutes);
        var finalValidLast = DateTimeOffset.MaxValue.AddMinutes(-2 * effectiveMinutes);
        foreach (var timestamp in new[] { DateTimeOffset.MinValue, firstValidLast.AddTicks(-1),
            finalValidLast.AddTicks(1), DateTimeOffset.MaxValue })
        {
            var aggregation = PriceTimeAggregator.Aggregate(Source(Bar(1, timestamp)), interval);
            Assert.Throws<ArgumentOutOfRangeException>(() => PriceFinancialViewport.CreateInitial(aggregation));
            Assert.Equal(timestamp, Assert.Single(aggregation.OriginalSource.Bars).Timestamp);
            Assert.Equal(timestamp, Assert.Single(aggregation.Source.Bars).Timestamp);
        }
        var firstValid = PriceFinancialViewport.CreateInitial(PriceTimeAggregator.Aggregate(Source(Bar(1, firstValidLast)), interval));
        var finalValid = PriceFinancialViewport.CreateInitial(PriceTimeAggregator.Aggregate(Source(Bar(1, finalValidLast)), interval));
        Assert.Equal(DateTimeOffset.MinValue, firstValid.RequestedMinimumUtc);
        Assert.Equal(DateTimeOffset.MinValue.AddMinutes(60 * effectiveMinutes), firstValid.RequestedMaximumUtc);
        Assert.Equal(DateTimeOffset.MaxValue.AddMinutes(-60 * effectiveMinutes), finalValid.RequestedMinimumUtc);
        Assert.Equal(DateTimeOffset.MaxValue, finalValid.RequestedMaximumUtc);
        Assert.Equal(1, firstValid.Options.BarCount);
        Assert.Equal(1, finalValid.Options.BarCount);
    }

    [Fact]
    public void SelectionReplacesOnlyTheOrdinalRangeAndDoesNotTreatBudgetAsATimeLimit()
    {
        var aggregation = PriceTimeAggregator.Aggregate(DenseSource(), PriceTimeInterval.Minute1);
        var domain = new HistoryValueDomain(0, 1000);
        var supplied = new PriceChartOptions(barStart: 999, barCount: 0, geometryBudget: 7,
            fixedDomain: domain, bodyWidthFraction: .375);

        var plan = PriceFinancialViewport.CreateInitial(aggregation, supplied);
        var query = plan.Source.Query(plan.Options);

        Assert.Equal(310, plan.Options.BarStart);
        Assert.Equal(291, plan.Options.BarCount);
        Assert.Equal(7, plan.Options.GeometryBudget);
        Assert.Equal(domain, plan.Options.FixedDomain);
        Assert.Equal(.375, plan.Options.BodyWidthFraction);
        Assert.Equal(999, supplied.BarStart);
        Assert.Equal(0, supplied.BarCount);
        Assert.NotSame(supplied, plan.Options);
        Assert.Same(plan.Options, query.Options);
        Assert.Equal(291, query.RequiredCandleCount);
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, query.BudgetStatus);
        Assert.Empty(query.Candles);
        Assert.Equal(domain, query.Domain);
        Assert.Equal(601, plan.Source.Bars.Length);
    }

    [Fact]
    public void FullSourceIndicatorsRetainTheirPreViewportWarmupAndOriginalProvenance()
    {
        var original = DenseSource();
        var aggregation = PriceTimeAggregator.Aggregate(original, PriceTimeInterval.Minute1);
        var plan = PriceFinancialViewport.CreateInitial(aggregation);

        var bands = PriceBollingerBands.Calculate(plan.Source, period: 20);
        var firstVisible = Assert.Single(bands.Points.Where(point => point.BarIndex == plan.Options.BarStart));

        Assert.Same(aggregation.Source, bands.Source);
        Assert.Equal(601, bands.Source.Bars.Length);
        Assert.Equal(582, bands.Points.Length);
        Assert.Equal(19, bands.Points[0].BarIndex);
        Assert.Equal(310, firstVisible.BarIndex);
        Assert.Equal(310, firstVisible.Key);
        Assert.Equal(Utc("2026-10-22T07:10:00Z"), firstVisible.Timestamp);
        Assert.Equal(301.5, firstVisible.Middle);
        Assert.Equal(Math.Sqrt(33.25), firstVisible.StandardDeviation, 12);
        Assert.Equal(0, bands.OverflowCount);
        Assert.Equal(original.Bars.ToArray(), aggregation.OriginalSource.Bars.ToArray());
        Assert.Equal(310, aggregation.Segments[firstVisible.BarIndex].OriginalStartIndex);
        Assert.Equal(1, aggregation.Segments[firstVisible.BarIndex].OriginalCount);
    }

    [Fact]
    public void NullAggregationIsRejected()
        => Assert.Throws<ArgumentNullException>(() => PriceFinancialViewport.CreateInitial(null!));

    private static readonly DateTimeOffset Last = Utc("2026-10-22T12:00:00Z");
    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static PriceChartSource Source(params PriceBar[] bars) => new("initial-prices", "Initial prices", bars);
    private static PriceBar Bar(long key, DateTimeOffset timestamp, double close = 10)
        => new(key, timestamp, close, close + 1, close - 1, close, 2);
    private static PriceBar Gap(long key, DateTimeOffset timestamp) => new(key, timestamp, null, null, null, null);
    private static PriceChartSource DenseSource() => Source(Enumerable.Range(0, 601)
        .Select(index => Bar(index, Utc("2026-10-22T02:00:00Z").AddMinutes(index), index + 1)).ToArray());
    private static IEnumerable<long?> Bits(PriceBar bar) => new[] { bar.Open, bar.High, bar.Low, bar.Close, bar.Volume }
        .Select(value => value.HasValue ? (long?)BitConverter.DoubleToInt64Bits(value.Value) : null);
}
