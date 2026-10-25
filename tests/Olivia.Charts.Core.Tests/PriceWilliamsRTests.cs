using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceWilliamsRTests
{
    [Fact]
    public void DefaultFourteenPeriodWaitsForACompleteWindowAndUsesItsLastClose()
    {
        var closes = Enumerable.Range(1, 13).Select(value => (double?)value).Append(7).ToArray();
        var source = Source(closes);
        var snapshot = PriceWilliamsR.Calculate(source);
        var point = Assert.Single(snapshot.Points);
        Assert.Same(source, snapshot.Source);
        Assert.Equal(14, snapshot.Period);
        Assert.Equal(0, snapshot.FlatWindowCount);
        Assert.Equal(13, point.BarIndex);
        Assert.Equal(-50, point.Value);
        Assert.Empty(PriceWilliamsR.Calculate(Source(closes.Take(13).ToArray())).Points);
    }

    [Fact]
    public void HandCalculatedWindowsUseHighLowAndLastCloseRatherThanCloseOnlyOrOpen()
    {
        var source = new PriceChartSource("ohlc", "OHLC", [
            new(10, Time(0), 7, 10, 4, 8),
            new(20, Time(1), 8, 12, 5, 9),
            new(30, Time(2), 6, 11, 2, 7),
            new(40, Time(3), 9, 15, 6, 12)]);
        var snapshot = PriceWilliamsR.Calculate(source, 3);
        Assert.Equal(new[] { 2, 3 }, snapshot.Points.Select(point => point.BarIndex));
        Assert.Equal(-50, snapshot.Points[0].Value);
        Assert.Equal(-300d / 13, snapshot.Points[1].Value, 12);
        Assert.Equal(0, snapshot.FlatWindowCount);
    }

    [Fact]
    public void OldHighAndLowExpireFromTheTrailingWindowRatherThanAccumulating()
    {
        var source = new PriceChartSource("trailing", "Trailing", [
            new(1, Time(0), 50, 100, 0, 50),
            new(2, Time(1), 15, 20, 10, 15),
            new(3, Time(2), 15, 18, 12, 12)]);
        var snapshot = PriceWilliamsR.Calculate(source, 2);
        Assert.Equal(new[] { 1, 2 }, snapshot.Points.Select(point => point.BarIndex));
        Assert.Equal(-85, snapshot.Points[0].Value, 12);
        Assert.Equal(-80, snapshot.Points[1].Value, 12);
    }

    [Fact]
    public void PeriodOneUsesTheCurrentBarRangeAndPreservesBothDomainEndpoints()
    {
        var source = new PriceChartSource("endpoints", "Endpoints", [
            new(1, Time(0), 5, 10, 0, 10),
            new(2, Time(1), 5, 10, 0, 0),
            new(3, Time(2), 5, 10, 0, 2.5)]);
        var snapshot = PriceWilliamsR.Calculate(source, 1);
        Assert.Equal(1, snapshot.Period);
        Assert.Equal(new[] { 0, 1, 2 }, snapshot.Points.Select(point => point.BarIndex));
        Assert.Equal(new[] { 0d, -100d, -75d }, snapshot.Points.Select(point => point.Value));
        Assert.Equal(0, snapshot.FlatWindowCount);
    }

    [Fact]
    public void OnlyFullConsecutiveWindowsEmitAndExplicitGapsRestartWarmup()
    {
        var source = Source(null, 1, 2, 3, null, 10, 11, null, 20, 21, 22, 23, null);
        var snapshot = PriceWilliamsR.Calculate(source, 3);
        Assert.Equal(new[] { 3, 10, 11 }, snapshot.Points.Select(point => point.BarIndex));
        Assert.All(snapshot.Points, point => Assert.Equal(0, point.Value));
        Assert.Equal(0, snapshot.FlatWindowCount);
        foreach (var empty in new[] { Source(1, 2), Source(null, null, null), Source() })
        {
            var result = PriceWilliamsR.Calculate(empty, 3);
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
        var point = Assert.Single(PriceWilliamsR.Calculate(source, 3).Points);
        Assert.Equal(2, point.BarIndex);
        Assert.Equal(-50, point.Value);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(25.25)]
    [InlineData(-25.25)]
    [InlineData(double.MaxValue)]
    [InlineData(-double.MaxValue)]
    [InlineData(double.Epsilon)]
    public void FlatCompleteWindowsAreUndefinedNotInventedZerosOrMidpoints(double value)
    {
        var source = Source(Enumerable.Repeat<double?>(value, 16).ToArray());
        var snapshot = PriceWilliamsR.Calculate(source);
        Assert.Empty(snapshot.Points);
        Assert.Equal(3, snapshot.FlatWindowCount);
        var one = PriceWilliamsR.Calculate(source, 1);
        Assert.Empty(one.Points);
        Assert.Equal(16, one.FlatWindowCount);
    }

    [Fact]
    public void FlatWindowOmissionLeavesAnOrdinalBreakAndDoesNotRestartHistory()
    {
        var snapshot = PriceWilliamsR.Calculate(Source(0, 1, 1, 1, 2), 3);
        Assert.Equal(new[] { 2, 4 }, snapshot.Points.Select(point => point.BarIndex));
        Assert.All(snapshot.Points, point => Assert.Equal(0, point.Value));
        Assert.Equal(1, snapshot.FlatWindowCount);

        var repeated = PriceWilliamsR.Calculate(Source(0, 1, 1, 1, 2, 2, 2, 1), 2);
        Assert.Equal(new[] { 1, 4, 7 }, repeated.Points.Select(point => point.BarIndex));
        Assert.Equal(new[] { 0d, 0d, -100d }, repeated.Points.Select(point => point.Value));
        Assert.Equal(4, repeated.FlatWindowCount);
    }

    [Fact]
    public void FlatWindowCountExcludesIncompleteHistoryAndGaps()
    {
        var snapshot = PriceWilliamsR.Calculate(Source(1, 1, null, 2, 2, 2, 3, 3, 3), 3);
        Assert.Equal(new[] { 6, 7 }, snapshot.Points.Select(point => point.BarIndex));
        Assert.Equal(2, snapshot.FlatWindowCount);
    }

    [Fact]
    public void ViewportQueriesKeepOffscreenHistoryAndCannotChangeTheCalculation()
    {
        var source = Source(Enumerable.Range(1, 40).Select(value => (double?)value).ToArray());
        var before = PriceWilliamsR.Calculate(source);
        var viewport = source.Query(new(barStart: 25, barCount: 10, geometryBudget: 0,
            fixedDomain: new(25, 35)));
        Assert.Equal(25, viewport.BarStart);
        Assert.Empty(viewport.Candles);
        var after = PriceWilliamsR.Calculate(source);
        Assert.Equal(before.Points.ToArray(), after.Points.ToArray());
        Assert.Same(source, after.Source);
        Assert.Equal(27, after.Points.Length);
        Assert.Equal(13, after.Points[0].BarIndex);
        Assert.Equal(39, after.Points[^1].BarIndex);
        var firstVisible = Assert.Single(after.Points.Where(point => point.BarIndex == viewport.BarStart));
        Assert.Equal(source.Bars[25].Key, firstVisible.Key);
        Assert.Equal(0, firstVisible.Value);
        var cropped = new PriceChartSource("crop", "Crop", source.Bars.Skip(25).Take(10));
        Assert.Empty(PriceWilliamsR.Calculate(cropped).Points);
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
        var snapshot = PriceWilliamsR.Calculate(source, 2);
        Assert.Same(source, snapshot.Source);
        Assert.Equal("prices", snapshot.Source.SeriesId);
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
    [InlineData(-1d, -100d)]
    [InlineData(-.5, -75d)]
    [InlineData(0d, -50d)]
    [InlineData(.5, -25d)]
    [InlineData(1d, 0d)]
    public void OppositeFiniteExtremesDoNotOverflowIntermediateDifferences(double fraction, double expected)
    {
        var source = new PriceChartSource("extremes", "Extremes", [
            new(1, Time(0), 0, double.MaxValue, -double.MaxValue, fraction * double.MaxValue)]);
        var snapshot = PriceWilliamsR.Calculate(source, 1);
        var point = Assert.Single(snapshot.Points);
        Assert.Equal(expected, point.Value, 12);
        Assert.True(double.IsFinite(point.Value));
        Assert.InRange(point.Value, -100, 0);
        Assert.Equal(0, snapshot.FlatWindowCount);
    }

    [Fact]
    public void AdjacentFiniteExtremesRetainTheirSmallNonzeroRanges()
    {
        var previous = Math.BitDecrement(double.MaxValue);
        var previousPrevious = Math.BitDecrement(previous);
        var source = new PriceChartSource("adjacent", "Adjacent", [
            new(1, Time(0), previous, double.MaxValue, previousPrevious, previous),
            new(2, Time(1), -previous, -previousPrevious, -double.MaxValue, -previous)]);
        var snapshot = PriceWilliamsR.Calculate(source, 1);
        Assert.Equal(2, snapshot.Points.Length);
        Assert.All(snapshot.Points, point => Assert.Equal(-50, point.Value));
        Assert.Equal(0, snapshot.FlatWindowCount);
    }

    [Fact]
    public void SubnormalInputRangesRemainDefinedAndProduceFiniteValues()
    {
        var source = new PriceChartSource("subnormal", "Subnormal", [
            new(1, Time(0), 6 * double.Epsilon, 8 * double.Epsilon, 4 * double.Epsilon, 5 * double.Epsilon),
            new(2, Time(1), 0, 8 * double.Epsilon, -8 * double.Epsilon, 0),
            new(3, Time(2), 0, double.Epsilon, 0, 0)]);
        var snapshot = PriceWilliamsR.Calculate(source, 1);
        Assert.Equal(new[] { -75d, -50d, -100d }, snapshot.Points.Select(point => point.Value));
        Assert.Equal(0, snapshot.FlatWindowCount);
    }

    [Fact]
    public void IndependentDifferenceScalesPreserveARepresentableSubnormalPercentage()
    {
        var source = new PriceChartSource("tiny-result", "Tiny result", [
            new(1, Time(0), 0, Math.ScaleB(1, -57), -double.MaxValue, 0)]);
        var point = Assert.Single(PriceWilliamsR.Calculate(source, 1).Points);
        // Scaling all prices by the range's exponent first would erase this numerator.
        Assert.Equal(-double.Epsilon, point.Value);
        Assert.NotEqual(0, point.Value);
    }

    [Fact]
    public void FractionalPricesAndLargeCommonOffsetsUseTheSameFormula()
    {
        var source = new PriceChartSource("fractional", "Fractional", [
            new(1, Time(0), .2, .3, .1, .2),
            new(2, Time(1), 1e12 + 2, 1e12 + 4, 1e12, 1e12 + 1),
            new(3, Time(2), double.MaxValue / 2, double.MaxValue, 0, double.MaxValue / 2)]);
        var snapshot = PriceWilliamsR.Calculate(source, 1);
        Assert.Equal(-50, snapshot.Points[0].Value, 12);
        Assert.Equal(-75, snapshot.Points[1].Value, 12);
        Assert.Equal(-50, snapshot.Points[2].Value, 12);
        Assert.All(snapshot.Points, point => Assert.InRange(point.Value, -100, 0));
    }

    [Fact]
    public void MaximumBoundedPeriodAndPeriodOneAreAcceptedWithoutDownsampling()
    {
        var source = new PriceChartSource("bounded", "Bounded",
            Enumerable.Range(0, PriceChartSource.MaximumBars)
                .Select(index => new PriceBar(index, Time(index), 2, 4, 0, 3)));
        var full = PriceWilliamsR.Calculate(source, PriceChartSource.MaximumBars);
        var point = Assert.Single(full.Points);
        Assert.Equal(PriceChartSource.MaximumBars, full.Period);
        Assert.Equal(PriceChartSource.MaximumBars - 1, point.BarIndex);
        Assert.Equal(-25, point.Value);
        Assert.Equal(0, full.FlatWindowCount);
        var shortest = PriceWilliamsR.Calculate(source, 1);
        Assert.Equal(PriceChartSource.MaximumBars, shortest.Points.Length);
        Assert.Equal(source.Bars[^1].Key, shortest.Points[^1].Key);
        Assert.All(shortest.Points, item => Assert.Equal(-25, item.Value));
    }

    [Fact]
    public void InvalidSourceAndPeriodsAreRejectedRatherThanSilentlyClamped()
    {
        Assert.Throws<ArgumentNullException>(() => PriceWilliamsR.Calculate(null!));
        var source = Source(1, 2);
        foreach (var period in new[] { int.MinValue, -1, 0, PriceChartSource.MaximumBars + 1, int.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => PriceWilliamsR.Calculate(source, period));
        Assert.Equal(1, PriceWilliamsR.MinimumPeriod);
        Assert.Equal(PriceChartSource.MaximumBars, PriceWilliamsR.MaximumPeriod);
        Assert.Single(PriceWilliamsR.Calculate(source, 2).Points);
        Assert.Empty(PriceWilliamsR.Calculate(source, PriceChartSource.MaximumBars).Points);
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
