using System.Globalization;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceCrosshairTests
{
    [Fact]
    public void OrdinalWindowCentersAndTiesIgnoreElapsedTimeAndTheFinalOriginalClose()
    {
        var source = Source(Enumerable.Range(0, 6).Select(i =>
            new PriceBar(100 - i, Epoch.AddHours(i * i), i + 10, i + 20, i, i + 11, i)).ToArray());
        var prices = source.Query(new(barStart: 1, barCount: 4));
        var cases = new (double X, int Index, double Center)[]
        {
            (0, 1, .125), (.249, 1, .125), (.25, 2, .375), (.5, 3, .625), (.75, 4, .875), (1, 4, .875)
        };
        foreach (var (x, index, center) in cases)
        {
            var hover = Assert.IsType<PriceCrosshairSnapshot>(PriceCrosshair.Query(prices, x));
            Assert.Same(prices, hover.Prices);
            Assert.Equal(index, hover.BarIndex);
            Assert.Equal(center, hover.ScreenX);
            Assert.Equal(new PriceChartHit(17, "original-prices", 23, source.Bars[index]), hover.Hit);
            Assert.NotEqual(source.Bars[^1].Close, hover.Hit.Bar.Close);
            Assert.Null(hover.Aggregation);
            Assert.Null(hover.AggregateSegment);
            Assert.False(hover.VolumeEnabled || hover.BollingerEnabled || hover.CciEnabled || hover.WilliamsREnabled);
            Assert.Equal("Close", Assert.Single(hover.Highlights).Series);
            Assert.DoesNotContain("Volume", hover.LabelText);
            Assert.DoesNotContain("Bollinger", hover.LabelText);
            Assert.DoesNotContain("CCI", hover.LabelText);
            Assert.DoesNotContain("Williams", hover.LabelText);
        }
    }

    [Fact]
    public void InvalidCoordinatesEmptyWindowsGapsAndRefusedFramesNeverInventAHover()
    {
        var source = Source(Bar(1, 10), new(2, Epoch.AddSeconds(2), null, null, null, null), Bar(3, 20));
        var prices = source.Query();
        foreach (var x in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, -double.Epsilon, Math.BitIncrement(1d) })
            Assert.Null(PriceCrosshair.Query(prices, x));
        Assert.NotNull(PriceCrosshair.Query(prices, 0));
        Assert.Null(PriceCrosshair.Query(prices, .5));
        Assert.NotNull(PriceCrosshair.Query(prices, 1));
        foreach (var empty in new[] { Source().Query(), source.Query(new(barCount: 0)),
            source.Query(new(barStart: int.MaxValue)), source.Query(new(barStart: 1, barCount: 1)),
            source.Query(new(barStart: 1, barCount: 1, fixedDomain: new(0, 100))) })
            Assert.Null(PriceCrosshair.Query(empty, .5));
        var refused = source.Query(new(geometryBudget: 1));
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, refused.BudgetStatus);
        Assert.Null(PriceCrosshair.Query(refused, 0));
    }

    [Theory]
    [InlineData(PriceTimeInterval.Minute1, "1m")]
    [InlineData(PriceTimeInterval.Minute5, "5m")]
    [InlineData(PriceTimeInterval.Minute15, "15m")]
    [InlineData(PriceTimeInterval.Minute30, "30m")]
    [InlineData(PriceTimeInterval.Hour1, "1h")]
    [InlineData(PriceTimeInterval.Hour4, "4h")]
    public void AllIntervalsRetainAggregateRawValuesFirstOffsetAndCompleteOriginalProvenance(PriceTimeInterval interval, string label)
    {
        var firstTime = Epoch.AddSeconds(1).ToOffset(TimeSpan.FromMinutes(345));
        var lastTime = Epoch.AddSeconds(2).ToOffset(TimeSpan.FromHours(-7));
        var source = Source(new(11, firstTime, 10.00049, 15, 1, 11.99951, 1.25),
            new(22, lastTime, 12, 20, -2, 18.23456, 2.25),
            new(33, Epoch.AddHours(4).AddSeconds(1), 900, 1000, 800, 999, 50));
        var aggregation = PriceTimeAggregator.Aggregate(source, interval);
        var prices = aggregation.Source.Query();
        var hover = Assert.IsType<PriceCrosshairSnapshot>(PriceCrosshair.Query(prices, 0,
            PriceVolumeGeometry.Create(prices), aggregation: aggregation));

        Assert.Same(aggregation, hover.Aggregation);
        Assert.Equal(0, hover.BarIndex);
        Assert.Equal(.25, hover.ScreenX);
        Assert.Equal(aggregation.Segments[0], hover.AggregateSegment);
        Assert.Equal(new PriceBar(11, firstTime, 10.00049, 20, -2, 18.23456, 3.5), hover.Hit.Bar);
        Assert.Equal(firstTime.Ticks, hover.Hit.Timestamp.Ticks);
        Assert.Equal(TimeSpan.FromMinutes(345), hover.Hit.Timestamp.Offset);
        Assert.Equal($"Aggregated {label} · 05:45 (first constituent)", hover.HeaderText);
        Assert.Equal($"Aggregated {label} · 05:45 (first constituent)\nOpen: 10.000\nHigh: 20.000\nLow: -2.000\nClose: 18.235\nVolume: 4", hover.LabelText);
        Assert.Contains($"Derived source: {aggregation.Source.SeriesId}; dataset 17; source 23; bar index 0; key 11;", hover.AccessibleSummary);
        Assert.Contains("Raw OHLC: Open 10.00049; High 20; Low -2; Close 18.23456.", hover.AccessibleSummary);
        Assert.Contains("Raw Volume: 3.5.", hover.AccessibleSummary);
        Assert.Contains("Original source: original-prices; dataset 17; source 23.", hover.AccessibleSummary);
        Assert.Contains($"Aggregation: {label} ({interval}); UTC bucket [2026-01-01T00:00:00.0000000+00:00,", hover.AccessibleSummary);
        Assert.Contains("Constituents: 2; first index 0, key 11, timestamp 2026-01-01T05:45:01.0000000+05:45; last index 1, key 22, timestamp 2025-12-31T17:00:02.0000000-07:00.", hover.AccessibleSummary);
        Assert.Contains("Aggregate volume status: Complete; missing constituents 0.", hover.AccessibleSummary);
        Assert.Equal(18.23456, hover.Hit.Bar.Close);
        Assert.NotEqual(999d, hover.Hit.Bar.Close);
    }

    [Fact]
    public void OfficialCustomFormatsAndTwelveHourHeaderAreInvariantWithoutRoundingRawValues()
    {
        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        var timestamp = new DateTimeOffset(2026, 1, 1, 13, 5, 59, TimeSpan.FromHours(9));
        var source = Source(new PriceBar(11, timestamp, 1.2345, 2.5, negativeZero, 2.3455, 2.5));
        var prices = source.Query();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var hover = Assert.IsType<PriceCrosshairSnapshot>(PriceCrosshair.Query(prices, .5, PriceVolumeGeometry.Create(prices)));
            Assert.Equal("01:05", hover.HeaderText);
            // Custom numeric format rounds midpoint away from zero: volume 2.5 => 3, not F0's 2.
            Assert.Equal("01:05\nOpen: 1.235\nHigh: 2.500\nLow: -0.000\nClose: 2.346\nVolume: 3", hover.LabelText);
            Assert.Contains("timestamp 2026-01-01T13:05:59.0000000+09:00.", hover.AccessibleSummary);
            Assert.Contains("Raw OHLC: Open 1.2345; High 2.5; Low -0; Close 2.3455.", hover.AccessibleSummary);
            Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(hover.Hit.Bar.Low!.Value));
            Assert.Equal(2.3455, hover.Hit.Bar.Close);
            Assert.Equal(2.5, hover.Hit.Bar.Volume);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void CachedActualIndicatorsUseOnlyTheSelectedBarIndexAndNeverBorrowAcrossWarmupOrGaps()
    {
        var source = Source(Bar(0, 10), Bar(1, 12), new(2, Epoch.AddSeconds(2), null, null, null, null),
            Bar(3, 14), Bar(4, 18));
        var prices = source.Query();
        var bb = PriceBollingerBands.Calculate(source, 2);
        var cci = PriceCommodityChannelIndex.Calculate(source, 2);
        var wr = PriceWilliamsR.Calculate(source, 2);
        foreach (var x in new[] { 0d, .6 })
        {
            var missing = Assert.IsType<PriceCrosshairSnapshot>(PriceCrosshair.Query(prices, x, bands: bb, cci: cci, williamsR: wr));
            Assert.True(missing.BollingerEnabled && missing.CciEnabled && missing.WilliamsREnabled);
            Assert.Null(missing.BollingerPoint);
            Assert.Null(missing.CciPoint);
            Assert.Null(missing.WilliamsRPoint);
            Assert.Contains("Bollinger Upper: unavailable\nBollinger Middle: unavailable\nBollinger Lower: unavailable\nCCI: unavailable\nWilliams %R: unavailable", missing.LabelText);
            Assert.Single(missing.Highlights);
        }
        Assert.Null(PriceCrosshair.Query(prices, .4, bands: bb, cci: cci, williamsR: wr));
        var first = Assert.IsType<PriceCrosshairSnapshot>(PriceCrosshair.Query(prices, .2, bands: bb, cci: cci, williamsR: wr));
        Assert.Equal(bb.Points[0], first.BollingerPoint);
        Assert.Equal(cci.Points[0], first.CciPoint);
        Assert.Equal(wr.Points[0], first.WilliamsRPoint);
        Assert.Contains("Bollinger Upper: 13.0000\nBollinger Middle: 11.0000\nBollinger Lower: 9.0000\nCCI: 66.6667\nWilliams %R: -33.3333", first.LabelText);
        Assert.Contains("Indicator F4 formatting: Olivia compatibility.", first.LabelText);
        Assert.Equal(6, first.Highlights.Length);
        var last = Assert.IsType<PriceCrosshairSnapshot>(PriceCrosshair.Query(prices, 1, bands: bb, cci: cci, williamsR: wr));
        Assert.Equal(4, last.BarIndex);
        Assert.Equal(bb.Points[1], last.BollingerPoint);
        Assert.Equal(cci.Points[1], last.CciPoint);
        Assert.Equal(wr.Points[1], last.WilliamsRPoint);
        Assert.Equal(20, last.BollingerPoint!.Value.Upper);
        Assert.Equal(-25, last.WilliamsRPoint!.Value.Value);
    }

    [Fact]
    public void OutsidePriceAndCciValuesStayRawInReadoutWithoutBoundaryHighlights()
    {
        var bars = Enumerable.Range(0, 14).Select(i =>
            new PriceBar(i, Epoch.AddSeconds(i), i == 13 ? 100 : 1, i == 13 ? 100 : 1,
                i == 13 ? 100 : 1, i == 13 ? 100 : 1, 1)).ToArray();
        var source = Source(bars);
        var prices = source.Query(new(fixedDomain: new(0, 2)));
        var bb = PriceBollingerBands.Calculate(source, 14);
        var cci = PriceCommodityChannelIndex.Calculate(source, 14);
        var wr = PriceWilliamsR.Calculate(source, 14);
        var hover = Assert.IsType<PriceCrosshairSnapshot>(PriceCrosshair.Query(prices, 1,
            PriceVolumeGeometry.Create(prices), bb, cci, wr));

        Assert.DoesNotContain(prices.Candles, candle => candle.BarIndex == 13);
        Assert.Equal(100, hover.Hit.Bar.Close);
        Assert.Equal(1400d / 3, hover.CciPoint!.Value.Value);
        Assert.Equal(Assert.Single(cci.Points), hover.CciPoint);
        Assert.Equal(Assert.Single(bb.Points), hover.BollingerPoint);
        Assert.Contains("Close: 100.000", hover.LabelText);
        Assert.Contains("CCI: 466.6667", hover.LabelText);
        Assert.Contains("Raw CCI: 466.6666666666667.", hover.AccessibleSummary);
        foreach (var series in new[] { "Close", "Bollinger Upper", "Bollinger Middle", "Bollinger Lower", "CCI" })
            Assert.Contains($"No marker: {series} (outside Y)", hover.LabelText);
        Assert.Equal(new[] { "Volume", "Williams %R" }, hover.Highlights.Select(point => point.Series));
        Assert.All(hover.Highlights, point => Assert.Equal(0, point.ScreenY));
        Assert.Equal(PriceCrosshairPane.WilliamsR, hover.Highlights[1].Pane);
        Assert.Equal(new HistoryValueDomain(0, 2), prices.Domain);
    }

    [Fact]
    public void ProjectionRetainsInclusiveEndpointsConstantMidpointsAndTinyNearTopCoordinates()
    {
        var prior = Math.BitDecrement(double.MaxValue);
        var cases = new (double Minimum, double Maximum, double Close, double? Y)[]
        {
            (0, 10, 0, 1), (0, 10, 10, 0), (0, 10, 2.5, .75), (5, 5, 5, .5),
            (0, 10, -double.Epsilon, null), (5, 5, Math.BitIncrement(5), null),
            (-double.MaxValue, double.MaxValue, double.MaxValue / 2, .25),
            (Math.BitDecrement(prior), double.MaxValue, prior, .5),
            (0, 8 * double.Epsilon, 5 * double.Epsilon, .375), (-1, double.Epsilon, 0, double.Epsilon)
        };
        foreach (var (minimum, maximum, close, y) in cases)
        {
            var prices = Source(new PriceBar(1, Epoch, close, close, close, close))
                .Query(new(fixedDomain: new(minimum, maximum)));
            var hover = Assert.IsType<PriceCrosshairSnapshot>(PriceCrosshair.Query(prices, .5));
            Assert.Equal(close, hover.Hit.Bar.Close);
            if (y.HasValue)
            {
                var highlight = Assert.Single(hover.Highlights);
                Assert.Equal(PriceCrosshairPane.Price, highlight.Pane);
                Assert.Equal(y.Value, highlight.ScreenY);
                Assert.DoesNotContain("Clipped", hover.LabelText);
            }
            else
            {
                Assert.Empty(hover.Highlights);
                Assert.Contains("No marker: Close (outside Y)", hover.LabelText);
            }
        }
    }

    [Fact]
    public void KnownZeroVolumeHasBottomBaselineWhileMissingAndOverflowStayUnavailable()
    {
        var source = Source(new(1, Epoch, 5, 5, 5, 5, 0), new(2, Epoch.AddSeconds(1), 5, 5, 5, 5));
        var prices = source.Query();
        var volumes = PriceVolumeGeometry.Create(prices);
        var zero = Assert.IsType<PriceCrosshairSnapshot>(PriceCrosshair.Query(prices, 0, volumes));
        Assert.Equal(new HistoryValueDomain(0, 0), volumes.Domain);
        Assert.Equal(.5, zero.Highlights.Single(point => point.Series == "Close").ScreenY);
        Assert.Equal(1, zero.Highlights.Single(point => point.Series == "Volume").ScreenY);
        Assert.Contains("Volume: 0", zero.LabelText);
        var missing = Assert.IsType<PriceCrosshairSnapshot>(PriceCrosshair.Query(prices, 1, volumes));
        Assert.Null(missing.Hit.Bar.Volume);
        Assert.Contains("Volume: unavailable", missing.LabelText);
        Assert.DoesNotContain(missing.Highlights, point => point.Series == "Volume");

        foreach (var second in new double?[] { null, double.Epsilon })
        {
            var aggregate = PriceTimeAggregator.Aggregate(Source(
                new(11, Epoch, 5, 5, 5, 5, double.MaxValue),
                new(22, Epoch.AddSeconds(1), 5, 5, 5, 5, second)), PriceTimeInterval.Minute1);
            var aggregatePrices = aggregate.Source.Query();
            var hover = Assert.IsType<PriceCrosshairSnapshot>(PriceCrosshair.Query(aggregatePrices, .5,
                PriceVolumeGeometry.Create(aggregatePrices), aggregation: aggregate));
            var status = second.HasValue ? PriceAggregateVolumeStatus.Overflow : PriceAggregateVolumeStatus.Missing;
            Assert.Equal(status, hover.AggregateSegment!.Value.VolumeStatus);
            Assert.Null(hover.Hit.Bar.Volume);
            Assert.Contains($"Volume: unavailable ({status})", hover.LabelText);
            Assert.Contains($"Aggregate volume status: {status};", hover.AccessibleSummary);
            Assert.DoesNotContain(hover.Highlights, point => point.Series == "Volume");
        }
    }

    [Fact]
    public void ReferenceMismatchesThrowBeforeEmptyFrameOrInvalidPointerLookup()
    {
        Assert.Equal("prices", Assert.Throws<ArgumentNullException>(() => PriceCrosshair.Query(null!, 0)).ParamName);
        foreach (var bars in new[] { Array.Empty<PriceBar>(), new[] { Bar(0, 10), Bar(1, 12) } })
        {
            var source = Source(bars);
            var duplicate = Source(bars);
            var prices = source.Query();
            var otherPrices = source.Query();
            Assert.Equal("volumes", Assert.Throws<ArgumentException>(() => PriceCrosshair.Query(prices, double.NaN,
                PriceVolumeGeometry.Create(otherPrices))).ParamName);
            Assert.Equal("volumes", Assert.Throws<ArgumentException>(() => PriceCrosshair.Query(prices, double.NaN,
                PriceVolumeGeometry.Create(duplicate.Query()))).ParamName);
            Assert.Equal("bands", Assert.Throws<ArgumentException>(() => PriceCrosshair.Query(prices, double.NaN,
                bands: PriceBollingerBands.Calculate(duplicate, 2))).ParamName);
            Assert.Equal("cci", Assert.Throws<ArgumentException>(() => PriceCrosshair.Query(prices, double.NaN,
                cci: PriceCommodityChannelIndex.Calculate(duplicate, 2))).ParamName);
            Assert.Equal("williamsR", Assert.Throws<ArgumentException>(() => PriceCrosshair.Query(prices, double.NaN,
                williamsR: PriceWilliamsR.Calculate(duplicate, 2))).ParamName);
            var aggregate = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute1);
            var duplicateAggregate = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute1);
            Assert.Equal(aggregate.Source.SeriesId, duplicateAggregate.Source.SeriesId);
            Assert.Equal("aggregation", Assert.Throws<ArgumentException>(() => PriceCrosshair.Query(
                duplicateAggregate.Source.Query(), double.NaN, aggregation: aggregate)).ParamName);
            Assert.Equal("aggregation", Assert.Throws<ArgumentException>(() => PriceCrosshair.Query(prices, double.NaN,
                aggregation: aggregate)).ParamName);
        }
    }

    [Fact]
    public void RepeatedQueriesKeepMaximumHistoryCachedIndicatorsFrameAndSignedBitsUnchanged()
    {
        var bars = Enumerable.Range(0, PriceChartSource.MaximumBars).Select(i =>
            i == 512 ? new PriceBar(i, Epoch.AddSeconds(i), null, null, null, null) :
            new PriceBar(i, Epoch.AddSeconds(i).ToOffset(TimeSpan.FromMinutes(345)),
                BitConverter.Int64BitsToDouble(long.MinValue), 2, -2, i % 2 == 0 ? -1 : 1, i)).ToArray();
        var source = Source(bars);
        var prices = source.Query(new(barStart: 500, barCount: 100));
        var volumes = PriceVolumeGeometry.Create(prices);
        var bb = PriceBollingerBands.Calculate(source, 2);
        var cci = PriceCommodityChannelIndex.Calculate(source, 2);
        var wr = PriceWilliamsR.Calculate(source, 2);
        var before = source.Bars.SelectMany(Bits).ToArray();
        var candles = prices.Candles;
        var columns = volumes.Columns;
        var bands = bb.Points;
        var cciPoints = cci.Points;
        var wrPoints = wr.Points;
        foreach (var x in new[] { 0d, .1, .12, .5, 1d, .12, 0d })
        {
            var hover = PriceCrosshair.Query(prices, x, volumes, bb, cci, wr);
            if (x == .12) { Assert.Null(hover); continue; }
            Assert.NotNull(hover);
            var expectedIndex = x == 1 ? 599 : 500 + (int)(x * 100);
            Assert.Equal(expectedIndex, hover.BarIndex);
            Assert.Equal(source.Bars[expectedIndex], hover.Hit.Bar);
            Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(hover.Hit.Bar.Open!.Value));
            Assert.Equal(7, hover.Highlights.Length);
            Assert.All(hover.Highlights, point => Assert.InRange(point.ScreenY, 0, 1));
        }
        Assert.Equal(before, source.Bars.SelectMany(Bits).ToArray());
        Assert.Equal(bars, source.Bars.ToArray());
        Assert.Equal(candles, prices.Candles);
        Assert.Equal(columns, volumes.Columns);
        Assert.Equal(bands, bb.Points);
        Assert.Equal(cciPoints, cci.Points);
        Assert.Equal(wrPoints, wr.Points);
        Assert.Equal(new HistoryValueDomain(-2, 2), prices.Domain);
    }

    [Fact]
    public void AggregateAtMaximumDateKeepsExactExclusiveUtcTickProvenance()
    {
        var original = Source(new PriceBar(11, DateTimeOffset.MaxValue, 5, 5, 5, 5, 1));
        var aggregate = PriceTimeAggregator.Aggregate(original, PriceTimeInterval.Hour4);
        var hover = Assert.IsType<PriceCrosshairSnapshot>(PriceCrosshair.Query(aggregate.Source.Query(), 1,
            aggregation: aggregate));
        Assert.Null(hover.AggregateSegment!.Value.BucketEndUtc);
        Assert.Contains("UTC tick 3155378976000000000 (beyond DateTimeOffset.MaxValue)", hover.AccessibleSummary);
        Assert.Contains("end ticks exclusive 3155378976000000000.", hover.AccessibleSummary);
        Assert.Equal(DateTimeOffset.MaxValue.Ticks, hover.Hit.Timestamp.Ticks);
    }

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static PriceChartSource Source(params PriceBar[] bars) => new("original-prices", "Original prices", bars, 17, 23);
    private static PriceBar Bar(int index, double close) => new(index, Epoch.AddSeconds(index), close, close + 2, close - 2, close, index);
    private static IEnumerable<long?> Bits(PriceBar bar) => new[] { bar.Open, bar.High, bar.Low, bar.Close, bar.Volume }
        .Select(value => value.HasValue ? (long?)BitConverter.DoubleToInt64Bits(value.Value) : null);
}
