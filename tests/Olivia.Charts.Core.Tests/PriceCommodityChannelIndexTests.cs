using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceCommodityChannelIndexTests
{
    [Fact]
    public void DefaultFourteenPeriodRequiresACompleteWindow()
    {
        var values = Enumerable.Range(1, 14).Select(value => (double?)value).ToArray();
        var source = Source(values);
        var snapshot = PriceCommodityChannelIndex.Calculate(source);
        var point = Assert.Single(snapshot.Points);
        Assert.Same(source, snapshot.Source);
        Assert.Equal(14, snapshot.Period);
        Assert.Equal(0, snapshot.FlatWindowCount);
        Assert.Equal(13, point.BarIndex);
        Assert.Equal(2600d / 21, point.Value, 12);
        Assert.Empty(PriceCommodityChannelIndex.Calculate(Source(values.Take(13).ToArray())).Points);
    }

    [Fact]
    public void AnalyticalVectorUsesHighLowCloseAndMeanAbsoluteDeviationNotRmsOrOpen()
    {
        // Typical prices are 2, 4, 8, 3. The first window has mean 14/3 and MAD 20/9;
        // the second has mean 5 and MAD 2. Neither Open nor Close alone gives these values.
        var source = new PriceChartSource("ohlc", "OHLC", [
            new(10, Time(0), 1, 4, 0, 2),
            new(20, Time(1), 7, 9, 0, 3),
            new(30, Time(2), 12, 12, 3, 9),
            new(40, Time(3), 2, 6, 0, 3)]);
        var snapshot = PriceCommodityChannelIndex.Calculate(source, 3);
        Assert.Equal(new[] { 2, 3 }, snapshot.Points.Select(point => point.BarIndex));
        Assert.Equal(100, snapshot.Points[0].Value);
        Assert.Equal(-200d / 3, snapshot.Points[1].Value, 12);
        Assert.Equal(0, snapshot.FlatWindowCount);
    }

    [Fact]
    public void OldPricesExpireFromTheTrailingWindowRatherThanAccumulating()
    {
        var snapshot = PriceCommodityChannelIndex.Calculate(Source(100, 1, 2, 3), 3);
        Assert.Equal(new[] { 2, 3 }, snapshot.Points.Select(point => point.BarIndex));
        Assert.Equal(-9700d / 197, snapshot.Points[0].Value, 12);
        Assert.Equal(100, snapshot.Points[1].Value);
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(-1d)]
    public void RawValuesAreNotClampedToDisplayBoundsOrThresholdGuides(double sign)
    {
        var source = Source(Enumerable.Repeat<double?>(0, 13).Append(sign).ToArray());
        var point = Assert.Single(PriceCommodityChannelIndex.Calculate(source).Points);
        Assert.Equal(sign * 1400d / 3, point.Value, 12);
        Assert.True(Math.Abs(point.Value) > 150);
    }

    [Fact]
    public void CurrentTypicalPriceAtTheMeanEmitsARealZeroForANonflatWindow()
    {
        var snapshot = PriceCommodityChannelIndex.Calculate(Source(0, 2, 1), 3);
        Assert.Equal(0, Assert.Single(snapshot.Points).Value);
        Assert.Equal(0, snapshot.FlatWindowCount);
    }

    [Fact]
    public void OnlyFullConsecutiveWindowsEmitAndExplicitGapsRestartWarmup()
    {
        var source = Source(null, 1, 2, 3, null, 10, 11, null, 20, 21, 22, 23, null);
        var snapshot = PriceCommodityChannelIndex.Calculate(source, 3);
        Assert.Equal(new[] { 3, 10, 11 }, snapshot.Points.Select(point => point.BarIndex));
        Assert.All(snapshot.Points, point => Assert.Equal(100, point.Value));
        Assert.Equal(0, snapshot.FlatWindowCount);
        foreach (var empty in new[] { Source(1, 2), Source(null, null, null), Source() })
        {
            var result = PriceCommodityChannelIndex.Calculate(empty, 3);
            Assert.Empty(result.Points);
            Assert.Equal(0, result.FlatWindowCount);
        }
    }

    [Fact]
    public void MissingCalendarSessionsDoNotInventAnExplicitGap()
    {
        var source = new PriceChartSource("sessions", "Sessions", [
            new(1, Time(0), 1, 1, 1, 1),
            new(2, Time(30), 3, 3, 3, 3),
            new(3, Time(90), 2, 2, 2, 2)]);
        var point = Assert.Single(PriceCommodityChannelIndex.Calculate(source, 3).Points);
        Assert.Equal(2, point.BarIndex);
        Assert.Equal(0, point.Value);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(25.25)]
    [InlineData(-25.25)]
    [InlineData(double.MaxValue)]
    [InlineData(-double.MaxValue)]
    [InlineData(double.Epsilon)]
    public void FlatCompleteWindowsAreUndefinedNotInventedZeros(double value)
    {
        var source = Source(Enumerable.Repeat<double?>(value, 16).ToArray());
        var snapshot = PriceCommodityChannelIndex.Calculate(source);
        Assert.Empty(snapshot.Points);
        Assert.Equal(3, snapshot.FlatWindowCount);
    }

    [Fact]
    public void DifferentOhlcBarsWithIdenticalTypicalPricesAreStillFlat()
    {
        var source = new PriceChartSource("flat-tp", "Flat typical prices", [
            new(1, Time(0), 8, 9, 0, 3),
            new(2, Time(1), 5, 6, 3, 3),
            new(3, Time(2), 4, 4, 4, 4)]);
        var snapshot = PriceCommodityChannelIndex.Calculate(source, 3);
        Assert.Empty(snapshot.Points);
        Assert.Equal(1, snapshot.FlatWindowCount);
    }

    [Fact]
    public void FlatWindowOmissionLeavesAnOrdinalBreakAndDoesNotRestartHistory()
    {
        var snapshot = PriceCommodityChannelIndex.Calculate(Source(0, 1, 1, 1, 2), 3);
        Assert.Equal(new[] { 2, 4 }, snapshot.Points.Select(point => point.BarIndex));
        Assert.Equal(new[] { 50d, 100d }, snapshot.Points.Select(point => point.Value));
        Assert.Equal(1, snapshot.FlatWindowCount);

        var repeated = PriceCommodityChannelIndex.Calculate(Source(0, 1, 1, 1, 2, 2, 2, 1), 2);
        Assert.Equal(new[] { 1, 4, 7 }, repeated.Points.Select(point => point.BarIndex));
        Assert.Equal(200d / 3, repeated.Points[0].Value, 12);
        Assert.Equal(200d / 3, repeated.Points[1].Value, 12);
        Assert.Equal(-200d / 3, repeated.Points[2].Value, 12);
        Assert.Equal(4, repeated.FlatWindowCount);
    }

    [Fact]
    public void FlatWindowCountExcludesIncompleteHistoryAndGaps()
    {
        var snapshot = PriceCommodityChannelIndex.Calculate(Source(1, 1, null, 2, 2, 2, 3, 3, 3), 3);
        Assert.Equal(new[] { 6, 7 }, snapshot.Points.Select(point => point.BarIndex));
        Assert.Equal(2, snapshot.FlatWindowCount);
    }

    [Fact]
    public void PeriodOneIsAcceptedButEveryCompleteWindowHasZeroDeviation()
    {
        var source = new PriceChartSource("one", "One", [
            new(1, Time(0), 5, 10, 0, 10),
            new(2, Time(1), null, null, null, null),
            new(3, Time(2), 2, 7, -3, 1)]);
        var snapshot = PriceCommodityChannelIndex.Calculate(source, 1);
        Assert.Equal(1, snapshot.Period);
        Assert.Empty(snapshot.Points);
        Assert.Equal(2, snapshot.FlatWindowCount);
    }

    [Fact]
    public void ViewportQueriesKeepOffscreenHistoryAndCannotChangeTheCalculation()
    {
        var source = Source(Enumerable.Range(1, 40).Select(value => (double?)value).ToArray());
        var before = PriceCommodityChannelIndex.Calculate(source);
        var viewport = source.Query(new(barStart: 25, barCount: 10, geometryBudget: 0,
            fixedDomain: new(25, 35)));
        Assert.Equal(25, viewport.BarStart);
        Assert.Empty(viewport.Candles);
        var after = PriceCommodityChannelIndex.Calculate(source);
        Assert.Equal(before.Points.ToArray(), after.Points.ToArray());
        Assert.Same(source, after.Source);
        Assert.Equal(27, after.Points.Length);
        Assert.Equal(13, after.Points[0].BarIndex);
        Assert.Equal(39, after.Points[^1].BarIndex);
        var firstVisible = Assert.Single(after.Points.Where(point => point.BarIndex == viewport.BarStart));
        Assert.Equal(source.Bars[25].Key, firstVisible.Key);
        Assert.Equal(2600d / 21, firstVisible.Value, 12);
        var cropped = new PriceChartSource("crop", "Crop", source.Bars.Skip(25).Take(10));
        Assert.Empty(PriceCommodityChannelIndex.Calculate(cropped).Points);
    }

    [Fact]
    public void OriginalIdentityVersionsTimestampOffsetAndOhlcBitsRemainUntouched()
    {
        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        var values = new double?[] { negativeZero, 0, 1.25, null, -2.5, double.Epsilon, 9 };
        var keys = new[] { 41L, -9, long.MaxValue, 0, long.MinValue, 7, 2 };
        var source = new PriceChartSource("prices", "Prices",
            values.Select((value, index) => new PriceBar(keys[index], Time(index).ToOffset(
                index % 2 == 0 ? TimeSpan.FromMinutes(330) : TimeSpan.FromHours(-4)),
                value, value, value, value)), datasetVersion: 17, sourceVersion: 23);
        var originals = source.Bars.ToArray();
        var beforeBits = originals.SelectMany(Bits).ToArray();
        var snapshot = PriceCommodityChannelIndex.Calculate(source, 2);
        Assert.Same(source, snapshot.Source);
        Assert.Equal("prices", snapshot.Source.SeriesId);
        Assert.Equal("Prices", snapshot.Source.Label);
        Assert.Equal(17, snapshot.Source.DatasetVersion);
        Assert.Equal(23, snapshot.Source.SourceVersion);
        Assert.Equal(originals, source.Bars.ToArray());
        Assert.Equal(keys, source.Bars.Select(bar => bar.Key));
        Assert.Equal(beforeBits, source.Bars.SelectMany(Bits).ToArray());
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(source.Bars[0].Close!.Value));
        Assert.Equal(1, snapshot.FlatWindowCount);
        Assert.All(snapshot.Points, point =>
        {
            var original = originals[point.BarIndex];
            Assert.Equal(original.Key, point.Key);
            Assert.Equal(original.Timestamp, point.Timestamp);
            Assert.Equal(original.Timestamp.Offset, point.Timestamp.Offset);
            Assert.Equal(original.Timestamp.Ticks, point.Timestamp.Ticks);
        });
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(-1d)]
    public void OppositeFiniteExtremesDoNotOverflowTypicalPricesOrDeviations(double sign)
    {
        var snapshot = PriceCommodityChannelIndex.Calculate(
            Source(-sign * double.MaxValue, 0, sign * double.MaxValue), 3);
        var point = Assert.Single(snapshot.Points);
        Assert.True(double.IsFinite(point.Value));
        Assert.Equal(sign * 100, point.Value);
        Assert.Equal(0, snapshot.FlatWindowCount);
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(-1d)]
    public void AdjacentFiniteExtremesRetainTheirSmallNonzeroDeviation(double sign)
    {
        var previous = Math.BitDecrement(double.MaxValue);
        var previousPrevious = Math.BitDecrement(previous);
        var snapshot = PriceCommodityChannelIndex.Calculate(
            Source(sign * previousPrevious, sign * previous, sign * double.MaxValue), 3);
        Assert.Equal(sign * 100, Assert.Single(snapshot.Points).Value);
        Assert.Equal(0, snapshot.FlatWindowCount);
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(-1d)]
    public void ExtremeHighLowCancellationRetainsEvenSubnormalCloseVariation(double sign)
    {
        var source = new PriceChartSource("cancellation", "Cancellation",
            Enumerable.Range(1, 3).Select(index => new PriceBar(index, Time(index), 0,
                double.MaxValue, -double.MaxValue, sign * index * double.Epsilon)));
        var snapshot = PriceCommodityChannelIndex.Calculate(source, 3);
        Assert.Equal(sign * 100, Assert.Single(snapshot.Points).Value);
        Assert.Equal(0, snapshot.FlatWindowCount);
    }

    [Fact]
    public void SubnormalTypicalPriceFractionsAreNotRoundedAwayBeforeTheFinalRatio()
    {
        // Exact typical prices are 0, epsilon, epsilon/3. Rounding TP into a double
        // first would change the third value to zero and return -50 instead of -20.
        var source = new PriceChartSource("subnormal", "Subnormal", [
            new(1, Time(0), 0, 0, 0, 0),
            new(2, Time(1), double.Epsilon, double.Epsilon, double.Epsilon, double.Epsilon),
            new(3, Time(2), 0, double.Epsilon, 0, 0)]);
        var snapshot = PriceCommodityChannelIndex.Calculate(source, 3);
        Assert.Equal(-20, Assert.Single(snapshot.Points).Value);
        Assert.Equal(0, snapshot.FlatWindowCount);
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(-1d)]
    public void ExactRatioPreservesARepresentableSubnormalResultAndItsSign(double sign)
    {
        var source = Source(-double.MaxValue, double.MaxValue, sign * Math.ScaleB(1, -57));
        var snapshot = PriceCommodityChannelIndex.Calculate(source, 3);
        var point = Assert.Single(snapshot.Points);
        Assert.Equal(sign * double.Epsilon, point.Value);
        Assert.True(double.IsFinite(point.Value));
        Assert.Equal(0, snapshot.FlatWindowCount);
    }

    [Fact]
    public void NegativeResultBelowRepresentableRangeRetainsNegativeZeroRatherThanBecomingFlat()
    {
        var snapshot = PriceCommodityChannelIndex.Calculate(
            Source(-double.MaxValue, double.MaxValue, -Math.ScaleB(1, -58)), 3);
        var point = Assert.Single(snapshot.Points);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(point.Value));
        Assert.Equal(0, snapshot.FlatWindowCount);
    }

    [Fact]
    public void FractionalPricesAndLargeExactCommonOffsetsUseTheSameFormula()
    {
        var fractional = PriceCommodityChannelIndex.Calculate(Source(.5, 1, 2, .75), 3);
        var offset = PriceCommodityChannelIndex.Calculate(
            Source(1e12 + .5, 1e12 + 1, 1e12 + 2, 1e12 + .75), 3);
        Assert.Equal(fractional.Points.Select(point => point.Value),
            offset.Points.Select(point => point.Value));
        Assert.Equal(new[] { 100d, -200d / 3 }, fractional.Points.Select(point => point.Value));
    }

    [Fact]
    public void MaximumBoundedPeriodIsAcceptedWithoutDownsampling()
    {
        var source = Source(Enumerable.Range(0, PriceChartSource.MaximumBars)
            .Select(value => (double?)value).ToArray());
        var full = PriceCommodityChannelIndex.Calculate(source, PriceChartSource.MaximumBars);
        var point = Assert.Single(full.Points);
        Assert.Equal(PriceChartSource.MaximumBars, full.Period);
        Assert.Equal(PriceChartSource.MaximumBars - 1, point.BarIndex);
        Assert.Equal(source.Bars[^1].Key, point.Key);
        // For the even-length linear sequence 0..n-1: mean absolute deviation = n/4.
        Assert.Equal(400d * (PriceChartSource.MaximumBars - 1) / (3 * PriceChartSource.MaximumBars), point.Value);
        Assert.Equal(0, full.FlatWindowCount);
        var shortest = PriceCommodityChannelIndex.Calculate(source, 1);
        Assert.Empty(shortest.Points);
        Assert.Equal(PriceChartSource.MaximumBars, shortest.FlatWindowCount);
    }

    [Fact]
    public void InvalidSourceAndPeriodsAreRejectedRatherThanSilentlyClamped()
    {
        Assert.Throws<ArgumentNullException>(() => PriceCommodityChannelIndex.Calculate(null!));
        var source = Source(1, 2);
        foreach (var period in new[] { int.MinValue, -1, 0, PriceChartSource.MaximumBars + 1, int.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => PriceCommodityChannelIndex.Calculate(source, period));
        Assert.Equal(1, PriceCommodityChannelIndex.MinimumPeriod);
        Assert.Equal(PriceChartSource.MaximumBars, PriceCommodityChannelIndex.MaximumPeriod);
        Assert.Single(PriceCommodityChannelIndex.Calculate(source, 2).Points);
        Assert.Empty(PriceCommodityChannelIndex.Calculate(source, PriceChartSource.MaximumBars).Points);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(24)]
    public void Narrow_exponent_path_matches_exact_fallback_bit_for_bit_across_gaps_and_signed_prices(int spread)
    {
        var values = Enumerable.Range(0, 100).Select(i => i == 37 ? (double?)null :
            (i % 3 == 0 ? -1 : 1) * Math.ScaleB(1 + i % 8 / 16d, i % 2 == 0 ? 0 : spread)).ToArray();
        var fastSource = Source(values);
        // An isolated subnormal AFTER a gap changes only the chosen arithmetic path;
        // it cannot alter any of the original completed windows.
        var fallbackSource = Source(values.Concat(new double?[] { null, double.Epsilon }).ToArray());
        foreach (var period in new[] { 2, 14, 40 })
        {
            var fast = PriceCommodityChannelIndex.Calculate(fastSource, period);
            var exact = PriceCommodityChannelIndex.Calculate(fallbackSource, period);
            Assert.Equal(exact.Points.Select(p => BitConverter.DoubleToInt64Bits(p.Value)),
                fast.Points.Select(p => BitConverter.DoubleToInt64Bits(p.Value)));
            Assert.Equal(exact.Points.Select(p => p.BarIndex), fast.Points.Select(p => p.BarIndex));
            Assert.Equal(exact.FlatWindowCount, fast.FlatWindowCount);
        }
    }

    private static DateTimeOffset Time(int day) =>
        new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.FromMinutes(330)).AddDays(day);

    private static PriceChartSource Source(params double?[] closes) => new("prices", "Prices",
        closes.Select((close, index) => new PriceBar(long.MinValue + index * 23, Time(index),
            close, close, close, close)), datasetVersion: 17, sourceVersion: 23);

    private static IEnumerable<long?> Bits(PriceBar bar) =>
        new[] { bar.Open, bar.High, bar.Low, bar.Close }
            .Select(value => value.HasValue ? (long?)BitConverter.DoubleToInt64Bits(value.Value) : null);
}
