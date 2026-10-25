using System.Collections.Immutable;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceVolumeTests
{
    [Theory]
    [InlineData(0, 2500, 0)]
    [InlineData(500, 2500, 1d / 6)]
    [InlineData(double.MaxValue, 30000, 1)]
    public void Financial_headroom_preserves_zero_baseline_raw_values_and_hit_geometry(double value, double margin, double height)
    {
        var prices = Source(value, 0, null).Query();
        var snapshot = PriceVolumeGeometry.Create(prices, margin);
        Assert.Equal(new HistoryValueDomain(0, Math.Min(double.MaxValue, value + margin)), snapshot.Domain);
        Assert.Equal(height, snapshot.Columns[0].Body.Height, 12);
        Assert.Equal(value, snapshot.Columns[0].Hit.Bar.Volume);
        Assert.Equal(0, snapshot.Columns[1].Body.Height);
        Assert.Null(PriceVolumeGeometry.HitTest(snapshot, snapshot.Columns[1].Body.Left, 1));
        if (height > 0)
        {
            var body = snapshot.Columns[0].Body;
            Assert.Equal(snapshot.Columns[0].Hit, PriceVolumeGeometry.HitTest(snapshot,
                body.Left + body.Width / 2, body.Top + body.Height / 2));
            if (body.Top > 0) Assert.Null(PriceVolumeGeometry.HitTest(snapshot, body.Left + body.Width / 2, body.Top / 2));
        }
        Assert.Null(PriceVolumeGeometry.Create(Source(null, null).Query(), margin).Domain);
        Assert.Same(prices, snapshot.Prices);
    }

    [Fact]
    public void Financial_headroom_rejects_invalid_requests_without_mutating_prices()
    {
        var prices = Source(12).Query();
        foreach (var margin in new[] { -1, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => PriceVolumeGeometry.Create(prices, margin));
        Assert.Equal(12, prices.Source.Bars[0].Volume);
    }

    [Fact]
    public void ExactSixArgumentConstructorRemainsAndDelegatesToMissingVolume()
    {
        var signature = new[] { typeof(long), typeof(DateTimeOffset), typeof(double?),
            typeof(double?), typeof(double?), typeof(double?) };
        Assert.NotNull(typeof(PriceBar).GetConstructor(signature));
        Assert.NotNull(typeof(PriceBar).GetConstructor(signature.Append(typeof(double?)).ToArray()));
        Assert.Null(typeof(PriceBar).GetProperty(nameof(PriceBar.Volume))!.SetMethod);
        var legacy = new PriceBar(7, Time(0), 10, 12, 8, 11);
        var explicitMissing = new PriceBar(7, Time(0), 10, 12, 8, 11, null);
        Assert.Equal(legacy, explicitMissing);
        Assert.Null(legacy.Volume);
        Assert.False(legacy.IsGap);
        var snapshot = PriceVolumeGeometry.Create(new PriceChartSource("legacy", "Legacy", [legacy]).Query());
        Assert.Null(snapshot.Domain);
        Assert.Empty(snapshot.Columns);
        Assert.Equal(0, snapshot.RequiredColumnCount);
        Assert.Equal(1, snapshot.MissingVolumeCount);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(double.Epsilon)]
    [InlineData(.125)]
    [InlineData(123456789.5)]
    [InlineData(double.MaxValue)]
    public void SevenArgumentConstructorRetainsEveryFiniteNonnegativeVolume(double volume)
    {
        var bar = Bar(0, volume);
        Assert.Equal(BitConverter.DoubleToInt64Bits(volume), BitConverter.DoubleToInt64Bits(bar.Volume!.Value));
        Assert.Equal(10d, bar.Open);
        Assert.Equal(12d, bar.High);
        Assert.Equal(8d, bar.Low);
        Assert.Equal(11d, bar.Close);
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(-double.Epsilon)]
    [InlineData(-double.MaxValue)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NegativeAndNonfiniteVolumeAreRejectedRatherThanClamped(double volume)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Bar(0, volume));
        Assert.Equal("volume", error.ParamName);
    }

    [Fact]
    public void ExplicitOhlcGapsRequireNullVolumeAndStillOccupyAnOrdinalSlot()
    {
        var legacyGap = new PriceBar(1, Time(0), null, null, null, null);
        var explicitGap = new PriceBar(1, Time(0), null, null, null, null, null);
        Assert.Equal(legacyGap, explicitGap);
        Assert.True(explicitGap.IsGap);
        Assert.Null(default(PriceBar).Volume);
        foreach (var volume in new[] { 0d, NegativeZero, 1, double.MaxValue })
            Assert.Throws<ArgumentException>(() => new PriceBar(1, Time(0), null, null, null, null, volume));

        var source = new PriceChartSource("gaps", "Gaps", [Bar(0, 10), Gap(1), Bar(2, 20)]);
        var snapshot = PriceVolumeGeometry.Create(source.Query());
        Assert.Equal(new[] { 0, 2 }, snapshot.Columns.Select(column => column.BarIndex));
        Assert.Equal(2, snapshot.RequiredColumnCount);
        Assert.Equal(1, snapshot.MissingVolumeCount);
        Assert.Equal(3, snapshot.RequiredColumnCount + snapshot.MissingVolumeCount);
        Assert.Null(PriceVolumeGeometry.HitTest(snapshot, .5, .5));
    }

    [Fact]
    public void VolumeDoesNotRelaxExistingOhlcValidation()
    {
        Assert.Throws<ArgumentException>(() => new PriceBar(1, Time(0), 1, null, 1, 1, 5));
        Assert.Throws<ArgumentException>(() => new PriceBar(1, Time(0), 1, double.PositiveInfinity, 0, 1, 5));
        Assert.Throws<ArgumentException>(() => new PriceBar(1, Time(0), 3, 2, 0, 1, 5));
        Assert.Throws<ArgumentException>(() => new PriceBar(1, Time(0), 1, 2, 3, 1, 5));
    }

    [Fact]
    public void MissingAndKnownZeroVolumesRemainDifferentWithoutImputation()
    {
        var source = Source(null, 0, NegativeZero, 25);
        var prices = source.Query();
        var snapshot = PriceVolumeGeometry.Create(prices);
        Assert.Same(prices, snapshot.Prices);
        Assert.Same(source, snapshot.Source);
        Assert.Equal(new HistoryValueDomain(0, 25), snapshot.Domain);
        Assert.Equal(3, snapshot.RequiredColumnCount);
        Assert.Equal(1, snapshot.MissingVolumeCount);
        Assert.Equal(new[] { 1, 2, 3 }, snapshot.Columns.Select(column => column.BarIndex));
        Assert.Null(source.Bars[0].Volume);
        Assert.Equal(0d, snapshot.Columns[0].Body.Height);
        Assert.Equal(0d, snapshot.Columns[1].Body.Height);
        Assert.Equal(1d, snapshot.Columns[0].Body.Top);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(snapshot.Columns[1].Hit.Bar.Volume!.Value));
        Assert.Equal(1d, snapshot.Columns[2].Body.Height);
        Assert.Equal(0d, snapshot.Columns[2].Body.Top);
    }

    [Fact]
    public void EmptyAllMissingAndGapOnlyWindowsHaveNoVolumeDomain()
    {
        foreach (var source in new[] { Source(), Source(null, null),
            new PriceChartSource("gaps", "Gaps", [Gap(0), Gap(1)]) })
        {
            var snapshot = PriceVolumeGeometry.Create(source.Query());
            Assert.Null(snapshot.Domain);
            Assert.Empty(snapshot.Columns);
            Assert.Equal(0, snapshot.RequiredColumnCount);
            Assert.Equal(source.Bars.Length, snapshot.MissingVolumeCount);
        }
    }

    [Fact]
    public void AllZeroDomainIsExactlyZeroAndZeroColumnsNeverHit()
    {
        var snapshot = PriceVolumeGeometry.Create(Source(0, NegativeZero, null).Query());
        Assert.Equal(new HistoryValueDomain(0, 0), snapshot.Domain);
        Assert.Equal(2, snapshot.Columns.Length);
        Assert.Equal(2, snapshot.RequiredColumnCount);
        Assert.Equal(1, snapshot.MissingVolumeCount);
        foreach (var column in snapshot.Columns)
        {
            Assert.Equal(0d, column.Body.Height);
            Assert.Equal(1d, column.Body.Top);
            Assert.Null(PriceVolumeGeometry.HitTest(snapshot, column.Body.Left + column.Body.Width / 2, 1));
        }
    }

    [Fact]
    public void SubnormalVolumesKeepTheirRatiosWithoutArtificialMinimumHeight()
    {
        var snapshot = PriceVolumeGeometry.Create(Source(double.Epsilon, 2 * double.Epsilon,
            4 * double.Epsilon, 0).Query());
        Assert.Equal(new HistoryValueDomain(0, 4 * double.Epsilon), snapshot.Domain);
        Assert.Equal(new[] { .25, .5, 1, 0 }, snapshot.Columns.Select(column => column.Body.Height));
        Assert.Equal(new[] { .75, .5, 0, 1 }, snapshot.Columns.Select(column => column.Body.Top));
        Assert.All(snapshot.Columns, column => Assert.True(double.IsFinite(column.Body.Height)));
    }

    [Fact]
    public void MaxValueVolumesDoNotOverflowAndAdjacentMaximumRetainsNearTopPrecision()
    {
        var previous = Math.BitDecrement(double.MaxValue);
        var snapshot = PriceVolumeGeometry.Create(Source(double.MaxValue, previous, double.MaxValue / 2).Query());
        Assert.Equal(new HistoryValueDomain(0, double.MaxValue), snapshot.Domain);
        Assert.Equal(1d, snapshot.Columns[0].Body.Height);
        Assert.Equal(0d, snapshot.Columns[0].Body.Top);
        Assert.Equal(previous / double.MaxValue, snapshot.Columns[1].Body.Height);
        Assert.Equal((double.MaxValue - previous) / double.MaxValue, snapshot.Columns[1].Body.Top);
        Assert.True(snapshot.Columns[1].Body.Top > 0);
        Assert.Equal(.5, snapshot.Columns[2].Body.Height);
        Assert.Equal(.5, snapshot.Columns[2].Body.Top);
        Assert.All(snapshot.Columns, column =>
        {
            Assert.True(double.IsFinite(column.Body.Height));
            Assert.True(double.IsFinite(column.Body.Top));
        });
    }

    [Fact]
    public void TinyToHugeRatiosMayUnderflowGeometryButNeverEraseRawVolumeOrBecomeHittableZeros()
    {
        var snapshot = PriceVolumeGeometry.Create(Source(double.Epsilon, double.MaxValue).Query());
        var tiny = snapshot.Columns[0];
        Assert.Equal(0d, tiny.Body.Height);
        Assert.Equal(1d, tiny.Body.Top);
        Assert.Equal(1L, BitConverter.DoubleToInt64Bits(tiny.Hit.Bar.Volume!.Value));
        Assert.Null(PriceVolumeGeometry.HitTest(snapshot, tiny.Body.Left + tiny.Body.Width / 2, 1));
        Assert.Equal(2, snapshot.RequiredColumnCount);
        Assert.Equal(0, snapshot.MissingVolumeCount);
    }

    [Fact]
    public void RepresentableTinyNormalizedHeightIsRetainedRatherThanReconstructedFromTop()
    {
        var snapshot = PriceVolumeGeometry.Create(Source(double.Epsilon, 1).Query());
        Assert.Equal(double.Epsilon, snapshot.Columns[0].Body.Height);
        Assert.Equal(1d, snapshot.Columns[0].Body.Top);
        Assert.Equal(double.Epsilon, snapshot.Columns[0].Hit.Bar.Volume);
    }

    [Fact]
    public void VisibleOrdinalWindowAloneSetsDomainCountsAndSharedXWhileZoomChangesWidth()
    {
        var source = Source(double.MaxValue, null, 10, 20, 100);
        var prices = source.Query(new(barStart: 2, barCount: 2, bodyWidthFraction: .9));
        var snapshot = PriceVolumeGeometry.Create(prices);
        Assert.Equal(new HistoryValueDomain(0, 20), snapshot.Domain);
        Assert.Equal(2, snapshot.RequiredColumnCount);
        Assert.Equal(0, snapshot.MissingVolumeCount);
        Assert.Equal(new[] { 2, 3 }, snapshot.Columns.Select(column => column.BarIndex));
        Assert.Equal(new[] { .5, 1 }, snapshot.Columns.Select(column => column.Body.Height));
        for (var i = 0; i < snapshot.Columns.Length; i++)
        {
            var body = snapshot.Columns[i].Body;
            Assert.Equal(.1, body.Width);
            Assert.Equal(prices.Candles[i].CenterX, body.Left + body.Width / 2, 14);
            Assert.Equal((i + .4) / 2, body.Left);
        }
        var zoom = PriceVolumeGeometry.Create(source.Query(new(barStart: 3, barCount: 1)));
        var zoomed = Assert.Single(zoom.Columns);
        Assert.Equal(3, zoomed.BarIndex);
        Assert.Equal(new HistoryValueDomain(0, 20), zoom.Domain);
        Assert.Equal(.4, zoomed.Body.Left);
        Assert.Equal(.2, zoomed.Body.Width);
        Assert.Equal(1d, zoomed.Body.Height);
        Assert.Equal(5, source.Bars.Length);
    }

    [Fact]
    public void NullsAndGapsOutsideViewportDoNotAffectVisibleMissingCount()
    {
        var source = new PriceChartSource("window", "Window", [Gap(0), Bar(1, null),
            Bar(2, 5), Bar(3, null), Gap(4), Bar(5, 10), Gap(6)]);
        var snapshot = PriceVolumeGeometry.Create(source.Query(new(barStart: 2, barCount: 4)));
        Assert.Equal(new[] { 2, 5 }, snapshot.Columns.Select(column => column.BarIndex));
        Assert.Equal(2, snapshot.MissingVolumeCount);
        Assert.Equal(2, snapshot.RequiredColumnCount);
        Assert.Equal(new HistoryValueDomain(0, 10), snapshot.Domain);
    }

    [Fact]
    public void EmptyAndOutOfRangeViewportsHaveNoColumnsCountsOrDomain()
    {
        var source = Source(10, 20);
        foreach (var options in new[] { new PriceChartOptions(barCount: 0),
            new PriceChartOptions(barStart: 2), new PriceChartOptions(barStart: int.MaxValue) })
        {
            var snapshot = PriceVolumeGeometry.Create(source.Query(options));
            Assert.Null(snapshot.Domain);
            Assert.Empty(snapshot.Columns);
            Assert.Equal(0, snapshot.RequiredColumnCount);
            Assert.Equal(0, snapshot.MissingVolumeCount);
        }
    }

    [Fact]
    public void MissingCalendarSessionsNeverInventSlotsOrChangeVolumeWidth()
    {
        var source = new PriceChartSource("sessions", "Sessions", [Bar(0, 10), Bar(30, 20), Bar(90, 30)]);
        var snapshot = PriceVolumeGeometry.Create(source.Query());
        Assert.Equal(3, snapshot.Columns.Length);
        for (var i = 0; i < snapshot.Columns.Length; i++)
        {
            Assert.Equal(i, snapshot.Columns[i].BarIndex);
            Assert.Equal(.2 / 3, snapshot.Columns[i].Body.Width);
            Assert.Equal((i + .4) / 3, snapshot.Columns[i].Body.Left);
        }
    }

    [Fact]
    public void FixedPriceYClippingCannotRemoveVolumeOrAlterItsIndependentDomain()
    {
        var source = Source(5, null, 0, 10);
        var automatic = PriceVolumeGeometry.Create(source.Query());
        var offScalePrices = source.Query(new(geometryBudget: 0, fixedDomain: new(100, 200)));
        Assert.Empty(offScalePrices.Candles);
        Assert.Equal(0, offScalePrices.RequiredCandleCount);
        Assert.Equal(ChartBudgetStatus.Satisfied, offScalePrices.BudgetStatus);
        var offScale = PriceVolumeGeometry.Create(offScalePrices);
        Assert.Equal(automatic.Domain, offScale.Domain);
        Assert.Equal(automatic.Columns.ToArray(), offScale.Columns.ToArray());
        Assert.Equal(3, offScale.RequiredColumnCount);
        Assert.Equal(1, offScale.MissingVolumeCount);
        // The existing budget is candle-only. It is not reinterpreted as a volume primitive budget.
        Assert.True(offScale.Columns.Length > offScale.Prices.Options.GeometryBudget);
    }

    [Fact]
    public void PriceBudgetRefusalSuppressesAllVolumeGeometryButRetainsRawCountsAndDomain()
    {
        var source = Source(5, null, 0, 10);
        var prices = source.Query(new(geometryBudget: 3));
        var refused = PriceVolumeGeometry.Create(prices);
        Assert.Equal(4, prices.RequiredCandleCount);
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, prices.BudgetStatus);
        Assert.Empty(prices.Candles);
        Assert.Empty(refused.Columns);
        Assert.Equal(3, refused.RequiredColumnCount);
        Assert.Equal(1, refused.MissingVolumeCount);
        Assert.Equal(new HistoryValueDomain(0, 10), refused.Domain);
        Assert.Null(PriceVolumeGeometry.HitTest(refused, .875, .5));
        var admitted = PriceVolumeGeometry.Create(source.Query(new(geometryBudget: 4)));
        Assert.Equal(3, admitted.Columns.Length);
        Assert.Equal(ChartBudgetStatus.Satisfied, admitted.Prices.BudgetStatus);
    }

    [Fact]
    public void MaximumSourceRemainsBoundedWithoutPartialOutputOrCombinedBudgetReinterpretation()
    {
        var source = new PriceChartSource("maximum", "Maximum",
            Enumerable.Range(0, PriceChartSource.MaximumBars).Select(index => Bar(index, index + 1)));
        var full = PriceVolumeGeometry.Create(source.Query());
        Assert.Equal(PriceChartSource.MaximumBars, full.Columns.Length);
        Assert.Equal(PriceChartSource.MaximumBars, full.RequiredColumnCount);
        Assert.Equal(0, full.MissingVolumeCount);
        Assert.Equal(PriceChartSource.MaximumBars, full.Prices.Candles.Length);
        Assert.Equal(new HistoryValueDomain(0, PriceChartSource.MaximumBars), full.Domain);
        Assert.Equal(.2 / PriceChartSource.MaximumBars, full.Columns[0].Body.Width);
        var refused = PriceVolumeGeometry.Create(source.Query(new(geometryBudget: PriceChartSource.MaximumBars - 1)));
        Assert.Empty(refused.Columns);
        Assert.Equal(PriceChartSource.MaximumBars, refused.RequiredColumnCount);
        Assert.Equal(full.Domain, refused.Domain);
        Assert.Throws<ArgumentException>(() => new PriceChartSource("too-many", "Too many",
            Enumerable.Range(0, PriceChartSource.MaximumBars + 1).Select(index => Bar(index, index + 1))));
    }

    [Fact]
    public void SourceVersionsIdentityTimestampOffsetsAndAllRawBitsRemainUntouched()
    {
        var originals = new[] {
            new PriceBar(long.MinValue, Time(0).ToOffset(TimeSpan.FromHours(-4)), NegativeZero, 0, NegativeZero, 0, NegativeZero),
            new PriceBar(long.MaxValue, Time(1).ToOffset(TimeSpan.FromMinutes(345)), 10, 12, 8, 11, double.Epsilon),
            new PriceBar(-7, Time(2), 10, 12, 8, 11, double.MaxValue),
            new PriceBar(0, Time(3), 10, 12, 8, 11, null), Gap(4) };
        var beforeBits = originals.SelectMany(Bits).ToArray();
        var source = new PriceChartSource("raw-volume", "Raw volume", originals, datasetVersion: 17, sourceVersion: 23);
        var newer = new PriceChartSource("raw-volume", "Raw volume", originals, datasetVersion: 18, sourceVersion: 24);
        foreach (var current in new[] { source, newer })
        {
            var snapshot = PriceVolumeGeometry.Create(current.Query());
            Assert.Same(current, snapshot.Source);
            Assert.Equal(originals, current.Bars.ToArray());
            Assert.Equal(beforeBits, current.Bars.SelectMany(Bits).ToArray());
            Assert.All(snapshot.Columns, column =>
            {
                var raw = originals[column.BarIndex];
                Assert.Equal(current.DatasetVersion, column.Hit.DatasetVersion);
                Assert.Equal(current.SourceVersion, column.Hit.SourceVersion);
                Assert.Equal("raw-volume", column.Hit.SeriesId);
                Assert.Equal(raw.Key, column.Hit.Key);
                Assert.Equal(raw.Timestamp.Ticks, column.Hit.Timestamp.Ticks);
                Assert.Equal(raw.Timestamp.Offset, column.Hit.Timestamp.Offset);
                Assert.Equal(Bits(raw).ToArray(), Bits(column.Hit.Bar).ToArray());
            });
        }
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(source.Bars[0].Volume!.Value));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(source.Bars[0].Open!.Value));
    }

    [Fact]
    public void AddingVolumeLeavesExistingPriceAndIndicatorNumericsUnchanged()
    {
        var originalBars = Enumerable.Range(0, 64).Select(index =>
        {
            var open = 100 + index * .25;
            return new PriceBar(index, Time(index), open, open + 3, open - 2,
                open + (index % 3 - 1) * .5);
        }).ToArray();
        var withVolume = originalBars.Select((bar, index) => new PriceBar(bar.Key, bar.Timestamp,
            bar.Open, bar.High, bar.Low, bar.Close,
            (index % 4) switch { 0 => NegativeZero, 1 => double.Epsilon, 2 => double.MaxValue, _ => index * 123.25 }));
        var original = new PriceChartSource("compat", "Compatibility", originalBars, 17, 23);
        var extended = new PriceChartSource("compat", "Compatibility", withVolume, 17, 23);
        foreach (var options in new[] { new PriceChartOptions(), new PriceChartOptions(barStart: 11, barCount: 20),
            new PriceChartOptions(fixedDomain: new(104, 108)), new PriceChartOptions(geometryBudget: 0) })
        {
            var before = original.Query(options);
            var after = extended.Query(options);
            Assert.Equal(before.Domain, after.Domain);
            Assert.Equal(before.BudgetStatus, after.BudgetStatus);
            Assert.Equal(before.RequiredCandleCount, after.RequiredCandleCount);
            Assert.Equal(before.Ticks.ToArray(), after.Ticks.ToArray());
            Assert.Equal(before.Candles.Select(candle => (candle.BarIndex, candle.CenterX, candle.Body, candle.Wick)).ToArray(),
                after.Candles.Select(candle => (candle.BarIndex, candle.CenterX, candle.Body, candle.Wick)).ToArray());
        }
        var originalBands = PriceBollingerBands.Calculate(original);
        var extendedBands = PriceBollingerBands.Calculate(extended);
        Assert.NotEmpty(originalBands.Points);
        Assert.Equal(originalBands.Points.ToArray(), extendedBands.Points.ToArray());
        Assert.Equal(originalBands.OverflowCount, extendedBands.OverflowCount);
        var originalCci = PriceCommodityChannelIndex.Calculate(original);
        var extendedCci = PriceCommodityChannelIndex.Calculate(extended);
        Assert.NotEmpty(originalCci.Points);
        Assert.Equal(originalCci.Points.ToArray(), extendedCci.Points.ToArray());
        Assert.Equal(originalCci.FlatWindowCount, extendedCci.FlatWindowCount);
        var originalWilliams = PriceWilliamsR.Calculate(original);
        var extendedWilliams = PriceWilliamsR.Calculate(extended);
        Assert.NotEmpty(originalWilliams.Points);
        Assert.Equal(originalWilliams.Points.ToArray(), extendedWilliams.Points.ToArray());
        Assert.Equal(originalWilliams.FlatWindowCount, extendedWilliams.FlatWindowCount);
    }

    [Fact]
    public void HitTestUsesOnlyActualColumnRectangleIncludingItsExactEdges()
    {
        var snapshot = PriceVolumeGeometry.Create(Source(25, 100).Query());
        var column = snapshot.Columns[0];
        var body = column.Body;
        var center = body.Left + body.Width / 2;
        Assert.Equal(.75, body.Top);
        Assert.Equal(.25, body.Height);
        Assert.Equal(column.Hit, PriceVolumeGeometry.HitTest(snapshot, center, .875));
        Assert.Equal(column.Hit, PriceVolumeGeometry.HitTest(snapshot, body.Left, body.Top));
        Assert.Equal(column.Hit, PriceVolumeGeometry.HitTest(snapshot, body.Left + body.Width, 1));
        Assert.Null(PriceVolumeGeometry.HitTest(snapshot, Math.BitDecrement(body.Left), .875));
        Assert.Null(PriceVolumeGeometry.HitTest(snapshot, Math.BitIncrement(body.Left + body.Width), .875));
        Assert.Null(PriceVolumeGeometry.HitTest(snapshot, center, Math.BitDecrement(body.Top)));
        Assert.Null(PriceVolumeGeometry.HitTest(snapshot, .5, 1));
    }

    [Fact]
    public void MissingGapAndZeroSlotsHaveNoInvisibleHitTargets()
    {
        var source = new PriceChartSource("no-hit", "No hit", [Bar(0, null), Gap(1), Bar(2, 0), Bar(3, 10)]);
        var snapshot = PriceVolumeGeometry.Create(source.Query());
        foreach (var x in new[] { .125, .375, .625 })
        {
            Assert.Null(PriceVolumeGeometry.HitTest(snapshot, x, .5));
            Assert.Null(PriceVolumeGeometry.HitTest(snapshot, x, 1));
        }
        Assert.Equal(source.Bars[3], PriceVolumeGeometry.HitTest(snapshot, .875, .5)!.Value.Bar);
    }

    [Fact]
    public void OverlappingRectanglesUseLastDrawnAndNonpositiveHeightsAreNotHittable()
    {
        var snapshot = PriceVolumeGeometry.Create(Source(10, 20).Query());
        var first = snapshot.Columns[0] with { Body = new(.2, .2, .6, .6) };
        var last = snapshot.Columns[1] with { Body = new(.3, .3, .4, .4) };
        var overlap = snapshot with { Columns = ImmutableArray.Create(first, last) };
        Assert.Equal(last.Hit, PriceVolumeGeometry.HitTest(overlap, .5, .5));
        foreach (var height in new[] { 0d, NegativeZero, -.1 })
        {
            var invisible = snapshot with { Columns = ImmutableArray.Create(last with { Body = new(.3, .3, .4, height) }) };
            Assert.Null(PriceVolumeGeometry.HitTest(invisible, .5, .3));
        }
    }

    [Theory]
    [InlineData(-.001, .5)]
    [InlineData(1.001, .5)]
    [InlineData(.5, -.001)]
    [InlineData(.5, 1.001)]
    public void HitTestRejectsCoordinatesOutsideViewport(double x, double y)
    {
        var snapshot = PriceVolumeGeometry.Create(Source(10).Query());
        Assert.Null(PriceVolumeGeometry.HitTest(snapshot, x, y));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void HitTestRejectsNonfiniteCoordinates(double value)
    {
        var snapshot = PriceVolumeGeometry.Create(Source(10).Query());
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceVolumeGeometry.HitTest(snapshot, value, .5));
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceVolumeGeometry.HitTest(snapshot, .5, value));
    }

    [Fact]
    public void NullSnapshotsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => PriceVolumeGeometry.Create(null!));
        Assert.Throws<ArgumentNullException>(() => PriceVolumeGeometry.HitTest(null!, .5, .5));
    }

    private static double NegativeZero => BitConverter.Int64BitsToDouble(long.MinValue);
    private static DateTimeOffset Time(int day) =>
        new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.FromMinutes(330)).AddDays(day);
    private static PriceBar Bar(int index, double? volume) =>
        new(10L + index * 13, Time(index), 10, 12, 8, 11, volume);
    private static PriceBar Gap(int index) => new(10L + index * 13, Time(index), null, null, null, null, null);
    private static PriceChartSource Source(params double?[] volumes) =>
        new("prices", "Prices", volumes.Select((volume, index) => Bar(index, volume)), datasetVersion: 17, sourceVersion: 23);
    private static IEnumerable<long?> Bits(PriceBar bar) =>
        new[] { bar.Open, bar.High, bar.Low, bar.Close, bar.Volume }
            .Select(value => value.HasValue ? (long?)BitConverter.DoubleToInt64Bits(value.Value) : null);
}
