using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class ScatterChartTests
{
    [Fact]
    public void CopiesOriginalPairsAndPreservesIdentityBitsAndSourceOrder()
    {
        var samples = new[] { new ScatterSample(7, 30, -0d), new ScatterSample(2, 10, 5) };
        var series = new[] { new ScatterSeries("s", "Same", samples, 13) };
        var source = new ScatterChartSource(series, 17);
        samples[0] = new(99, 99, 99); series[0] = new("other", "Other", []);
        var snapshot = source.Query();
        Assert.Same(source, snapshot.Source);
        Assert.Equal(new long[] { 7, 2 }, snapshot.Points.Select(p => p.Hit.Key));
        Assert.Equal(30, snapshot.Points[0].Hit.X);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0d), BitConverter.DoubleToInt64Bits(snapshot.Points[0].Hit.Y!.Value));
        Assert.Equal(17, snapshot.Points[0].Hit.DatasetVersion);
        Assert.Equal(13, snapshot.Points[0].Hit.SeriesSourceVersion);
        Assert.Equal("s", snapshot.Points[0].Hit.SeriesId);
    }

    [Fact]
    public void AutoDomainsDoNotForceZeroAndMapScreenCoordinatesIndependently()
    {
        var snapshot = Source(new(1, 10, -20), new(2, 20, -10), new(3, 15, -15)).Query();
        Assert.Equal(new HistoryValueDomain(10, 20), snapshot.XDomain);
        Assert.Equal(new HistoryValueDomain(-20, -10), snapshot.YDomain);
        Assert.Equal(new[] { 0d, 1d, .5 }, snapshot.Points.Select(p => p.X));
        Assert.Equal(new[] { 1d, 0d, .5 }, snapshot.Points.Select(p => p.Y));
        Assert.Equal(10, snapshot.XTicks[0].Value); Assert.Equal(20, snapshot.XTicks[^1].Value);
        Assert.Equal(-20, snapshot.YTicks[0].Value); Assert.Equal(-10, snapshot.YTicks[^1].Value);
        Assert.Equal(0, snapshot.YTicks[0].Position); Assert.Equal(1, snapshot.YTicks[^1].Position);
    }

    [Fact]
    public void EitherInvalidCoordinateIsAnExplicitGapAndNeverInfluencesDomains()
    {
        var snapshot = Source(new(1, null, 1e100), new(2, double.NaN, 2), new(3, 3, double.PositiveInfinity),
            new(4, double.NegativeInfinity, 4), new(5, 1e100, null), new(6, 5, 6)).Query();
        Assert.Equal(5, snapshot.Gaps.Length); Assert.Single(snapshot.Points);
        Assert.Equal(new HistoryValueDomain(5, 5), snapshot.XDomain);
        Assert.Equal(new HistoryValueDomain(6, 6), snapshot.YDomain);
        Assert.Equal(.5, snapshot.Points[0].X); Assert.Equal(.5, snapshot.Points[0].Y);
        Assert.True(double.IsNaN(snapshot.Gaps[1].X!.Value));
        Assert.Equal(double.PositiveInfinity, snapshot.Gaps[2].Y);
        Assert.Single(snapshot.XTicks); Assert.Single(snapshot.YTicks);
    }

    [Fact]
    public void EmptyAndAllGapSourcesHaveNoAutoDomainWhileFixedDomainsRemainExplicit()
    {
        foreach (var source in new[] { new ScatterChartSource([]), Source(new(1, null, 2), new(2, 3, null)) })
        {
            var snapshot = source.Query();
            Assert.Empty(snapshot.Points); Assert.Null(snapshot.XDomain); Assert.Null(snapshot.YDomain);
            Assert.Empty(snapshot.XTicks); Assert.Empty(snapshot.YTicks);
            Assert.Equal(ChartBudgetStatus.Satisfied, snapshot.BudgetStatus);
            var fixedSnapshot = source.Query(new(fixedXDomain: new(2, 4), fixedYDomain: new(8, 8)));
            Assert.Equal(new HistoryValueDomain(2, 4), fixedSnapshot.XDomain);
            Assert.Equal(new HistoryValueDomain(8, 8), fixedSnapshot.YDomain);
            Assert.Empty(fixedSnapshot.Points);
        }
    }

    [Fact]
    public void FixedDomainsClipWholePairsInclusivelyWithoutClampingOutsidePoints()
    {
        var snapshot = Source(new(1, -1, 0), new(2, 0, 0), new(3, 1, 1), new(4, 2, 1),
            new(5, 1, -1), new(6, 1, 2), new(7, null, 1)).Query(new(fixedXDomain: new(0, 1), fixedYDomain: new(0, 1)));
        Assert.Equal(new long[] { 2, 3 }, snapshot.Points.Select(p => p.Hit.Key));
        Assert.Equal(2, snapshot.RequiredPointCount); Assert.Single(snapshot.Gaps);
        Assert.Equal(0, snapshot.Points[0].X); Assert.Equal(1, snapshot.Points[0].Y);
        Assert.Equal(1, snapshot.Points[1].X); Assert.Equal(0, snapshot.Points[1].Y);
        var constant = Source(new(1, 2, 3), new(2, 3, 3)).Query(new(fixedXDomain: new(2, 2)));
        Assert.Single(constant.Points); Assert.Equal(.5, constant.Points[0].X);
    }

    [Fact]
    public void ExtremeSubnormalAndAdjacentDoublesRemainFiniteAndRetainRawPairs()
    {
        var inputs = new[]
        {
            new[] { new ScatterSample(1, -double.MaxValue, double.MaxValue), new ScatterSample(2, 0, 0), new ScatterSample(3, double.MaxValue, -double.MaxValue) },
            new[] { new ScatterSample(1, -double.Epsilon, double.Epsilon), new ScatterSample(2, 0, 0), new ScatterSample(3, double.Epsilon, -double.Epsilon) },
            new[] { new ScatterSample(1, 1, 1), new ScatterSample(2, Math.BitIncrement(1), Math.BitIncrement(1)) }
        };
        foreach (var samples in inputs)
        {
            var snapshot = Source(samples).Query();
            Assert.Equal(samples, snapshot.Points.Select(p => p.Hit.Sample));
            Assert.All(snapshot.Points, p => { Assert.True(double.IsFinite(p.X)); Assert.True(double.IsFinite(p.Y)); Assert.InRange(p.X, 0, 1); Assert.InRange(p.Y, 0, 1); });
            foreach (var ticks in new[] { snapshot.XTicks, snapshot.YTicks })
            {
                Assert.All(ticks, t => { Assert.True(double.IsFinite(t.Value)); Assert.InRange(t.Position, 0, 1); });
                Assert.Equal(ticks.Length, ticks.Select(t => t.Value).Distinct().Count());
            }
        }
        Assert.Equal(.5, Source(inputs[0]).Query().Points[1].X);
    }

    [Fact]
    public void BudgetRejectsEntireGeometryRetainsRequiredCountAndRestoresExactIdentity()
    {
        var source = Source(new(1, 0, 0), new(2, 1, 1), new(3, null, 5));
        var rejected = source.Query(new(1));
        Assert.Empty(rejected.Points); Assert.Equal(2, rejected.RequiredPointCount);
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, rejected.BudgetStatus); Assert.Single(rejected.Gaps);
        Assert.Null(ScatterChartGeometry.GetHit(rejected, 0));
        var restored = source.Query(new(2));
        Assert.Equal(2, restored.Points.Length); Assert.Equal(ChartBudgetStatus.Satisfied, restored.BudgetStatus);
        Assert.Equal(source.Series[0].Samples[0], restored.Points[0].Hit.Sample);
        Assert.Equal(ChartBudgetStatus.Satisfied, source.Query(new(0, new(3, 4))).BudgetStatus);
    }

    [Fact]
    public void NearestEllipticalHitUsesLastDrawnTieAndKeyboardIncludesCoincidentPoints()
    {
        var source = new ScatterChartSource([
            new("a", "Same", [new(8, .5, .5), new(9, .5, .5), new(10, .75, .5)]),
            new("b", "Same", [new(8, .5, .5)])]);
        var snapshot = source.Query(new(fixedXDomain: new(0, 1), fixedYDomain: new(0, 1)));
        Assert.Equal("b", ScatterChartGeometry.HitTest(snapshot, .5, .5)!.Value.SeriesId);
        Assert.Equal(10, ScatterChartGeometry.HitTest(snapshot, .72, .5, .5)!.Value.Key);
        Assert.Equal("b", ScatterChartGeometry.HitTest(snapshot, .625, .5, .5)!.Value.SeriesId);
        Assert.Null(ScatterChartGeometry.HitTest(snapshot, .5, .51));
        Assert.NotNull(ScatterChartGeometry.HitTest(snapshot, .5, .6, 0, .2));
        Assert.Null(ScatterChartGeometry.HitTest(snapshot, .51, .6, 0, .2));
        Assert.Null(ScatterChartGeometry.HitTest(snapshot, .6, .7, .1, .2));
        Assert.NotNull(ScatterChartGeometry.HitTest(snapshot, .5, .625, .01, .125));
        Assert.Null(ScatterChartGeometry.HitTest(snapshot, -.01, .5, 1));
        Assert.Equal(new long[] { 8, 9, 10, 8 }, Enumerable.Range(0, 4).Select(i => ScatterChartGeometry.GetHit(snapshot, i)!.Value.Key));
        Assert.Null(ScatterChartGeometry.GetHit(snapshot, -1)); Assert.Null(ScatterChartGeometry.GetHit(snapshot, 4));
    }

    [Theory]
    [InlineData(1e-200)]
    [InlineData(double.Epsilon)]
    public void TinyDistinctHitDistancesDoNotTieAnExactPoint(double separation)
    {
        var snapshot = Source(new(1, 0, 0), new(2, separation, 0)).Query(
            new(fixedXDomain: new(0, 1), fixedYDomain: new(0, 0)));
        Assert.Equal(separation, snapshot.Points[1].X);
        Assert.Equal(1, ScatterChartGeometry.HitTest(snapshot, 0, .5, 1, 1)!.Value.Key);
        Assert.Equal(2, ScatterChartGeometry.HitTest(snapshot, separation, .5, 1, 1)!.Value.Key);
    }

    [Fact]
    public void RepeatedXAndUnsortedPairsRemainDistinctInOriginalOrder()
    {
        var snapshot = Source(new(3, 5, 8), new(1, 5, -4), new(2, -2, 1), new(4, 5, 8)).Query();
        Assert.Equal(new long[] { 3, 1, 2, 4 }, snapshot.Points.Select(p => p.Hit.Key));
        Assert.Equal(new[] { 1d, 1d, 0d, 1d }, snapshot.Points.Select(p => p.X));
        Assert.Equal(0, snapshot.Points[0].Y); Assert.Equal(1, snapshot.Points[1].Y);
        Assert.Equal(4, ScatterChartGeometry.HitTest(snapshot, 1, 0)!.Value.Key);
    }

    [Fact]
    public void MaximumDatasetIsAdmittedButAggregateOverflowAndDuplicateIdentitiesAreRejected()
    {
        var series = Enumerable.Range(0, 8).Select(s => new ScatterSeries(s.ToString(), "Same",
            Enumerable.Range(0, 256).Select(i => new ScatterSample(i, i, s)))).ToArray();
        var source = new ScatterChartSource(series);
        Assert.Equal(2048, source.RawEntryCount); Assert.Equal(2048, source.Query().Points.Length);
        var overflow = new ScatterSeries("overflow", "Overflow", Enumerable.Range(0, 1793).Select(i => new ScatterSample(i, i, i)));
        Assert.Throws<ArgumentException>(() => new ScatterChartSource([series[0], overflow]));
        Assert.Throws<ArgumentException>(() => new ScatterChartSource([series[0], series[0]]));
        Assert.Throws<ArgumentException>(() => new ScatterSeries("s", "S", [new(1, 0, 0), new(1, 1, 1)]));
    }

    [Fact]
    public void InfiniteInputStopsAtCapPlusOneAndDisposesEnumerators()
    {
        var advances = 0; var disposed = false;
        IEnumerable<ScatterSample> Samples()
        {
            try { while (true) { advances++; yield return new(advances, advances, advances); } }
            finally { disposed = true; }
        }
        Assert.Throws<ArgumentException>(() => new ScatterSeries("s", "S", Samples()));
        Assert.Equal(2049, advances); Assert.True(disposed);
        advances = 0; disposed = false;
        IEnumerable<ScatterSeries> Series()
        {
            try { while (true) { advances++; yield return new(advances.ToString(), "S", []); } }
            finally { disposed = true; }
        }
        Assert.Throws<ArgumentException>(() => new ScatterChartSource(Series()));
        Assert.Equal(9, advances); Assert.True(disposed);
    }

    [Fact]
    public void InvalidIdentityVersionsOptionsAndHitCoordinatesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new ScatterSeries("s", "S", null!));
        Assert.Throws<ArgumentNullException>(() => new ScatterChartSource(null!));
        Assert.Throws<ArgumentNullException>(() => new ScatterChartSource([null!]));
        foreach (var text in new[] { "", " ", new string('x', 257), "\ud800" })
            Assert.Throws<ArgumentException>(() => new ScatterSeries(text, "S", []));
        Assert.Throws<ArgumentException>(() => new ScatterSeries("s", "\udc00", []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScatterSeries("s", "S", [], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScatterChartSource([], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScatterChartOptions(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScatterChartOptions(2049));
        var snapshot = Source(new ScatterSample(1, 1, 1)).Query();
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ScatterChartGeometry.HitTest(snapshot, value, .5));
            Assert.Throws<ArgumentOutOfRangeException>(() => ScatterChartGeometry.HitTest(snapshot, .5, value));
            Assert.Throws<ArgumentOutOfRangeException>(() => ScatterChartGeometry.HitTest(snapshot, .5, .5, value, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ScatterChartGeometry.HitTest(snapshot, .5, .5, 0, value));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => ScatterChartGeometry.HitTest(snapshot, .5, .5, -.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScatterChartGeometry.HitTest(snapshot, .5, .5, 1.1));
        Assert.Throws<ArgumentNullException>(() => ScatterChartGeometry.GetHit(null!, 0));
    }

    private static ScatterChartSource Source(params ScatterSample[] samples) => new([new("s", "Series", samples)]);
}
