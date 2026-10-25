using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceBollingerBandsTests
{
    [Fact]
    public void DefaultTwentyPeriodUsesCloseSmaAndPopulationStandardDeviation()
    {
        var source = Source(Enumerable.Range(1, 20).Select(value => (double?)value).ToArray());
        var snapshot = PriceBollingerBands.Calculate(source);
        var point = Assert.Single(snapshot.Points);
        Assert.Same(source, snapshot.Source);
        Assert.Equal(20, snapshot.Period);
        Assert.Equal(2, snapshot.StandardDeviationMultiplier);
        Assert.Equal(0, snapshot.OverflowCount);
        Assert.Equal(19, point.BarIndex);
        Assert.Equal(10.5, point.Middle, 12);
        Assert.Equal(Math.Sqrt(33.25), point.StandardDeviation, 12);
        Assert.Equal(10.5 + 2 * Math.Sqrt(33.25), point.Upper, 12);
        Assert.Equal(10.5 - 2 * Math.Sqrt(33.25), point.Lower, 12);
    }

    [Fact]
    public void CloseIsUsedRatherThanOpenHighLowOrTypicalPrice()
    {
        var source = new PriceChartSource("ohlc", "OHLC", [
            new(10, Time(0), 100, 200, -20, 1), new(20, Time(1), 80, 300, -40, 3)]);
        var point = Assert.Single(PriceBollingerBands.Calculate(source, 2).Points);
        Assert.Equal(2, point.Middle);
        Assert.Equal(1, point.StandardDeviation);
        Assert.Equal(4, point.Upper);
        Assert.Equal(0, point.Lower);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(25.25)]
    [InlineData(-25.25)]
    [InlineData(double.MaxValue)]
    [InlineData(-double.MaxValue)]
    [InlineData(double.Epsilon)]
    public void ConstantWindowsRemainExactEvenAtFiniteExtremes(double value)
    {
        var source = Source(Enumerable.Repeat<double?>(value, 24).ToArray());
        var snapshot = PriceBollingerBands.Calculate(source);
        Assert.Equal(5, snapshot.Points.Length);
        Assert.Equal(0, snapshot.OverflowCount);
        Assert.All(snapshot.Points, point =>
        {
            Assert.Equal(value, point.Middle);
            Assert.Equal(value, point.Upper);
            Assert.Equal(value, point.Lower);
            Assert.Equal(0, point.StandardDeviation);
        });
    }

    [Fact]
    public void PeriodAndMultiplierSelectTrailingWindowsNotCumulativeHistory()
    {
        var source = Source(1, 2, 3, 4, 5);
        var two = PriceBollingerBands.Calculate(source, 2, 1);
        Assert.Equal(new[] { 1, 2, 3, 4 }, two.Points.Select(point => point.BarIndex));
        Assert.Equal(new[] { 1.5, 2.5, 3.5, 4.5 }, two.Points.Select(point => point.Middle));
        Assert.All(two.Points, point => Assert.Equal(.5, point.StandardDeviation));
        var three = PriceBollingerBands.Calculate(source, 3, 0);
        Assert.Equal(3, three.Period);
        Assert.Equal(0, three.StandardDeviationMultiplier);
        Assert.Equal(new[] { 2d, 3d, 4d }, three.Points.Select(point => point.Middle));
        Assert.All(three.Points, point =>
        {
            Assert.Equal(Math.Sqrt(2d / 3), point.StandardDeviation, 12);
            Assert.Equal(point.Middle, point.Upper);
            Assert.Equal(point.Middle, point.Lower);
        });
    }

    [Fact]
    public void OnlyFullConsecutiveWindowsEmitAndExplicitGapsRestartWarmup()
    {
        var source = Source(null, 1, 2, 3, null, 10, 11, null, 20, 21, 22, 23, null);
        var snapshot = PriceBollingerBands.Calculate(source, 3);
        Assert.Equal(new[] { 3, 10, 11 }, snapshot.Points.Select(point => point.BarIndex));
        Assert.Equal(new[] { 2d, 21d, 22d }, snapshot.Points.Select(point => point.Middle));
        Assert.Equal(0, snapshot.OverflowCount);
        Assert.Empty(PriceBollingerBands.Calculate(Source(1, 2), 3).Points);
        Assert.Empty(PriceBollingerBands.Calculate(Source(null, null, null), 2).Points);
        Assert.Empty(PriceBollingerBands.Calculate(Source(), 2).Points);
    }

    [Fact]
    public void MissingCalendarSessionsDoNotInventAnExplicitGap()
    {
        var source = new PriceChartSource("sessions", "Sessions", [
            new(1, Time(0), 1, 1, 1, 1),
            new(2, Time(30), 2, 2, 2, 2),
            new(3, Time(90), 3, 3, 3, 3)]);
        var point = Assert.Single(PriceBollingerBands.Calculate(source, 3).Points);
        Assert.Equal(2, point.BarIndex);
        Assert.Equal(2, point.Middle);
    }

    [Fact]
    public void ViewportQueriesKeepOffscreenHistoryAndCannotChangeTheCalculation()
    {
        var source = Source(Enumerable.Range(1, 40).Select(value => (double?)value).ToArray());
        var before = PriceBollingerBands.Calculate(source);
        var viewport = source.Query(new(barStart: 25, barCount: 10, geometryBudget: 0,
            fixedDomain: new(25, 35)));
        Assert.Equal(25, viewport.BarStart);
        Assert.Empty(viewport.Candles);
        var after = PriceBollingerBands.Calculate(source);
        Assert.Equal(before.Points.ToArray(), after.Points.ToArray());
        Assert.Equal(21, after.Points.Length);
        var firstVisible = Assert.Single(after.Points.Where(point => point.BarIndex == viewport.BarStart));
        // Index 25 closes at 26; its twenty-point window is values 7 through 26.
        Assert.Equal(16.5, firstVisible.Middle, 12);
        Assert.Equal(source.Bars[25].Key, firstVisible.Key);
        var cropped = new PriceChartSource("crop", "Crop", source.Bars.Skip(25).Take(10));
        Assert.Empty(PriceBollingerBands.Calculate(cropped).Points);
    }

    [Fact]
    public void OriginalIdentityVersionsTimestampOffsetAndOhlcBitsRemainUntouched()
    {
        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        var values = new double?[] { negativeZero, 0, 1.25, null, -2.5, double.Epsilon, 9 };
        var source = Source(values);
        var originals = source.Bars.ToArray();
        var beforeBits = originals.SelectMany(Bits).ToArray();
        var snapshot = PriceBollingerBands.Calculate(source, 2, 0);
        Assert.Same(source, snapshot.Source);
        Assert.Equal("prices", snapshot.Source.SeriesId);
        Assert.Equal(17, snapshot.Source.DatasetVersion);
        Assert.Equal(23, snapshot.Source.SourceVersion);
        Assert.Equal(originals, source.Bars.ToArray());
        Assert.Equal(beforeBits, source.Bars.SelectMany(Bits).ToArray());
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(source.Bars[0].Close!.Value));
        Assert.All(snapshot.Points, point =>
        {
            var original = originals[point.BarIndex];
            Assert.Equal(original.Key, point.Key);
            Assert.Equal(original.Timestamp, point.Timestamp);
            Assert.Equal(TimeSpan.FromMinutes(330), point.Timestamp.Offset);
            Assert.Equal(original.Timestamp.Ticks, point.Timestamp.Ticks);
        });
    }

    [Fact]
    public void OppositeFiniteExtremesCanProduceRepresentableBandsWithoutIntermediateOverflow()
    {
        var point = Assert.Single(PriceBollingerBands.Calculate(Source(-double.MaxValue, double.MaxValue), 2, 1).Points);
        Assert.Equal(0, point.Middle);
        Assert.Equal(double.MaxValue, point.StandardDeviation);
        Assert.Equal(double.MaxValue, point.Upper);
        Assert.Equal(-double.MaxValue, point.Lower);
        var oneSided = Assert.Single(PriceBollingerBands.Calculate(Source(0, double.MaxValue), 2, 1).Points);
        Assert.Equal(double.MaxValue / 2, oneSided.Middle);
        Assert.Equal(double.MaxValue / 2, oneSided.StandardDeviation);
        Assert.Equal(double.MaxValue, oneSided.Upper);
        Assert.Equal(0, oneSided.Lower);
    }

    [Fact]
    public void UnrepresentableBandsRejectOnlyThoseWindowsAndCountNeitherGapsNorWarmup()
    {
        var source = Source(-double.MaxValue, double.MaxValue, double.MaxValue, null,
            -double.MaxValue, double.MaxValue);
        var snapshot = PriceBollingerBands.Calculate(source, 2);
        Assert.Equal(2, snapshot.OverflowCount);
        var recovered = Assert.Single(snapshot.Points);
        Assert.Equal(2, recovered.BarIndex);
        Assert.Equal(double.MaxValue, recovered.Middle);
        Assert.Equal(double.MaxValue, recovered.Upper);
        Assert.Equal(double.MaxValue, recovered.Lower);
        Assert.Empty(PriceBollingerBands.Calculate(Source(0, double.MaxValue), 2, 10).Points);
        Assert.Equal(1, PriceBollingerBands.Calculate(Source(0, double.MaxValue), 2, 10).OverflowCount);
    }

    [Fact]
    public void CorrectedTwoPassVarianceRetainsSmallSpreadAtLargeOffsets()
    {
        var point = Assert.Single(PriceBollingerBands.Calculate(
            Source(1e12 + 1, 1e12 + 2, 1e12 + 3, 1e12 + 4), 4).Points);
        Assert.Equal(1e12 + 2.5, point.Middle);
        Assert.Equal(Math.Sqrt(1.25), point.StandardDeviation, 12);
        var previous = Math.BitDecrement(double.MaxValue);
        var extreme = Assert.Single(PriceBollingerBands.Calculate(Source(previous, double.MaxValue), 2, 0).Points);
        Assert.Equal((double.MaxValue - previous) / 2, extreme.StandardDeviation);
        Assert.True(double.IsFinite(extreme.Middle));
        Assert.Equal(extreme.Middle, extreme.Upper);
        Assert.Equal(extreme.Middle, extreme.Lower);
    }

    [Fact]
    public void SmallSubnormalInputsDoNotLoseSpreadToUnscaledSquaring()
    {
        var point = Assert.Single(PriceBollingerBands.Calculate(Source(4 * double.Epsilon, 8 * double.Epsilon), 2).Points);
        Assert.Equal(6 * double.Epsilon, point.Middle);
        Assert.Equal(2 * double.Epsilon, point.StandardDeviation);
        Assert.Equal(10 * double.Epsilon, point.Upper);
        Assert.Equal(2 * double.Epsilon, point.Lower);
    }

    [Fact]
    public void MaximumBoundedPeriodAndMultiplierAreAcceptedWithoutDownsampling()
    {
        var source = Source(Enumerable.Repeat<double?>(3, PriceChartSource.MaximumBars).ToArray());
        var full = PriceBollingerBands.Calculate(source, PriceChartSource.MaximumBars, 10);
        Assert.Equal(PriceChartSource.MaximumBars - 1, Assert.Single(full.Points).BarIndex);
        Assert.Equal(0, full.OverflowCount);
        var shortest = PriceBollingerBands.Calculate(source, 2);
        Assert.Equal(PriceChartSource.MaximumBars - 1, shortest.Points.Length);
        Assert.Equal(source.Bars[^1].Key, shortest.Points[^1].Key);
    }

    [Fact]
    public void InvalidSourcePeriodAndMultiplierAreRejectedRatherThanSilentlyClamped()
    {
        Assert.Throws<ArgumentNullException>(() => PriceBollingerBands.Calculate(null!));
        var source = Source(1, 2);
        foreach (var period in new[] { int.MinValue, -1, 0, 1, PriceChartSource.MaximumBars + 1, int.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => PriceBollingerBands.Calculate(source, period));
        foreach (var multiplier in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity,
                     -1, -double.Epsilon, Math.BitIncrement(10d), double.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => PriceBollingerBands.Calculate(source, 2, multiplier));
        Assert.Single(PriceBollingerBands.Calculate(source, 2, 0).Points);
        Assert.Single(PriceBollingerBands.Calculate(source, 2, 10).Points);
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
