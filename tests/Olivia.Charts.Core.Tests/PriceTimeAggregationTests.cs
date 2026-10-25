using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceTimeAggregationTests
{
    [Theory]
    [InlineData(PriceTimeInterval.Minute1, 1, "1m")]
    [InlineData(PriceTimeInterval.Minute5, 5, "5m")]
    [InlineData(PriceTimeInterval.Minute15, 15, "15m")]
    [InlineData(PriceTimeInterval.Minute30, 30, "30m")]
    [InlineData(PriceTimeInterval.Hour1, 60, "1h")]
    [InlineData(PriceTimeInterval.Hour4, 240, "4h")]
    public void EveryIntervalUsesExactHalfOpenUtcBoundaries(PriceTimeInterval interval, int minutes, string label)
    {
        var duration = TimeSpan.FromMinutes(minutes);
        var boundary = Epoch.AddTicks(2 * duration.Ticks);
        var source = Source(Bar(0, boundary.AddTicks(-1)), Bar(1, boundary),
            Bar(2, boundary.AddTicks(1)), Bar(3, boundary.AddTicks(duration.Ticks - 1)),
            Bar(4, boundary.Add(duration)));
        var snapshot = PriceTimeAggregator.Aggregate(source, interval);

        Assert.Equal(duration, PriceTimeAggregator.GetDuration(interval));
        Assert.Equal(label, PriceTimeAggregator.GetLabel(interval));
        Assert.Equal(interval, snapshot.Interval);
        Assert.Equal(new[] { 1, 3, 1 }, snapshot.Segments.Select(segment => segment.OriginalCount));
        Assert.Equal(new[] { 0, 1, 4 }, snapshot.Segments.Select(segment => segment.OriginalStartIndex));
        Assert.Equal(boundary.Subtract(duration), snapshot.Segments[0].BucketStartUtc);
        Assert.Equal(boundary, snapshot.Segments[1].BucketStartUtc);
        Assert.Equal(boundary.Add(duration), snapshot.Segments[2].BucketStartUtc);
        Assert.All(snapshot.Segments, segment =>
        {
            Assert.Equal(TimeSpan.Zero, segment.BucketStartUtc.Offset);
            Assert.Equal(segment.BucketStartUtc.UtcTicks + duration.Ticks, segment.BucketEndUtcTicksExclusive);
            Assert.Equal(new DateTimeOffset(segment.BucketEndUtcTicksExclusive, TimeSpan.Zero), segment.BucketEndUtc);
            foreach (var bar in source.Bars.Skip(segment.OriginalStartIndex).Take(segment.OriginalCount))
            {
                Assert.True(bar.Timestamp.UtcTicks >= segment.BucketStartUtc.UtcTicks);
                Assert.True(bar.Timestamp.UtcTicks < segment.BucketEndUtcTicksExclusive);
            }
        });
    }

    [Theory]
    [InlineData(PriceTimeInterval.Minute1)]
    [InlineData(PriceTimeInterval.Minute5)]
    [InlineData(PriceTimeInterval.Minute15)]
    [InlineData(PriceTimeInterval.Minute30)]
    [InlineData(PriceTimeInterval.Hour1)]
    [InlineData(PriceTimeInterval.Hour4)]
    public void BucketsUseUtcRatherThanOffsetLocalDatesOrUnixEpochTruncation(PriceTimeInterval interval)
    {
        var midnight = DateTimeOffset.UnixEpoch;
        var start = midnight.Subtract(PriceTimeAggregator.GetDuration(interval));
        var source = Source(Bar(0, start.AddTicks(1).ToOffset(TimeSpan.FromHours(-7))),
            Bar(1, midnight.AddTicks(-1).ToOffset(TimeSpan.FromMinutes(345))),
            Bar(2, midnight.ToOffset(TimeSpan.FromHours(14))));
        var snapshot = PriceTimeAggregator.Aggregate(source, interval);

        Assert.Equal(new[] { 2, 1 }, snapshot.Segments.Select(segment => segment.OriginalCount));
        Assert.Equal(start, snapshot.Segments[0].BucketStartUtc);
        Assert.Equal(midnight, snapshot.Segments[0].BucketEndUtc);
        Assert.Equal(midnight, snapshot.Segments[1].BucketStartUtc);
        Assert.Equal(source.Bars[0].Timestamp.Ticks, snapshot.Source.Bars[0].Timestamp.Ticks);
        Assert.Equal(TimeSpan.FromHours(-7), snapshot.Source.Bars[0].Timestamp.Offset);
        Assert.Equal(TimeSpan.FromHours(14), snapshot.Source.Bars[1].Timestamp.Offset);
    }

    [Theory]
    [InlineData(PriceTimeInterval.Minute1)]
    [InlineData(PriceTimeInterval.Minute5)]
    [InlineData(PriceTimeInterval.Minute15)]
    [InlineData(PriceTimeInterval.Minute30)]
    [InlineData(PriceTimeInterval.Hour1)]
    [InlineData(PriceTimeInterval.Hour4)]
    public void DateTimeExtremesRetainExactUnrepresentableExclusiveEndWithoutInventingEmptyBuckets(PriceTimeInterval interval)
    {
        var source = Source(Bar(0, DateTimeOffset.MinValue), Bar(1, DateTimeOffset.MinValue.AddTicks(1)),
            Bar(2, DateTimeOffset.MaxValue.AddTicks(-1)), Bar(3, DateTimeOffset.MaxValue));
        var snapshot = PriceTimeAggregator.Aggregate(source, interval);
        var durationTicks = PriceTimeAggregator.GetDuration(interval).Ticks;
        var afterMaximum = DateTimeOffset.MaxValue.UtcTicks + 1;

        Assert.Equal(2, snapshot.Segments.Length);
        Assert.Equal(new[] { 2, 2 }, snapshot.Segments.Select(segment => segment.OriginalCount));
        Assert.Equal(DateTimeOffset.MinValue, snapshot.Segments[0].BucketStartUtc);
        Assert.Equal(durationTicks, snapshot.Segments[0].BucketEndUtcTicksExclusive);
        Assert.Equal(afterMaximum - durationTicks, snapshot.Segments[1].BucketStartUtc.UtcTicks);
        Assert.Equal(afterMaximum, snapshot.Segments[1].BucketEndUtcTicksExclusive);
        Assert.Null(snapshot.Segments[1].BucketEndUtc);
        Assert.Equal(source.Bars[2].Timestamp, snapshot.Source.Bars[1].Timestamp);
    }

    [Fact]
    public void FinancialOhlcAndSumHaveHandCalculatedValuesAndContiguousOriginalProvenance()
    {
        var source = new PriceChartSource("raw-prices", "Raw prices", [
            new(11, Epoch.ToOffset(TimeSpan.FromMinutes(330)), 10, 13, 9, 12, 10),
            new(22, Epoch.AddMinutes(1).ToOffset(TimeSpan.FromHours(-4)), 12, 20, 5, 11, 5),
            new(33, Epoch.AddMinutes(4), 11, 19, 7, 18, 20),
            new(44, Epoch.AddMinutes(5), 18, 22, 17, 21, 30),
            new(55, Epoch.AddMinutes(17), 21, 23, 20, 22, 40)], 17, 23);
        var snapshot = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);

        Assert.Same(source, snapshot.OriginalSource);
        Assert.Equal(17, snapshot.Source.DatasetVersion);
        Assert.Equal(23, snapshot.Source.SourceVersion);
        Assert.Equal(new[] { 0, 3, 4 }, snapshot.Segments.Select(segment => segment.OriginalStartIndex));
        Assert.Equal(new[] { 3, 1, 1 }, snapshot.Segments.Select(segment => segment.OriginalCount));
        Assert.Equal(new PriceBar(11, source.Bars[0].Timestamp, 10, 20, 5, 18, 35), snapshot.Source.Bars[0]);
        Assert.Equal(source.Bars[3], snapshot.Source.Bars[1]);
        Assert.Equal(source.Bars[4], snapshot.Source.Bars[2]);
        Assert.Equal(Epoch.AddMinutes(15), snapshot.Segments[2].BucketStartUtc);
        Assert.Equal(snapshot.Source.Bars.ToArray(), snapshot.Segments.Select(segment => segment.Bar).ToArray());
        Assert.All(snapshot.Segments, segment =>
        {
            Assert.Equal(PriceAggregateVolumeStatus.Complete, segment.VolumeStatus);
            Assert.Equal(0, segment.MissingVolumeCount);
        });
        Assert.Equal(Enumerable.Range(0, source.Bars.Length), snapshot.Segments.SelectMany(segment =>
            Enumerable.Range(segment.OriginalStartIndex, segment.OriginalCount)));
    }

    [Fact]
    public void EachExplicitGapSurvivesAndSplitsRunsEvenInsideTheSameBucket()
    {
        var source = Source(Bar(0, Epoch), Bar(1, Epoch.AddSeconds(1)), Gap(2, Epoch.AddSeconds(2)),
            Gap(3, Epoch.AddSeconds(3).ToOffset(TimeSpan.FromHours(-5))),
            Bar(4, Epoch.AddSeconds(4)), Bar(5, Epoch.AddSeconds(5)), Gap(6, Epoch.AddSeconds(6)),
            Bar(7, Epoch.AddMinutes(2)));
        var snapshot = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute1);

        Assert.Equal(new[] { 0, 2, 3, 4, 6, 7 }, snapshot.Segments.Select(segment => segment.OriginalStartIndex));
        Assert.Equal(new[] { 2, 1, 1, 2, 1, 1 }, snapshot.Segments.Select(segment => segment.OriginalCount));
        Assert.Equal(new[] { false, true, true, false, true, false }, snapshot.Source.Bars.Select(bar => bar.IsGap));
        Assert.All(snapshot.Segments.Take(5), segment => Assert.Equal(Epoch, segment.BucketStartUtc));
        foreach (var segment in snapshot.Segments.Where(segment => segment.Bar.IsGap))
        {
            var original = source.Bars[segment.OriginalStartIndex];
            Assert.Equal(original, segment.Bar);
            Assert.Equal(original.Timestamp.Offset, segment.Bar.Timestamp.Offset);
            Assert.Equal(PriceAggregateVolumeStatus.Gap, segment.VolumeStatus);
            Assert.Equal(1, segment.OriginalCount);
            Assert.Equal(1, segment.MissingVolumeCount);
        }
        for (var i = 1; i < snapshot.Source.Bars.Length; i++)
            Assert.True(snapshot.Source.Bars[i].Timestamp > snapshot.Source.Bars[i - 1].Timestamp);
        Assert.Equal(3, snapshot.Source.Query().Gaps.Length);
    }

    [Fact]
    public void OhlcTieSelectionAndOriginalSignedZeroBitsAreRetainedWhileAggregateZeroVolumeIsPositive()
    {
        var source = Source(new(1, Epoch, NegativeZero, NegativeZero, 0, 0, NegativeZero),
            new(2, Epoch.AddTicks(1), 0, 0, NegativeZero, NegativeZero, 0));
        var before = source.Bars.SelectMany(Bits).ToArray();
        var snapshot = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute1);
        var bar = Assert.Single(snapshot.Source.Bars);

        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(bar.Open!.Value));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(bar.High!.Value));
        Assert.Equal(0L, BitConverter.DoubleToInt64Bits(bar.Low!.Value));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(bar.Close!.Value));
        Assert.Equal(0L, BitConverter.DoubleToInt64Bits(bar.Volume!.Value));
        Assert.Equal(before, source.Bars.SelectMany(Bits).ToArray());
        Assert.Equal(PriceAggregateVolumeStatus.Complete, snapshot.Segments[0].VolumeStatus);
    }

    [Fact]
    public void UnknownVolumeIsNeverZeroOrPartialSumAndTakesPrecedenceOverKnownSubtotalOverflow()
    {
        foreach (var volumes in new double?[][] { [10, null, 20], [null, null], [double.MaxValue, double.MaxValue, null] })
        {
            var snapshot = Sum(volumes);
            var segment = Assert.Single(snapshot.Segments);
            Assert.Equal(PriceAggregateVolumeStatus.Missing, segment.VolumeStatus);
            Assert.Equal(volumes.Count(volume => volume is null), segment.MissingVolumeCount);
            Assert.Null(segment.Bar.Volume);
            Assert.False(segment.Bar.IsGap);
            Assert.Equal(10d, segment.Bar.Open);
            Assert.Equal(12d, segment.Bar.High);
            Assert.Equal(8d, segment.Bar.Low);
            Assert.Equal(11d, segment.Bar.Close);
        }
        var zero = Assert.Single(Sum(0, NegativeZero).Segments);
        Assert.Equal(PriceAggregateVolumeStatus.Complete, zero.VolumeStatus);
        Assert.Equal(0d, zero.Bar.Volume);
        Assert.Equal(0, zero.MissingVolumeCount);
    }

    [Fact]
    public void ExactVolumeSumRoundsOnceToNearestEvenWithoutLosingSmallValuesOrDependingOnOrder()
    {
        var halfUlpAtOne = Math.ScaleB(1, -53);
        var minimumNormal = Math.ScaleB(1, -1022);
        var cases = new (double[] Values, double Expected)[]
        {
            ([1, halfUlpAtOne], 1),
            ([Math.BitIncrement(1), halfUlpAtOne], Math.BitIncrement(Math.BitIncrement(1))),
            ([1, halfUlpAtOne, halfUlpAtOne], Math.BitIncrement(1)),
            ([Math.BitDecrement(2), halfUlpAtOne], 2),
            ([Math.ScaleB(1, 53), 1, 1], Math.ScaleB(1, 53) + 2),
            ([double.Epsilon, double.Epsilon, double.Epsilon], 3 * double.Epsilon),
            ([Math.BitDecrement(minimumNormal), double.Epsilon], minimumNormal),
            ([minimumNormal, double.Epsilon], Math.BitIncrement(minimumNormal)),
            ([double.MaxValue / 2, double.MaxValue / 2], double.MaxValue),
            (Enumerable.Repeat(1d, 1023).Prepend(Math.ScaleB(1, 53)).ToArray(), Math.ScaleB(1, 53) + 1024)
        };
        foreach (var (values, expected) in cases)
        {
            foreach (var ordered in new[] { values.AsEnumerable(), values.Reverse() })
            {
                var segment = Assert.Single(Sum(ordered.Select(value => (double?)value).ToArray()).Segments);
                Assert.Equal(PriceAggregateVolumeStatus.Complete, segment.VolumeStatus);
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(segment.Bar.Volume!.Value));
                Assert.Equal(values.Length, segment.OriginalCount);
            }
        }
    }

    [Fact]
    public void OverflowUsesExactMathematicalLimitNotRoundedFloatingSumAndDoesNotInvalidateOhlc()
    {
        foreach (var volumes in new double?[][] { [double.MaxValue, double.Epsilon],
            [double.MaxValue, double.MaxValue], [double.MaxValue / 2, double.MaxValue / 2, double.Epsilon] })
        {
            var snapshot = Sum(volumes);
            var segment = Assert.Single(snapshot.Segments);
            Assert.Equal(PriceAggregateVolumeStatus.Overflow, segment.VolumeStatus);
            Assert.Equal(0, segment.MissingVolumeCount);
            Assert.Null(segment.Bar.Volume);
            Assert.False(segment.Bar.IsGap);
            Assert.Single(snapshot.Source.Query().Candles);
        }
        var lastUlp = Math.ScaleB(1, 971);
        var exactMaximum = Assert.Single(Sum(Math.BitDecrement(double.MaxValue), lastUlp).Segments);
        Assert.Equal(PriceAggregateVolumeStatus.Complete, exactMaximum.VolumeStatus);
        Assert.Equal(double.MaxValue, exactMaximum.Bar.Volume);
        Assert.Equal(PriceAggregateVolumeStatus.Overflow,
            Assert.Single(Sum(Math.BitDecrement(double.MaxValue), lastUlp, double.Epsilon).Segments).VolumeStatus);
    }

    [Fact]
    public void IdentityIsDeterministicDistinctBoundedAndVersionIndependentEvenAtOriginalTextLimits()
    {
        var identity = string.Concat(Enumerable.Repeat("\U0001F9ED", BarChartSource.MaximumTextLength / 2));
        var label = new string('L', BarChartSource.MaximumTextLength);
        var source = new PriceChartSource(identity, label, [Bar(0, Epoch)], 17, 23);
        var newer = new PriceChartSource(identity, label, source.Bars, 18, 24);
        var identities = new HashSet<string>();
        foreach (var interval in Enum.GetValues<PriceTimeInterval>())
        {
            var snapshot = PriceTimeAggregator.Aggregate(source, interval);
            var repeated = PriceTimeAggregator.Aggregate(source, interval);
            var updated = PriceTimeAggregator.Aggregate(newer, interval);
            Assert.Same(source, snapshot.OriginalSource);
            Assert.NotEqual(identity, snapshot.Source.SeriesId);
            Assert.StartsWith($"agg:{PriceTimeAggregator.GetLabel(interval)}:", snapshot.Source.SeriesId);
            Assert.InRange(snapshot.Source.SeriesId.Length, 1, BarChartSource.MaximumTextLength);
            Assert.InRange(snapshot.Source.Label.Length, 1, BarChartSource.MaximumTextLength);
            Assert.Equal(snapshot.Source.SeriesId, repeated.Source.SeriesId);
            Assert.Equal(snapshot.Source.SeriesId, updated.Source.SeriesId);
            Assert.Equal(18, updated.Source.DatasetVersion);
            Assert.Equal(24, updated.Source.SourceVersion);
            Assert.True(identities.Add(snapshot.Source.SeriesId));
        }
        Assert.Equal(identity, source.SeriesId);
        Assert.Equal(label, source.Label);
    }

    [Fact]
    public void ViewportIsAppliedAfterFullAggregationAndIntervalChangesCanRecoverEveryOriginalRow()
    {
        var source = Source(Enumerable.Range(0, 10).Select(index => Bar(index, Epoch.AddMinutes(index), index + 1)).ToArray());
        var before = source.Bars.ToArray();
        var narrowOriginal = source.Query(new(barStart: 6, barCount: 2));
        var snapshot = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var narrowDerived = snapshot.Source.Query(new(barStart: 1, barCount: 1));

        Assert.Equal(2, narrowOriginal.BarCount);
        Assert.Equal(new[] { 5, 5 }, snapshot.Segments.Select(segment => segment.OriginalCount));
        Assert.Equal(40d, Assert.Single(narrowDerived.Candles).Hit.Bar.Volume);
        Assert.Equal(source.Bars[5].Key, narrowDerived.Candles[0].Hit.Key);
        var fine = PriceTimeAggregator.Aggregate(snapshot.OriginalSource, PriceTimeInterval.Minute1);
        Assert.Same(source, fine.OriginalSource);
        Assert.Equal(before, fine.Source.Bars.ToArray());
        Assert.Equal(before, source.Bars.ToArray());
        Assert.Equal(snapshot.Segments.ToArray(),
            PriceTimeAggregator.Aggregate(fine.OriginalSource, PriceTimeInterval.Minute5).Segments.ToArray());
    }

    [Fact]
    public void EmptyAndMaximumSourcesRemainBoundedWithEveryRowMappedExactlyOnce()
    {
        var empty = Source();
        var emptySnapshot = PriceTimeAggregator.Aggregate(empty, PriceTimeInterval.Hour4);
        Assert.Same(empty, emptySnapshot.OriginalSource);
        Assert.Empty(emptySnapshot.Source.Bars);
        Assert.Empty(emptySnapshot.Segments);
        Assert.Empty(emptySnapshot.Source.Query().Candles);
        foreach (var tickSpacing in new[] { 1L, TimeSpan.TicksPerMinute })
        {
            var source = Source(Enumerable.Range(0, PriceChartSource.MaximumBars)
                .Select(index => Bar(index, Epoch.AddTicks(index * tickSpacing))).ToArray());
            var snapshot = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute1);
            Assert.Equal(PriceChartSource.MaximumBars, snapshot.OriginalSource.Bars.Length);
            Assert.Equal(tickSpacing == 1 ? 1 : PriceChartSource.MaximumBars, snapshot.Source.Bars.Length);
            Assert.Equal(PriceChartSource.MaximumBars, snapshot.Segments.Sum(segment => segment.OriginalCount));
            Assert.Equal(Enumerable.Range(0, PriceChartSource.MaximumBars), snapshot.Segments.SelectMany(segment =>
                Enumerable.Range(segment.OriginalStartIndex, segment.OriginalCount)));
            Assert.Equal((double)PriceChartSource.MaximumBars, snapshot.Source.Bars.Sum(bar => bar.Volume!.Value));
        }
    }

    [Fact]
    public void DerivedSourceWorksWithExistingPriceVolumeAndIndicatorApisWithoutChangingOriginalResults()
    {
        var source = Source(Enumerable.Range(0, 8).Select(index => new PriceBar(index, Epoch.AddMinutes(index),
            100 + index, 103 + index, 98 + index, 101 + index, index + 1)).ToArray());
        var originalBands = PriceBollingerBands.Calculate(source, 2).Points.ToArray();
        var snapshot = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var prices = snapshot.Source.Query();
        var volumes = PriceVolumeGeometry.Create(prices);
        var band = Assert.Single(PriceBollingerBands.Calculate(snapshot.Source, 2).Points);
        var cci = Assert.Single(PriceCommodityChannelIndex.Calculate(snapshot.Source, 2).Points);
        var williams = Assert.Single(PriceWilliamsR.Calculate(snapshot.Source, 2).Points);

        Assert.Equal(2, prices.Candles.Length);
        Assert.Equal(new HistoryValueDomain(98, 110), prices.Domain);
        Assert.Equal(new[] { 15d, 21d }, volumes.Columns.Select(column => column.Hit.Bar.Volume!.Value));
        Assert.Equal(new HistoryValueDomain(0, 21), volumes.Domain);
        Assert.Equal(106.5, band.Middle);
        Assert.Equal(109.5, band.Upper);
        Assert.Equal(103.5, band.Lower);
        Assert.Equal(200d / 3, cci.Value, 12);
        Assert.Equal(-100d / 6, williams.Value, 12);
        Assert.Equal(originalBands, PriceBollingerBands.Calculate(source, 2).Points.ToArray());
        Assert.All(prices.Candles, candle => Assert.Equal(snapshot.Source.SeriesId, candle.Hit.SeriesId));
    }

    [Fact]
    public void NullSourceAndEveryInvalidIntervalAreRejected()
    {
        Assert.Equal("source", Assert.Throws<ArgumentNullException>(() =>
            PriceTimeAggregator.Aggregate(null!, PriceTimeInterval.Minute1)).ParamName);
        foreach (var interval in new[] { (PriceTimeInterval)(-1), (PriceTimeInterval)6, (PriceTimeInterval)int.MaxValue })
        {
            Assert.Equal("interval", Assert.Throws<ArgumentOutOfRangeException>(() => PriceTimeAggregator.GetDuration(interval)).ParamName);
            Assert.Equal("interval", Assert.Throws<ArgumentOutOfRangeException>(() => PriceTimeAggregator.GetLabel(interval)).ParamName);
            Assert.Equal("interval", Assert.Throws<ArgumentOutOfRangeException>(() => PriceTimeAggregator.Aggregate(Source(), interval)).ParamName);
        }
    }

    private static DateTimeOffset Epoch => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static double NegativeZero => BitConverter.Int64BitsToDouble(long.MinValue);
    private static PriceBar Bar(int index, DateTimeOffset timestamp, double? volume = 1) =>
        new(100L + index * 7, timestamp, 10, 12, 8, 11, volume);
    private static PriceBar Gap(int index, DateTimeOffset timestamp) =>
        new(100L + index * 7, timestamp, null, null, null, null, null);
    private static PriceChartSource Source(params PriceBar[] bars) => new("prices", "Prices", bars, 17, 23);
    private static PriceTimeAggregationSnapshot Sum(params double?[] volumes) =>
        PriceTimeAggregator.Aggregate(Source(volumes.Select((volume, index) =>
            Bar(index, Epoch.AddTicks(index), volume)).ToArray()), PriceTimeInterval.Minute1);
    private static IEnumerable<long?> Bits(PriceBar bar) =>
        new[] { bar.Open, bar.High, bar.Low, bar.Close, bar.Volume }
            .Select(value => value.HasValue ? (long?)BitConverter.DoubleToInt64Bits(value.Value) : null);
}
