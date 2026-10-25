using System.Globalization;
using System.Numerics;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceCurrentCloseTests
{
    [Fact]
    public void FinalOriginalCloseWinsOverTheVisibleCandleWithoutChangingItsDomain()
    {
        var source = Source(new(11, Epoch, 10, 100, 0, 20, 3),
            new(22, Epoch.AddMinutes(1), 30, 200, -100, 75.12567, 7));
        var prices = source.Query(new(barCount: 1));
        var marker = PriceCurrentClose.Create(prices);

        Assert.Equal(20d, Assert.Single(prices.Candles).Hit.Bar.Close);
        Assert.Equal(new HistoryValueDomain(0, 100), prices.Domain);
        Assert.Equal(PriceCurrentCloseState.Visible, marker.State);
        Assert.Same(source, marker.OriginalSource);
        Assert.Equal(1, marker.OriginalIndex);
        Assert.Equal(source.Bars[1], marker.OriginalHit!.Value.Bar);
        Assert.Equal("75.1257", marker.AxisText);
        Assert.Equal(.2487433, marker.ScreenY!.Value, 14);
        Assert.NotEqual(.248743, marker.ScreenY!.Value);
        Assert.Equal(new HistoryValueDomain(0, 100), prices.Domain);
    }

    [Theory]
    [InlineData(PriceTimeInterval.Minute1)]
    [InlineData(PriceTimeInterval.Minute5)]
    [InlineData(PriceTimeInterval.Minute15)]
    [InlineData(PriceTimeInterval.Minute30)]
    [InlineData(PriceTimeInterval.Hour1)]
    [InlineData(PriceTimeInterval.Hour4)]
    public void EveryIntervalKeepsTheFinalOriginalRowRatherThanVisibleOrAggregateProvenance(PriceTimeInterval interval)
    {
        var last = new PriceBar(44, Epoch.AddHours(4).AddSeconds(1).ToOffset(TimeSpan.FromMinutes(345)),
            64, 90, 50, 75.12567, 17);
        var source = Source(new(11, Epoch, 10, 100, 0, 20, 3),
            new(22, Epoch.AddSeconds(1), 20, 80, 10, 30, 7),
            new(33, Epoch.AddHours(4), 11, 110, 1, 30, 13), last);
        var aggregation = PriceTimeAggregator.Aggregate(source, interval);
        var prices = aggregation.Source.Query(new(barCount: 1));
        var marker = PriceCurrentClose.Create(prices, aggregation);

        Assert.Equal(2, aggregation.Source.Bars.Length);
        Assert.Equal(33L, aggregation.Source.Bars[^1].Key);
        Assert.Equal(30d, aggregation.Source.Bars[^1].Volume);
        Assert.Equal(30d, Assert.Single(prices.Candles).Hit.Bar.Close);
        Assert.Same(source, marker.OriginalSource);
        Assert.Equal(3, marker.OriginalIndex);
        Assert.Equal(new PriceChartHit(17, "original-prices", 23, last), marker.OriginalHit);
        Assert.Equal(last.Timestamp.Ticks, marker.OriginalHit!.Value.Timestamp.Ticks);
        Assert.Equal(TimeSpan.FromMinutes(345), marker.OriginalHit!.Value.Timestamp.Offset);
        Assert.Equal(PriceCurrentCloseState.Visible, marker.State);
        Assert.Equal("75.1257", marker.AxisText);
        Assert.Equal(.2487433, marker.ScreenY!.Value, 14);
    }

    [Fact]
    public void EmptyAndTrailingGapNeverInventZeroOrSearchBackEvenWhenTheFrameIsRefused()
    {
        var empty = Source();
        var emptyAggregation = PriceTimeAggregator.Aggregate(empty, PriceTimeInterval.Hour4);
        foreach (var marker in new[] { PriceCurrentClose.Create(empty.Query()),
            PriceCurrentClose.Create(emptyAggregation.Source.Query(), emptyAggregation) })
        {
            Assert.Same(empty, marker.OriginalSource);
            Assert.Equal(-1, marker.OriginalIndex);
            Assert.Equal(PriceCurrentCloseState.Empty, marker.State);
            Assert.Null(marker.OriginalHit);
            Assert.Null(marker.AxisText);
            Assert.Null(marker.ScreenY);
        }

        var gap = new PriceBar(99, Epoch.AddMinutes(1), null, null, null, null);
        var source = Source(new(11, Epoch, 5, 10, 0, 5), gap);
        var refused = source.Query(new(barCount: 1, geometryBudget: 0));
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, refused.BudgetStatus);
        var aggregation = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Hour4);
        foreach (var marker in new[] { PriceCurrentClose.Create(refused),
            PriceCurrentClose.Create(source.Query(new(barCount: 0))),
            PriceCurrentClose.Create(aggregation.Source.Query(), aggregation),
            PriceCurrentClose.Create(Source(gap).Query()) })
        {
            Assert.Equal(PriceCurrentCloseState.Gap, marker.State);
            Assert.Equal(gap, marker.OriginalHit!.Value.Bar);
            Assert.Equal(marker.OriginalSource.Bars.Length - 1, marker.OriginalIndex);
            Assert.Null(marker.AxisText);
            Assert.Null(marker.ScreenY);
        }
    }

    [Fact]
    public void NoVisibleDomainKeepsTheFinalValueAndIsNotAnEmptyOriginalSource()
    {
        var source = Source(new(11, Epoch, null, null, null, null),
            new(22, Epoch.AddMinutes(1), 12.5, 12.5, 12.5, 12.5));
        foreach (var options in new[] { new PriceChartOptions(barCount: 0, fixedDomain: new(10, 20)),
            new PriceChartOptions(barStart: int.MaxValue), new PriceChartOptions(barCount: 1) })
        {
            var prices = source.Query(options);
            var marker = PriceCurrentClose.Create(prices);
            Assert.Null(prices.Domain);
            Assert.Equal(PriceCurrentCloseState.NoDomain, marker.State);
            Assert.Same(source, marker.OriginalSource);
            Assert.Equal(1, marker.OriginalIndex);
            Assert.Equal(12.5, marker.OriginalHit!.Value.Bar.Close);
            Assert.Equal("12.5000", marker.AxisText);
            Assert.Null(marker.ScreenY);
        }

        // A visible gap does not invent a domain, but an explicit existing domain is usable.
        var fixedPrices = source.Query(new(barCount: 1, fixedDomain: new(10, 20)));
        Assert.Empty(fixedPrices.Candles);
        var fixedMarker = PriceCurrentClose.Create(fixedPrices);
        Assert.Equal(PriceCurrentCloseState.Visible, fixedMarker.State);
        Assert.Equal(.75, fixedMarker.ScreenY);
    }

    [Fact]
    public void FixedDomainsIncludeExactEndpointsButNeverClampOutsideValuesOrExpandConstantRanges()
    {
        var cases = new (double Minimum, double Maximum, double Close, double? Y)[]
        {
            (0, 10, -double.Epsilon, null), (0, 10, 0, 1), (0, 10, 2.5, .75),
            (0, 10, 10, 0), (0, 10, Math.BitIncrement(10), null),
            (-10, -2, -4, .25), (5, 5, Math.BitDecrement(5), null),
            (5, 5, 5, .5), (5, 5, Math.BitIncrement(5), null)
        };
        foreach (var (minimum, maximum, close, y) in cases)
        {
            var domain = new HistoryValueDomain(minimum, maximum);
            var prices = Point(close).Query(new(fixedDomain: domain));
            var marker = PriceCurrentClose.Create(prices);
            Assert.Equal(domain, prices.Domain);
            Assert.Equal(y.HasValue ? PriceCurrentCloseState.Visible : PriceCurrentCloseState.OutsideDomain,
                marker.State);
            Assert.Equal(y, marker.ScreenY);
            Assert.Equal(close, marker.OriginalHit!.Value.Bar.Close);
            Assert.NotNull(marker.AxisText);
        }
    }

    [Fact]
    public void ExtremeAdjacentAndSubnormalDomainsProjectWithoutOverflowOrNearTopCancellation()
    {
        var previous = Math.BitDecrement(double.MaxValue);
        var beforePrevious = Math.BitDecrement(previous);
        var cases = new (double Minimum, double Maximum, double Close, double Y)[]
        {
            (-double.MaxValue, double.MaxValue, -double.MaxValue, 1),
            (-double.MaxValue, double.MaxValue, -double.MaxValue / 2, .75),
            (-double.MaxValue, double.MaxValue, 0, .5),
            (-double.MaxValue, double.MaxValue, double.MaxValue / 2, .25),
            (-double.MaxValue, double.MaxValue, double.MaxValue, 0),
            (beforePrevious, double.MaxValue, previous, .5),
            (-double.MaxValue, -beforePrevious, -previous, .5),
            (0, 8 * double.Epsilon, 5 * double.Epsilon, .375),
            (-1, double.Epsilon, 0, double.Epsilon),
            (double.MaxValue, double.MaxValue, double.MaxValue, .5),
            (-double.MaxValue, -double.MaxValue, -double.MaxValue, .5)
        };
        foreach (var (minimum, maximum, close, y) in cases)
        {
            var marker = PriceCurrentClose.Create(Point(close).Query(new(fixedDomain: new(minimum, maximum))));
            Assert.Equal(PriceCurrentCloseState.Visible, marker.State);
            Assert.Equal(y, marker.ScreenY!.Value);
            Assert.True(double.IsFinite(marker.ScreenY!.Value));
            Assert.InRange(marker.ScreenY!.Value, 0, 1);
        }
    }

    [Fact]
    public void AxisTextIsInvariantF4IncludingSignedZeroAndFullFiniteExtremesWithoutRoundingTheHit()
    {
        // Independent integer identity of binary64 MaxValue, not another double F4 formatter.
        var maximumText = (BigInteger.Pow(2, 1024) - BigInteger.Pow(2, 971))
            .ToString(CultureInfo.InvariantCulture) + ".0000";
        var cases = new (double Close, string Text)[]
        {
            (0, "0.0000"), (NegativeZero, "-0.0000"),
            (1.23454, "1.2345"), (1.23456, "1.2346"), (-1.23456, "-1.2346"),
            (1234567.125, "1234567.1250"), (double.Epsilon, "0.0000"),
            (-double.Epsilon, "-0.0000"), (double.MaxValue, maximumText),
            (-double.MaxValue, "-" + maximumText)
        };
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            foreach (var (close, expected) in cases)
            {
                var marker = PriceCurrentClose.Create(Point(close).Query());
                Assert.Equal(expected, marker.AxisText);
                Assert.Equal(BitConverter.DoubleToInt64Bits(close),
                    BitConverter.DoubleToInt64Bits(marker.OriginalHit!.Value.Bar.Close!.Value));
            }
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
        Assert.Equal(314, maximumText.Length);
    }

    [Fact]
    public void MaximumSourceKeepsTheFinalFullBarIdentityOffsetAndBitsWithoutMutatingOriginals()
    {
        var bars = Enumerable.Range(0, PriceChartSource.MaximumBars)
            .Select(index => new PriceBar(index, Epoch.AddSeconds(index), 1, 2, -2, 1, 5)).ToArray();
        var last = new PriceBar(long.MinValue, bars[^1].Timestamp.ToOffset(TimeSpan.FromHours(-7)),
            NegativeZero, 2, -2, NegativeZero, NegativeZero);
        bars[^1] = last;
        var source = Source(bars);
        var beforeBits = source.Bars.SelectMany(Bits).ToArray();
        var marker = PriceCurrentClose.Create(source.Query(new(barCount: 1)));

        Assert.Same(source, marker.OriginalSource);
        Assert.Equal(PriceChartSource.MaximumBars - 1, marker.OriginalIndex);
        Assert.Equal(new PriceChartHit(17, "original-prices", 23, last), marker.OriginalHit);
        Assert.Equal(last.Timestamp.Ticks, marker.OriginalHit!.Value.Timestamp.Ticks);
        Assert.Equal(TimeSpan.FromHours(-7), marker.OriginalHit!.Value.Timestamp.Offset);
        Assert.Equal(Bits(last).ToArray(), Bits(marker.OriginalHit!.Value.Bar).ToArray());
        Assert.Equal(bars, source.Bars.ToArray());
        Assert.Equal(beforeBits, source.Bars.SelectMany(Bits).ToArray());
        Assert.Equal(PriceCurrentCloseState.Visible, marker.State);
        Assert.Equal(.5, marker.ScreenY);
        Assert.Equal("-0.0000", marker.AxisText);
    }

    [Fact]
    public void RefusedBudgetSuppressesTheMarkerBeforeOutsideDomainButZeroBudgetAloneDoesNot()
    {
        foreach (var close in new[] { 7d, 20d })
        {
            var source = Source(new(11, Epoch, 5, 10, 0, 5),
                new(22, Epoch.AddMinutes(1), close, close, close, close));
            var prices = source.Query(new(barCount: 1, geometryBudget: 0, fixedDomain: new(0, 10)));
            var marker = PriceCurrentClose.Create(prices);
            Assert.Equal(ChartBudgetStatus.InsufficientBudget, prices.BudgetStatus);
            Assert.Equal(PriceCurrentCloseState.BudgetRefused, marker.State);
            Assert.Null(marker.ScreenY);
            Assert.Equal(source.Bars[1], marker.OriginalHit!.Value.Bar);
            Assert.Equal(close == 7 ? "7.0000" : "20.0000", marker.AxisText);
        }

        var clipped = Point(20).Query(new(geometryBudget: 0, fixedDomain: new(0, 10)));
        Assert.Equal(ChartBudgetStatus.Satisfied, clipped.BudgetStatus);
        var outside = PriceCurrentClose.Create(clipped);
        Assert.Equal(PriceCurrentCloseState.OutsideDomain, outside.State);
        Assert.Null(outside.ScreenY);
        Assert.Equal("20.0000", outside.AxisText);
    }

    [Fact]
    public void NullPricesAndAnyNonidenticalAggregationSourceAreRejectedEvenWhenValuesMatchOrAreEmpty()
    {
        Assert.Equal("prices", Assert.Throws<ArgumentNullException>(() => PriceCurrentClose.Create(null!)).ParamName);
        foreach (var source in new[] { Point(5), Source() })
        {
            var aggregation = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute1);
            var equivalent = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute1);
            Assert.Equal(aggregation.Source.SeriesId, equivalent.Source.SeriesId);
            foreach (var prices in new[] { source.Query(), equivalent.Source.Query() })
                Assert.Equal("aggregation", Assert.Throws<ArgumentException>(() =>
                    PriceCurrentClose.Create(prices, aggregation)).ParamName);

            var withProvenance = PriceCurrentClose.Create(aggregation.Source.Query(), aggregation);
            Assert.Same(source, withProvenance.OriginalSource);
            // Without an aggregation argument, the supplied source itself is the original.
            var withoutProvenance = PriceCurrentClose.Create(aggregation.Source.Query());
            Assert.Same(aggregation.Source, withoutProvenance.OriginalSource);
        }
    }

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly double NegativeZero = BitConverter.Int64BitsToDouble(long.MinValue);

    private static PriceChartSource Source(params PriceBar[] bars) =>
        new("original-prices", "Original prices", bars, datasetVersion: 17, sourceVersion: 23);

    private static PriceChartSource Point(double close) =>
        Source(new PriceBar(11, Epoch, close, close, close, close));

    private static IEnumerable<long?> Bits(PriceBar bar) =>
        new[] { bar.Open, bar.High, bar.Low, bar.Close, bar.Volume }
            .Select(value => value.HasValue ? (long?)BitConverter.DoubleToInt64Bits(value.Value) : null);
}
