using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class BarChartTests
{
    [Fact]
    public void CopiesInputsPreservesKeysVersionsAndCallerOrderIndependentOfLabelsAndTimes()
    {
        var categories = new[] { new BarCategory("later", "Same", 200), new BarCategory("early", "Same", 100) };
        var samples = new[] { new BarSample(42, "later", 5), new BarSample(43, "early", -2) };
        var series = new[] { new BarSeries("s", "Series", samples, 17) };
        var source = new BarChartSource(categories, series, 29);
        categories[0] = new BarCategory("changed", "Changed"); samples[0] = new BarSample(0, "changed", 999);
        series[0] = new BarSeries("changed", "Changed", []);
        var snapshot = source.Query();
        Assert.Equal("later", source.Categories[0].Key);
        Assert.Equal("early", source.Categories[1].Key);
        Assert.Equal(42, snapshot.Bars[0].Hit.Key);
        Assert.Equal(200, snapshot.Bars[0].Hit.UtcTicks);
        Assert.Equal(5, snapshot.Bars[0].Hit.Value);
        Assert.Equal(29, snapshot.Bars[0].Hit.DatasetVersion);
        Assert.Equal(17, snapshot.Bars[0].Hit.SeriesSourceVersion);
        Assert.Same(source, snapshot.Source);
        Assert.Equal(new HistoryValueDomain(-2, 5), snapshot.Domain);
    }

    [Fact]
    public void GroupedBarsMapExactPositiveNegativeAndZeroBaselines()
    {
        var source = Source([-4, 0, 8]);
        var snapshot = source.Query();
        Assert.Equal(new HistoryValueDomain(-4, 8), snapshot.Domain);
        Assert.Equal(2d / 3, snapshot.BaselinePosition, 14);
        Assert.Equal(2d / 3, snapshot.Bars[0].Top, 14);
        Assert.Equal(1d / 3, snapshot.Bars[0].Height, 14);
        Assert.Equal(0, snapshot.Bars[1].Height);
        Assert.Equal(0, snapshot.Bars[2].Top);
        Assert.Equal(2d / 3, snapshot.Bars[2].Height, 14);
        Assert.Equal(0.1 / 3, snapshot.Bars[0].Left, 14);
        Assert.Equal(0.8 / 3, snapshot.Bars[0].Width, 14);
        var bar = snapshot.Bars[1];
        Assert.Equal(bar.Hit, BarChartGeometry.HitTest(snapshot, bar.Left + bar.Width / 2, bar.Top));
        Assert.Null(BarChartGeometry.HitTest(snapshot, bar.Left + bar.Width / 2, bar.Top + .001));
        Assert.Equal(bar.Hit, BarChartGeometry.HitTest(snapshot, bar.Left + bar.Width / 2, bar.Top + .001, .002));
    }

    [Fact]
    public void GroupedAndOverlapKeepEveryOriginalIncludingSameSeriesRepeatedCategories()
    {
        var source = new BarChartSource([new("x", "X")],
            [new("a", "A", [new(3, "x", 2), new(4, "x", 2)], 7),
             new("b", "B", [new(3, "x", 2)], 9)], 11);
        var grouped = source.Query();
        Assert.Equal(3, grouped.Bars.Length);
        Assert.Equal(.1, grouped.Bars[0].Left, 14);
        Assert.Equal(.4, grouped.Bars[0].Width, 14);
        Assert.Equal(.5, grouped.Bars[2].Left, 14);
        var overlap = source.Query(new(BarChartLayout.Overlap));
        Assert.All(overlap.Bars, bar => Assert.Equal(.8, bar.Width, 14));
        Assert.Equal("b", BarChartGeometry.HitTest(overlap, .5, .5)!.Value.SeriesId);
        Assert.Equal(new long[] { 3, 4, 3 }, Enumerable.Range(0, 3).Select(i => BarChartGeometry.GetHit(overlap, i)!.Value.Key));
        Assert.Null(BarChartGeometry.GetHit(overlap, -1));
        Assert.Null(BarChartGeometry.GetHit(overlap, 3));
        Assert.Null(BarChartGeometry.HitTest(overlap, -0.01, .5, 1));
    }

    [Fact]
    public void AllNonfiniteAndNullValuesAreExplicitGapsRatherThanZeros()
    {
        var source = Source([null, double.NaN, double.PositiveInfinity, double.NegativeInfinity]);
        var snapshot = source.Query();
        Assert.Empty(snapshot.Bars);
        Assert.Equal(4, snapshot.Gaps.Length);
        Assert.Null(snapshot.Domain);
        Assert.Empty(snapshot.Ticks);
        Assert.Equal(ChartBudgetStatus.Satisfied, snapshot.BudgetStatus);
        Assert.True(double.IsNaN(snapshot.Gaps[1].Value!.Value));
        Assert.Equal(double.PositiveInfinity, snapshot.Gaps[2].Value);
    }

    [Fact]
    public void ViewportUsesCategoryIndicesAndEmptyViewportProducesNoGeometryOrDomain()
    {
        var source = Source([-100, 2, 9]);
        var snapshot = source.Query(new(categoryStart: 1, categoryCount: 1));
        var bar = Assert.Single(snapshot.Bars);
        Assert.Equal(1, bar.CategoryIndex);
        Assert.Equal(1, bar.Hit.Key);
        Assert.Equal(.1, bar.Left, 14);
        Assert.Equal(.8, bar.Width, 14);
        Assert.Equal(new HistoryValueDomain(0, 2), snapshot.Domain);
        foreach (var options in new[] { new BarChartOptions(categoryCount: 0), new BarChartOptions(categoryStart: int.MaxValue) })
        {
            var empty = source.Query(options);
            Assert.Empty(empty.Bars); Assert.Empty(empty.Gaps); Assert.Null(empty.Domain); Assert.Equal(0, empty.CategoryCount);
        }
        Assert.Empty(new BarChartSource([], []).Query().Bars);
    }

    [Fact]
    public void FixedDomainsClipActualIntervalsWithoutFabricatingWhollyOutsideBars()
    {
        var source = Source([-8, 0, 1, 4, 8]);
        var snapshot = source.Query(new(fixedDomain: new HistoryValueDomain(2, 6)));
        Assert.Equal(new long[] { 3, 4 }, snapshot.Bars.Select(bar => bar.Hit.Key));
        Assert.Equal(.5, snapshot.Bars[0].Top);
        Assert.Equal(.5, snapshot.Bars[0].Height);
        Assert.Equal(0, snapshot.Bars[1].Top);
        Assert.Equal(1, snapshot.Bars[1].Height);
        Assert.Equal(8, snapshot.Bars[1].Hit.Value);
        Assert.Equal(1, snapshot.BaselinePosition);
        Assert.False(snapshot.IsBaselineVisible);
        var negative = source.Query(new(fixedDomain: new HistoryValueDomain(-6, -2)));
        Assert.Equal(0, Assert.Single(negative.Bars).Hit.Key);
        Assert.Equal(0, negative.BaselinePosition);
        var constant = source.Query(new(fixedDomain: new HistoryValueDomain(3, 3)));
        Assert.Equal(2, constant.Bars.Length);
        Assert.All(constant.Bars, bar => { Assert.Equal(.5, bar.Top); Assert.Equal(0, bar.Height); });
    }

    [Theory]
    [InlineData(double.MaxValue, -double.MaxValue)]
    [InlineData(double.Epsilon, -double.Epsilon)]
    [InlineData(double.MaxValue, 0)]
    [InlineData(0, -double.MaxValue)]
    [InlineData(0, 0)]
    public void ExtremeAndSubnormalValuesHaveFiniteBoundedGeometry(double first, double second)
    {
        var snapshot = Source([first, second]).Query();
        Assert.Equal(2, snapshot.Bars.Length);
        Assert.All(snapshot.Bars, bar =>
        {
            Assert.InRange(bar.Left, 0, 1); Assert.InRange(bar.Top, 0, 1);
            Assert.InRange(bar.Width, 0, 1); Assert.InRange(bar.Height, 0, 1);
            Assert.InRange(bar.Top + bar.Height, 0, 1);
        });
        Assert.All(snapshot.Ticks, tick => { Assert.True(double.IsFinite(tick.Value)); Assert.InRange(tick.Position, 0, 1); });
        Assert.InRange(snapshot.BaselinePosition, 0, 1);
    }

    [Fact]
    public void RoundedNonzeroSpansRetainExactBitsAndRemainKeyboardAddressable()
    {
        double?[] values = [double.MaxValue, -double.MaxValue, double.Epsilon, -double.Epsilon, 0];
        var snapshot = Source(values).Query();
        Assert.Equal(5, snapshot.Bars.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var hit = BarChartGeometry.GetHit(snapshot, i)!.Value;
            Assert.Equal(i, hit.Key);
            Assert.Equal(BitConverter.DoubleToInt64Bits(values[i]!.Value), BitConverter.DoubleToInt64Bits(hit.Value!.Value));
        }
        Assert.Equal(0, snapshot.Bars[2].Height);
        Assert.Equal(0, snapshot.Bars[3].Height);
        var zero = snapshot.Bars[4];
        var x = zero.Left + zero.Width / 2;
        Assert.Null(BarChartGeometry.HitTest(snapshot, x, zero.Top + .01, .1, .001));
        Assert.Equal(zero.Hit, BarChartGeometry.HitTest(snapshot, x, zero.Top + .01, .001, .02));
    }

    [Fact]
    public void GeometryBudgetRefusesEntireFrameAndCanBeRestoredWithoutRawIdentityLoss()
    {
        var source = Source([1, 0, null, -1]);
        var rejected = source.Query(new(geometryBudget: 2));
        Assert.Equal(3, rejected.RequiredBarCount);
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, rejected.BudgetStatus);
        Assert.Empty(rejected.Bars);
        Assert.Single(rejected.Gaps);
        Assert.Null(BarChartGeometry.GetHit(rejected, 0));
        var accepted = source.Query(new(geometryBudget: 3));
        Assert.Equal(3, accepted.Bars.Length);
        Assert.Equal(new long[] { 0, 1, 3 }, accepted.Bars.Select(bar => bar.Hit.Key));
        Assert.Equal(ChartBudgetStatus.Satisfied, accepted.BudgetStatus);
    }

    [Fact]
    public void AdmissionEnumeratesOnlyCapPlusOneAndDisposesInfiniteInput()
    {
        var advances = 0; var disposed = false;
        IEnumerable<BarSample> Endless()
        {
            try { while (true) { advances++; yield return new BarSample(advances, "x", 1); } }
            finally { disposed = true; }
        }
        Assert.Throws<ArgumentException>(() => new BarSeries("s", "S", Endless()));
        Assert.Equal(2049, advances); Assert.True(disposed);
        var categoryAdvances = 0;
        IEnumerable<BarCategory> Categories()
        {
            while (true) { categoryAdvances++; yield return new BarCategory(categoryAdvances.ToString(), "X"); }
        }
        Assert.Throws<ArgumentException>(() => new BarChartSource(Categories(), []));
        Assert.Equal(257, categoryAdvances);
        var seriesAdvances = 0;
        IEnumerable<BarSeries> Series()
        {
            while (true) { seriesAdvances++; yield return new BarSeries(seriesAdvances.ToString(), "S", []); }
        }
        Assert.Throws<ArgumentException>(() => new BarChartSource([], Series()));
        Assert.Equal(9, seriesAdvances);
    }

    [Fact]
    public void MaximumRawDatasetIsPreservedAndAggregateOverflowIsRejected()
    {
        var categories = Enumerable.Range(0, 256).Select(i => new BarCategory(i.ToString(), "Same")).ToArray();
        var series = Enumerable.Range(0, 8).Select(s => new BarSeries(s.ToString(), "Same",
            Enumerable.Range(0, 256).Select(i => new BarSample(i, i.ToString(), s + i)))).ToArray();
        var source = new BarChartSource(categories, series);
        Assert.Equal(2048, source.RawEntryCount);
        Assert.Equal(2048, source.Query().Bars.Length);
        Assert.Equal(2048, source.Query(new(BarChartLayout.Overlap)).Bars.Length);
        var tooMany = new BarSeries("overflow", "Overflow", Enumerable.Range(0, 2048).Select(i => new BarSample(i, "0", i)));
        Assert.Throws<ArgumentException>(() => new BarChartSource(categories, [tooMany, series[0]]));
    }

    [Fact]
    public void InvalidIdentityOptionsAndCoordinatesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new BarChartSource([new("x", "X"), new("x", "Y")], []));
        Assert.Throws<ArgumentException>(() => new BarChartSource([new("x", "X")], [new("s", "S", [new(1, "missing", 1)])]));
        Assert.Throws<ArgumentException>(() => new BarSeries("s", "S", [new(1, "x", 1), new(1, "x", 2)]));
        Assert.Throws<ArgumentException>(() => new BarChartSource([], [new("s", "S", []), new("s", "Other", [])]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BarCategory("x", "X", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BarChartOptions(layout: (BarChartLayout)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BarChartOptions(categoryStart: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BarChartOptions(categoryCount: 257));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BarChartOptions(geometryBudget: 2049));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BarSeries("s", "S", [], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BarChartSource([], [], -1));
        Assert.Throws<ArgumentException>(() => new BarCategory(new string('x', 257), "X"));
        Assert.Throws<ArgumentException>(() => new BarCategory("x", new string('x', 257)));
        Assert.Throws<ArgumentException>(() => new BarCategory("x", "\uD800"));
        Assert.Throws<ArgumentException>(() => new BarSeries("x", "\uDC00", []));
        Assert.Throws<ArgumentException>(() => new BarSeries("x", "X", [new(1, new string('x', 257), 1)]));
        var snapshot = Source([1]).Query();
        Assert.Throws<ArgumentOutOfRangeException>(() => BarChartGeometry.HitTest(snapshot, double.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BarChartGeometry.HitTest(snapshot, 0, 0, double.PositiveInfinity));
    }

    private static BarChartSource Source(double?[] values) => new(
        values.Select((_, i) => new BarCategory(i.ToString(), "Category " + i)),
        [new BarSeries("series", "Series", values.Select((value, i) => new BarSample(i, i.ToString(), value)))]);
}
