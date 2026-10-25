using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class AreaChartTests
{
    [Fact]
    public void CopiesOriginalSamplesAndKeepsKeysVersionsSignedZeroAndOrder()
    {
        var samples = new[] { new AreaSample(7, 10, -0d), new AreaSample(2, 20, 5) };
        var series = new[] { new AreaSeries("s", "Same", samples, 13) };
        var source = new AreaChartSource(series, 17);
        samples[0] = new(99, 99, 99); series[0] = new("other", "Other", []);
        var snapshot = source.Query();
        Assert.Same(source, snapshot.Source);
        Assert.Equal(new long[] { 7, 2 }, snapshot.Points.Select(p => p.Hit.Key));
        Assert.Equal(10, snapshot.Points[0].Hit.X);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0d), BitConverter.DoubleToInt64Bits(snapshot.Points[0].Hit.Y!.Value));
        Assert.Equal(17, snapshot.Points[0].Hit.DatasetVersion);
        Assert.Equal(13, snapshot.Points[0].Hit.SeriesSourceVersion);
        Assert.Equal("s", snapshot.Points[0].Hit.SeriesId);
        var polygon = Assert.Single(snapshot.Polygons);
        Assert.Equal(snapshot.Points[0].Hit, polygon.StartHit);
        Assert.Equal(snapshot.Points[1].Hit, polygon.EndHit);
    }

    [Fact]
    public void AutoYIncludesMathematicalZeroWhileAutoXUsesOnlyCompletePairs()
    {
        var positive = Source(new(1, 10, 10), new(2, 20, 20)).Query();
        Assert.Equal(new HistoryValueDomain(10, 20), positive.XDomain);
        Assert.Equal(new HistoryValueDomain(0, 20), positive.YDomain);
        Assert.Equal(1, positive.BaselinePosition); Assert.True(positive.IsBaselineVisible);
        Assert.Equal(.5, positive.Points[0].Y); Assert.Equal(0, positive.Points[1].Y);
        Assert.Equal(.75, Area(positive), 14);
        var negative = Source(new(1, 10, -20), new(2, 20, -10)).Query();
        Assert.Equal(new HistoryValueDomain(-20, 0), negative.YDomain);
        Assert.Equal(0, negative.BaselinePosition); Assert.True(negative.IsBaselineVisible);
        Assert.Equal(.75, Area(negative), 14);
        Assert.Equal(0, positive.YTicks[0].Value); Assert.Equal(20, positive.YTicks[^1].Value);
        Assert.Equal(0, positive.YTicks[0].Position); Assert.Equal(1, positive.YTicks[^1].Position);
    }

    [Fact]
    public void EveryGapBreaksConnectivityWithoutResettingFiniteXOrder()
    {
        var snapshot = Source(new(1, 0, 1), new(2, 1, 2), new(3, 2, null), new(4, 3, 3),
            new(5, double.NaN, 99), new(6, 4, 4), new(7, null, 1e100), new(8, 5, 5),
            new(9, 6, double.PositiveInfinity), new(10, 7, 7), new(11, double.NegativeInfinity, 3)).Query();
        Assert.Equal(5, snapshot.Gaps.Length); Assert.Equal(6, snapshot.Points.Length);
        var only = Assert.Single(snapshot.Polygons);
        Assert.Equal(1, only.StartHit.Key); Assert.Equal(2, only.EndHit.Key);
        Assert.Equal(new HistoryValueDomain(0, 7), snapshot.XDomain);
        Assert.Equal(new HistoryValueDomain(0, 7), snapshot.YDomain);
        Assert.True(double.IsNaN(snapshot.Gaps[1].X!.Value));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(0d, -0d)]
    public void DuplicateAndDescendingFiniteXAreRejectedEvenAcrossGaps(double first, double second)
    {
        Assert.Throws<ArgumentException>(() => new AreaSeries("s", "S", [new(1, first, 1), new(2, second, 2)]));
        Assert.Throws<ArgumentException>(() => new AreaSeries("s", "S", [new(1, first, 1), new(2, null, null), new(3, second, 2)]));
        Assert.Throws<ArgumentException>(() => new AreaSeries("s", "S", [new(1, first, null), new(2, second, 2)]));
        Assert.Throws<ArgumentException>(() => new AreaSeries("s", "S", [new(1, first, 1), new(2, second, double.NaN)]));
    }

    [Fact]
    public void MultipleSeriesMayReuseXAndSampleKeysButRemainIndependent()
    {
        var source = new AreaChartSource([
            new("a", "Same", [new(1, 0, 1), new(2, 1, 2)], 3),
            new("b", "Same", [new(1, 0, -1), new(2, 1, -2)], 4)], 5);
        var snapshot = source.Query();
        Assert.Equal(new[] { "a", "a", "b", "b" }, snapshot.Points.Select(p => p.Hit.SeriesId));
        Assert.Equal(new[] { 0, 1 }, snapshot.Polygons.Select(p => p.SeriesIndex));
        Assert.All(snapshot.Polygons, p => Assert.Equal(p.StartHit.SeriesId, p.EndHit.SeriesId));
    }

    [Fact]
    public void BaselineCrossingsSplitSimplePolygonsAndNeverCreateInteractiveSamples()
    {
        var snapshot = Source(new(1, 0, -2), new(2, 4, 2)).Query();
        Assert.Equal(2, snapshot.Polygons.Length); Assert.Equal(2, snapshot.Points.Length);
        Assert.All(snapshot.Polygons, p => Assert.Equal(3, p.Vertices.Length));
        Assert.All(snapshot.Polygons, p => Assert.Contains(new HistoryChartPoint(.5, .5), p.Vertices));
        Assert.Equal(.25, Area(snapshot), 14);
        Assert.Null(AreaChartGeometry.HitTest(snapshot, .5, .5));
        Assert.Null(AreaChartGeometry.HitTest(snapshot, .25, .625));
        Assert.Null(AreaChartGeometry.GetHit(snapshot, 2));
        var endpoint = Source(new(1, 0, 0), new(2, 1, 2)).Query();
        Assert.Equal(3, Assert.Single(endpoint.Polygons).Vertices.Length);
        var zero = Source(new(1, 0, 0), new(2, 1, 0)).Query();
        Assert.Empty(zero.Polygons); Assert.Equal(2, zero.Points.Length);
        Assert.Equal(.5, zero.BaselinePosition);
    }

    [Fact]
    public void FixedRectangleClipsLinearAreaRatherThanClampingRawEndpoints()
    {
        var snapshot = Source(new(1, -1, 0), new(2, 3, 4)).Query(new(fixedXDomain: new(0, 2), fixedYDomain: new(1, 3)));
        Assert.Empty(snapshot.Points);
        var polygon = Assert.Single(snapshot.Polygons);
        Assert.Equal(3, polygon.Vertices.Length);
        Assert.Contains(new HistoryChartPoint(0, 1), polygon.Vertices);
        Assert.Contains(new HistoryChartPoint(1, 0), polygon.Vertices);
        Assert.Contains(new HistoryChartPoint(1, 1), polygon.Vertices);
        Assert.Equal(.5, Area(snapshot), 14);
        Assert.False(snapshot.IsBaselineVisible); Assert.Equal(1, snapshot.BaselinePosition);
        Assert.Null(AreaChartGeometry.HitTest(snapshot, 1, 0));
        var above = Source(new(1, -1, 100), new(2, 3, 100)).Query(new(fixedXDomain: new(0, 2), fixedYDomain: new(1, 3)));
        Assert.Equal(1, Area(above), 14); Assert.Empty(above.Points);
        var below = Source(new(1, -1, -100), new(2, 3, -100)).Query(new(fixedXDomain: new(0, 2), fixedYDomain: new(1, 3)));
        Assert.Empty(below.Polygons);
    }

    [Fact]
    public void EmptyAllGapSingletonConstantAndBoundaryOnlyGeometryStayHonest()
    {
        foreach (var source in new[] { new AreaChartSource([]), Source(new(1, null, 2), new(2, 3, null)) })
        {
            var snapshot = source.Query();
            Assert.Empty(snapshot.Points); Assert.Empty(snapshot.Polygons);
            Assert.Null(snapshot.XDomain); Assert.Null(snapshot.YDomain);
            Assert.Empty(snapshot.XTicks); Assert.Empty(snapshot.YTicks);
            Assert.Equal(ChartBudgetStatus.Satisfied, snapshot.BudgetStatus);
            var fixedSnapshot = source.Query(new(fixedXDomain: new(2, 4), fixedYDomain: new(8, 8)));
            Assert.Equal(new HistoryValueDomain(2, 4), fixedSnapshot.XDomain);
            Assert.Equal(new HistoryValueDomain(8, 8), fixedSnapshot.YDomain);
            Assert.Empty(fixedSnapshot.Polygons);
        }
        var singleton = Source(new AreaSample(1, 2, 3)).Query();
        Assert.Single(singleton.Points); Assert.Empty(singleton.Polygons); Assert.Equal(.5, singleton.Points[0].X);
        var constantX = Source(new(1, 2, 3), new(2, 3, 4)).Query(new(fixedXDomain: new(2, 2)));
        Assert.Single(constantX.Points); Assert.Empty(constantX.Polygons);
        var constantY = Source(new(1, 0, 3), new(2, 1, 3)).Query(new(fixedYDomain: new(3, 3)));
        Assert.Equal(2, constantY.Points.Length); Assert.All(constantY.Points, p => Assert.Equal(.5, p.Y));
        Assert.Empty(constantY.Polygons);
        var touch = Source(new(1, 0, 1), new(2, 1, 1)).Query(new(fixedYDomain: new(1, 2)));
        Assert.Empty(touch.Polygons); Assert.Equal(2, touch.Points.Length);
    }

    [Fact]
    public void ExtremeDiagonalClipsTinyWindowsWithoutLosingBoundaryIntersections()
    {
        foreach (var magnitude in new[] { 1d, double.Epsilon, 1e-200 })
        {
            var snapshot = Source(new(1, -double.MaxValue, double.MaxValue), new(2, double.MaxValue, -double.MaxValue))
                .Query(new(fixedXDomain: new(-magnitude, magnitude), fixedYDomain: new(-magnitude, magnitude)));
            Assert.Empty(snapshot.Points); Assert.Equal(2, snapshot.Polygons.Length);
            Assert.Equal(.25, Area(snapshot), 14);
            Assert.Contains(snapshot.Polygons.SelectMany(p => p.Vertices), v => v == new HistoryChartPoint(0, 0));
            Assert.Contains(snapshot.Polygons.SelectMany(p => p.Vertices), v => v == new HistoryChartPoint(1, 1));
            AssertFinite(snapshot);
        }
    }

    [Fact]
    public void SubnormalAdjacentAndExtremeRangesRemainFiniteAndKeepRawIdentity()
    {
        var data = new[]
        {
            new[] { new AreaSample(1, -double.MaxValue, -double.MaxValue), new AreaSample(2, 0, 0), new AreaSample(3, double.MaxValue, double.MaxValue) },
            new[] { new AreaSample(1, -double.Epsilon, -double.Epsilon), new AreaSample(2, 0, 0), new AreaSample(3, double.Epsilon, double.Epsilon) },
            new[] { new AreaSample(1, 1, 1), new AreaSample(2, Math.BitIncrement(1), Math.BitIncrement(1)) }
        };
        foreach (var samples in data)
        {
            var snapshot = Source(samples).Query();
            Assert.Equal(samples, snapshot.Points.Select(p => p.Hit.Sample));
            AssertFinite(snapshot);
            foreach (var ticks in new[] { snapshot.XTicks, snapshot.YTicks })
            {
                Assert.All(ticks, t => { Assert.True(double.IsFinite(t.Value)); Assert.InRange(t.Position, 0, 1); });
                Assert.Equal(ticks.Length, ticks.Select(t => t.Value).Distinct().Count());
            }
        }
        var tinyTop = Source(new(1, 0, 0), new(2, 1, double.Epsilon)).Query(new(fixedXDomain: new(0, 1), fixedYDomain: new(-1, double.Epsilon)));
        Assert.Equal(double.Epsilon, tinyTop.Points[0].Y);
        Assert.Equal(double.Epsilon, tinyTop.BaselinePosition);
        Assert.Equal(1, AreaChartGeometry.HitTest(tinyTop, 0, double.Epsilon)!.Value.Key);
    }

    [Fact]
    public void EveryGeometryBudgetRefusesAtomicallyAndExactThresholdRestores()
    {
        var source = Source(new(1, 0, -2), new(2, 4, 2), new(3, 5, null));
        var complete = source.Query();
        Assert.Equal(2, complete.RequiredPointCount); Assert.Equal(2, complete.RequiredPolygonCount);
        Assert.Equal(6, complete.RequiredVertexCount); Assert.True(complete.GeometryCountsKnown);
        foreach (var options in new[] { new AreaChartOptions(1), new AreaChartOptions(polygonBudget: 1), new AreaChartOptions(vertexBudget: 5) })
        {
            var rejected = source.Query(options);
            Assert.Equal(ChartBudgetStatus.InsufficientBudget, rejected.BudgetStatus);
            Assert.Empty(rejected.Points); Assert.Empty(rejected.Polygons); Assert.Single(rejected.Gaps);
            Assert.Equal(complete.RequiredPointCount, rejected.RequiredPointCount);
            Assert.Equal(complete.RequiredPolygonCount, rejected.RequiredPolygonCount);
            Assert.Equal(complete.RequiredVertexCount, rejected.RequiredVertexCount);
            Assert.True(rejected.GeometryCountsKnown);
            Assert.Null(AreaChartGeometry.GetHit(rejected, 0));
        }
        var restored = source.Query(new(2, polygonBudget: 2, vertexBudget: 6, workBudget: complete.RequiredWorkCount));
        Assert.Equal(ChartBudgetStatus.Satisfied, restored.BudgetStatus);
        Assert.Equal(complete.Points.ToArray(), restored.Points.ToArray()); Assert.Equal(complete.RequiredVertexCount, restored.RequiredVertexCount);
    }

    [Fact]
    public void WorkRefusalIsPreflightAndUnknownGeometryCountsAreExplicit()
    {
        var source = Source(new(1, 0, 1), new(2, 1, 2), new(3, 2, null), new(4, 3, 3), new(5, 4, 4));
        var admitted = source.Query();
        Assert.Equal(5 + 2 * AreaChartSource.WorkPerAdjacentPair, admitted.RequiredWorkCount);
        var rejected = source.Query(new(workBudget: admitted.RequiredWorkCount - 1));
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, rejected.BudgetStatus);
        Assert.False(rejected.GeometryCountsKnown); Assert.Equal(-1, rejected.RequiredPolygonCount); Assert.Equal(-1, rejected.RequiredVertexCount);
        Assert.Equal(4, rejected.RequiredPointCount); Assert.Single(rejected.Gaps);
        Assert.Empty(rejected.Points); Assert.Empty(rejected.Polygons);
        Assert.Equal(ChartBudgetStatus.Satisfied, source.Query(new(workBudget: admitted.RequiredWorkCount)).BudgetStatus);
        Assert.Equal(ChartBudgetStatus.Satisfied, new AreaChartSource([]).Query(new(0, polygonBudget: 0, vertexBudget: 0, workBudget: 0)).BudgetStatus);
    }

    [Fact]
    public void NearestEllipticalHitSelectsOnlyRawPointsWithStableLastDrawnTie()
    {
        var source = new AreaChartSource([
            new("a", "Same", [new(1, .5, .5), new(2, .75, .5)]),
            new("b", "Same", [new(3, .5, .5)])]);
        var snapshot = source.Query(new(fixedXDomain: new(0, 1), fixedYDomain: new(0, 1)));
        Assert.Equal(3, AreaChartGeometry.HitTest(snapshot, .5, .5)!.Value.Key);
        Assert.Equal(2, AreaChartGeometry.HitTest(snapshot, .72, .5, .5)!.Value.Key);
        Assert.Equal(3, AreaChartGeometry.HitTest(snapshot, .625, .5, .5)!.Value.Key);
        Assert.Null(AreaChartGeometry.HitTest(snapshot, .5, .51));
        Assert.NotNull(AreaChartGeometry.HitTest(snapshot, .5, .6, 0, .2));
        Assert.Null(AreaChartGeometry.HitTest(snapshot, .51, .6, 0, .2));
        Assert.Null(AreaChartGeometry.HitTest(snapshot, .6, .7, .1, .2));
        Assert.Null(AreaChartGeometry.HitTest(snapshot, -.01, .5, 1));
        Assert.Equal(new long[] { 1, 2, 3 }, Enumerable.Range(0, 3).Select(i => AreaChartGeometry.GetHit(snapshot, i)!.Value.Key));
        Assert.Null(AreaChartGeometry.GetHit(snapshot, -1)); Assert.Null(AreaChartGeometry.GetHit(snapshot, 3));
    }

    [Theory]
    [InlineData(1e-200)]
    [InlineData(double.Epsilon)]
    public void TinyDistinctHitDistancesNeverTieExactOriginalPoints(double distance)
    {
        var snapshot = Source(new(1, 0, 0), new(2, distance, 0)).Query(new(fixedXDomain: new(0, 1), fixedYDomain: new(0, 0)));
        Assert.Equal(distance, snapshot.Points[1].X);
        Assert.Equal(1, AreaChartGeometry.HitTest(snapshot, 0, .5, 1, 1)!.Value.Key);
        Assert.Equal(2, AreaChartGeometry.HitTest(snapshot, distance, .5, 1, 1)!.Value.Key);
    }

    [Fact]
    public void MaximumAggregateFitsAllHardBoundsAndDuplicateIdentitiesAreRejected()
    {
        var series = Enumerable.Range(0, 8).Select(s => new AreaSeries(s.ToString(), "Same",
            Enumerable.Range(0, 256).Select(i => new AreaSample(i, i, i % 2 == 0 ? -1 : 1)))).ToArray();
        var source = new AreaChartSource(series);
        var snapshot = source.Query();
        Assert.Equal(2048, source.RawEntryCount); Assert.Equal(2048, snapshot.Points.Length);
        Assert.Equal(ChartBudgetStatus.Satisfied, snapshot.BudgetStatus);
        Assert.InRange(snapshot.Polygons.Length, 0, AreaChartSource.MaximumPolygons);
        Assert.InRange(snapshot.RequiredVertexCount, 0, AreaChartSource.MaximumVertices);
        Assert.InRange(snapshot.RequiredWorkCount, 0, AreaChartSource.MaximumWork);
        var overflow = new AreaSeries("overflow", "Overflow", Enumerable.Range(0, 1793).Select(i => new AreaSample(i, i, i)));
        Assert.Throws<ArgumentException>(() => new AreaChartSource([series[0], overflow]));
        Assert.Throws<ArgumentException>(() => new AreaChartSource([series[0], series[0]]));
        Assert.Throws<ArgumentException>(() => new AreaSeries("s", "S", [new(1, 0, 0), new(1, 1, 1)]));
    }

    [Fact]
    public void InfiniteEnumeratorsStopAtCapPlusOneAndAreDisposed()
    {
        var advances = 0; var disposed = false;
        IEnumerable<AreaSample> Samples()
        {
            try { while (true) { advances++; yield return new(advances, advances, advances); } }
            finally { disposed = true; }
        }
        Assert.Throws<ArgumentException>(() => new AreaSeries("s", "S", Samples()));
        Assert.Equal(2049, advances); Assert.True(disposed);
        advances = 0; disposed = false;
        IEnumerable<AreaSeries> Series()
        {
            try { while (true) { advances++; yield return new(advances.ToString(), "S", []); } }
            finally { disposed = true; }
        }
        Assert.Throws<ArgumentException>(() => new AreaChartSource(Series()));
        Assert.Equal(9, advances); Assert.True(disposed);
    }

    [Fact]
    public void InvalidTextVersionsDomainsBudgetsAndHitCoordinatesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new AreaSeries("s", "S", null!));
        Assert.Throws<ArgumentNullException>(() => new AreaChartSource(null!));
        Assert.Throws<ArgumentNullException>(() => new AreaChartSource([null!]));
        foreach (var text in new[] { "", " ", new string('x', 257), "\ud800" })
            Assert.Throws<ArgumentException>(() => new AreaSeries(text, "S", []));
        Assert.Throws<ArgumentException>(() => new AreaSeries("s", "\udc00", []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaSeries("s", "S", [], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaChartSource([], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaChartOptions(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaChartOptions(2049));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaChartOptions(polygonBudget: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaChartOptions(polygonBudget: AreaChartSource.MaximumPolygons + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaChartOptions(vertexBudget: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaChartOptions(vertexBudget: AreaChartSource.MaximumVertices + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaChartOptions(workBudget: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaChartOptions(workBudget: AreaChartSource.MaximumWork + 1));
        var snapshot = Source(new AreaSample(1, 1, 1)).Query();
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => AreaChartGeometry.HitTest(snapshot, value, .5));
            Assert.Throws<ArgumentOutOfRangeException>(() => AreaChartGeometry.HitTest(snapshot, .5, value));
            Assert.Throws<ArgumentOutOfRangeException>(() => AreaChartGeometry.HitTest(snapshot, .5, .5, value, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => AreaChartGeometry.HitTest(snapshot, .5, .5, 0, value));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => AreaChartGeometry.HitTest(snapshot, .5, .5, -.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AreaChartGeometry.HitTest(snapshot, .5, .5, 1.1));
        Assert.Throws<ArgumentNullException>(() => AreaChartGeometry.GetHit(null!, 0));
    }

    private static AreaChartSource Source(params AreaSample[] samples) => new([new("s", "Series", samples)]);
    private static double Area(AreaChartSnapshot snapshot) => snapshot.Polygons.Sum(p =>
    {
        var total = 0d;
        for (var i = 0; i < p.Vertices.Length; i++)
        {
            var a = p.Vertices[i]; var b = p.Vertices[(i + 1) % p.Vertices.Length];
            total += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(total) / 2;
    });
    private static void AssertFinite(AreaChartSnapshot snapshot)
    {
        foreach (var point in snapshot.Polygons.SelectMany(p => p.Vertices).Concat(snapshot.Points.Select(p => new HistoryChartPoint(p.X, p.Y))))
        {
            Assert.True(double.IsFinite(point.X)); Assert.True(double.IsFinite(point.Y));
            Assert.InRange(point.X, 0, 1); Assert.InRange(point.Y, 0, 1);
        }
    }
}
