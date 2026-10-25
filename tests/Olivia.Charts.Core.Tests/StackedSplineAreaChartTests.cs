using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class StackedSplineAreaChartTests
{
    [Fact]
    public void OptionsAppendWithoutChangingOldDefaultsAndValidateEvenWhenUnused()
    {
        Assert.Equal(0, (int)StackedAreaChartInterpolation.Linear);
        Assert.Equal(1, (int)StackedAreaChartInterpolation.Step);
        Assert.Equal(2, (int)StackedAreaChartInterpolation.InvertedStep);
        Assert.Equal(3, (int)StackedAreaChartInterpolation.Spline);
        Assert.Equal(StackedAreaChartInterpolation.Linear, StackedAreaChartOptions.Default.Interpolation);
        Assert.Equal(StackedAreaSplineAlgorithm.StrictMinMax, StackedAreaChartOptions.Default.SplineAlgorithm);
        Assert.Equal(.8, StackedAreaChartOptions.Default.LineTension);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StackedAreaChartOptions(splineAlgorithm: (StackedAreaSplineAlgorithm)99));
        foreach (var tension in new[] { -.01, 1.01, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => new StackedAreaChartOptions(lineTension: tension));
    }

    [Theory]
    [InlineData(StackedAreaChartMode.Stacked, StackedAreaSplineAlgorithm.StrictMinMax)]
    [InlineData(StackedAreaChartMode.Stacked, StackedAreaSplineAlgorithm.Cardinal)]
    [InlineData(StackedAreaChartMode.Normalized100, StackedAreaSplineAlgorithm.StrictMinMax)]
    [InlineData(StackedAreaChartMode.Normalized100, StackedAreaSplineAlgorithm.Cardinal)]
    public void AlgorithmsAndTensionsPreserveOriginalIdentityAndSharedBoundaries(
        StackedAreaChartMode mode, StackedAreaSplineAlgorithm algorithm)
    {
        var source = Example();
        var linear = source.Query(new(mode));
        foreach (var tension in new[] { 0d, .8, 1d })
        {
            var frame = source.Query(new(mode, interpolation: StackedAreaChartInterpolation.Spline,
                splineAlgorithm: algorithm, lineTension: tension));
            Assert.Equal(StackedAreaChartStatus.Ready, frame.Status);
            Assert.Equal(linear.Contributions.ToArray(), frame.Contributions.ToArray());
            Assert.Equal(source.RawEntryCount, frame.Points.Length);
            Assert.Equal(source.RawEntryCount, frame.Contributions.Length);
            Assert.All(frame.Contributions, value => Assert.True(source.TryResolve(value.Hit)));
            Assert.All(frame.Polygons, polygon =>
            {
                Assert.True(source.TryResolve(polygon.StartHit)); Assert.True(source.TryResolve(polygon.EndHit));
                Assert.Equal(polygon.StartHit.SeriesId, polygon.EndHit.SeriesId);
                Assert.Equal(polygon.StartHit.X!.Value + 1, polygon.EndHit.X);
            });
            Assert.All(frame.Segments, segment =>
            {
                Assert.True(source.TryResolve(segment.StartHit)); Assert.True(source.TryResolve(segment.EndHit));
                Assert.NotEqual(segment.Start, segment.End);
            });
            var lowerTop = frame.Segments.Where(s => s.SeriesIndex == 0 && s.UpperBoundary).Select(s => (s.Start, s.End)).ToArray();
            var upperBase = frame.Segments.Where(s => s.SeriesIndex == 1 && !s.UpperBoundary).Select(s => (s.Start, s.End)).ToArray();
            Assert.Equal(lowerTop, upperBase);
            var selected = frame.Points[0];
            Assert.Equal(selected.Hit, StackedAreaChartGeometry.HitTest(frame, selected.X,
                (selected.Top + selected.Bottom) / 2, 1000, 1000, 0));
            Assert.Null(StackedAreaChartGeometry.HitTest(frame, .5, .5, 1000, 1000, 0));
            Assert.False(Example().TryResolve(selected.Hit));
            Assert.False(source.TryResolve(selected.Hit with { Sample = selected.Hit.Sample with { Y = 999 } }));
            if (mode == StackedAreaChartMode.Normalized100)
            {
                Assert.All(frame.Contributions.Where(v => v.SeriesIndex == 1), v => Assert.Equal(1, v.StackTop));
                Assert.All(frame.Segments.Where(s => s.SeriesIndex == 1 && s.UpperBoundary), s =>
                { Assert.Equal(ScreenY(1, frame), s.Start.Y); Assert.Equal(ScreenY(1, frame), s.End.Y); });
            }
            Finite(frame);
        }
    }

    [Theory]
    [InlineData(StackedAreaSplineAlgorithm.StrictMinMax)]
    [InlineData(StackedAreaSplineAlgorithm.Cardinal)]
    public void ZeroTensionIsExactlyLinearIncludingClippingCountsAndAdmission(StackedAreaSplineAlgorithm algorithm)
    {
        var source = Example();
        foreach (var mode in Enum.GetValues<StackedAreaChartMode>())
        {
            var linear = source.Query(new(mode, fixedXDomain: new(.25, 2.75), fixedYDomain: new(.1, 6)));
            var zero = source.Query(new(mode, fixedXDomain: new(.25, 2.75), fixedYDomain: new(.1, 6),
                workBudget: source.RequiredWork, interpolation: StackedAreaChartInterpolation.Spline,
                splineAlgorithm: algorithm, lineTension: 0));
            Assert.Equal(linear.Status, zero.Status);
            Assert.Equal(linear.RequiredWork, zero.RequiredWork);
            Assert.Equal(linear.RequiredPoints, zero.RequiredPoints);
            Assert.Equal(linear.RequiredPolygons, zero.RequiredPolygons);
            Assert.Equal(linear.RequiredVertices, zero.RequiredVertices);
            Assert.Equal(linear.RequiredSegments, zero.RequiredSegments);
            Assert.Equal(linear.Contributions.ToArray(), zero.Contributions.ToArray());
            Assert.Equal(linear.Points.ToArray(), zero.Points.ToArray());
            Assert.Equal(linear.Segments.ToArray(), zero.Segments.ToArray());
            Assert.Equal(linear.Polygons.SelectMany(p => p.Vertices).ToArray(), zero.Polygons.SelectMany(p => p.Vertices).ToArray());
        }
    }

    [Theory]
    [InlineData(StackedAreaSplineAlgorithm.StrictMinMax)]
    [InlineData(StackedAreaSplineAlgorithm.Cardinal)]
    public void PositiveTensionIsCurvedAndConvergesToTheLinearChord(StackedAreaSplineAlgorithm algorithm)
    {
        var source = Example();
        var small = source.Query(Spline(algorithm, .01, fixedY: new(0, 10)));
        var normal = source.Query(Spline(algorithm, .8, fixedY: new(0, 10)));
        var strong = source.Query(Spline(algorithm, 1, fixedY: new(0, 10)));
        Assert.Equal(StackedAreaChartStatus.Ready, small.Status);
        Assert.True(Deviation(normal) > .001);
        Assert.True(Deviation(small) < Deviation(normal) / 5);
        Assert.NotEqual(normal.Segments.Select(s => s.Start).ToArray(), strong.Segments.Select(s => s.Start).ToArray());

        static double Deviation(StackedAreaChartSnapshot frame) => frame.Segments
            .Where(s => s.SeriesIndex == 0 && s.UpperBoundary).SelectMany(s => new[] { (s, s.Start), (s, s.End) })
            .Max(item =>
            {
                var (segment, point) = item;
                var x = point.X * 3;
                var first = segment.StartHit; var last = segment.EndHit;
                var y = first.Y!.Value + (last.Y!.Value - first.Y.Value) * (x - first.X!.Value) / (last.X!.Value - first.X.Value);
                return Math.Abs(point.Y - (1 - y / 10));
            });
    }

    [Theory]
    [InlineData(.8)]
    [InlineData(1)]
    public void StrictMinMaxStaysInsideEachOriginalBoundaryHullAndKeepsBandsOrdered(double tension)
    {
        var source = Example();
        foreach (var mode in Enum.GetValues<StackedAreaChartMode>())
        {
            var frame = source.Query(new(mode, interpolation: StackedAreaChartInterpolation.Spline, lineTension: tension));
            foreach (var segment in frame.Segments)
            {
                var a = Assert.Single(frame.Contributions.Where(v => v.Hit == segment.StartHit));
                var b = Assert.Single(frame.Contributions.Where(v => v.Hit == segment.EndHit));
                var ya = segment.UpperBoundary ? a.StackTop : a.StackBase;
                var yb = segment.UpperBoundary ? b.StackTop : b.StackBase;
                var low = Math.Min(ScreenY(ya, frame), ScreenY(yb, frame));
                var high = Math.Max(ScreenY(ya, frame), ScreenY(yb, frame));
                Assert.InRange(segment.Start.Y, low - 1e-14, high + 1e-14);
                Assert.InRange(segment.End.Y, low - 1e-14, high + 1e-14);
            }
            foreach (var s in new[] { 0, 1 })
            {
                var lower = frame.Segments.Where(e => e.SeriesIndex == s && !e.UpperBoundary).ToArray();
                var upper = frame.Segments.Where(e => e.SeriesIndex == s && e.UpperBoundary).ToArray();
                Assert.Equal(lower.Length, upper.Length);
                for (var i = 0; i < lower.Length; i++)
                {
                    Assert.True(lower[i].Start.Y >= upper[i].Start.Y);
                    Assert.True(lower[i].End.Y >= upper[i].End.Y);
                }
            }
            Assert.Equal(mode == StackedAreaChartMode.Normalized100 ? new HistoryValueDomain(0, 1) : new HistoryValueDomain(0, 8), frame.YDomain);
        }
    }

    [Theory]
    [InlineData(StackedAreaChartMode.Stacked)]
    [InlineData(StackedAreaChartMode.Normalized100)]
    public void CardinalOvershootIsVisibleAndCrossingsAreSplitWithoutInventedObservations(StackedAreaChartMode mode)
    {
        var source = new StackedAreaChartSource([
            new("A", "A", [new(10, 0, 20), new(11, 1, 1), new(12, 2, 1), new(13, 3, 20)]),
            new("B", "B", [new(20, 0, 1), new(21, 1, 20), new(22, 2, 20), new(23, 3, 1)])]);
        var frame = source.Query(new(mode, interpolation: StackedAreaChartInterpolation.Spline,
            splineAlgorithm: StackedAreaSplineAlgorithm.Cardinal));
        Assert.Equal(StackedAreaChartStatus.Ready, frame.Status);
        Assert.True(frame.YDomain!.Value.Minimum < 0);
        Assert.Contains(frame.Segments, e => e.SeriesIndex == 0 && e.UpperBoundary && e.Start.Y > ScreenY(0, frame));
        Assert.Equal(8, frame.Points.Length);
        Assert.Equal(8, frame.Contributions.Length);
        // There are two sign crossings in A's middle source pair, each split exactly rather
        // than returning a self-intersecting quadrilateral through the zero baseline.
        Assert.True(frame.Polygons.Count(p => p.SeriesIndex == 0 && p.StartHit.Key == 11) > 32);
        foreach (var polygon in frame.Polygons.Where(p => p.SeriesIndex == 0 && p.StartHit.Key == 11))
        {
            var baseline = ScreenY(0, frame);
            Assert.True(polygon.Vertices.All(v => v.Y <= baseline + 1e-14) || polygon.Vertices.All(v => v.Y >= baseline - 1e-14));
        }
        var strict = source.Query(new(mode, interpolation: StackedAreaChartInterpolation.Spline));
        Assert.Equal(0, strict.YDomain!.Value.Minimum);
        Finite(frame); Finite(strict);
    }

    [Theory]
    [InlineData(StackedAreaSplineAlgorithm.StrictMinMax)]
    [InlineData(StackedAreaSplineAlgorithm.Cardinal)]
    public void HiddenAndRaggedSeriesHaveNoSyntheticSupportOrBrokenSharedEdges(StackedAreaSplineAlgorithm algorithm)
    {
        var source = new StackedAreaChartSource([
            new("A", "A", [new(10, 0, 2), new(11, 1, 4), new(12, 2, 3), new(13, 3, 5)], 7),
            new("B", "B", [new(20, 1, 1), new(21, 2, 2), new(22, 3, 1)], 9),
            new("C", "C", [new(30, 2, 3), new(31, 3, 2)], 13)], 17);
        var before = source.Query();
        var ids = new List<string> { "A", "C" };
        var options = new StackedAreaChartOptions(StackedAreaChartMode.Normalized100, visibleSeriesIds: ids,
            interpolation: StackedAreaChartInterpolation.Spline, splineAlgorithm: algorithm);
        ids.Clear();
        var frame = source.Query(options);
        Assert.Equal(6, frame.Points.Length);
        Assert.DoesNotContain(frame.Contributions, v => v.Hit.SeriesId == "B" || (v.Hit.SeriesId == "C" && v.Hit.X < 2));
        Assert.All(frame.Polygons.Where(p => p.SeriesIndex == 2), p =>
        { Assert.Equal(2, p.StartHit.X); Assert.Equal(3, p.EndHit.X); });
        var a = frame.Segments.Where(e => e.SeriesIndex == 0 && e.StartHit.X == 2 && e.UpperBoundary).Select(e => (e.Start, e.End));
        var c = frame.Segments.Where(e => e.SeriesIndex == 2 && !e.UpperBoundary).Select(e => (e.Start, e.End));
        Assert.Equal(a.ToArray(), c.ToArray());
        Assert.All(frame.Contributions, v => Assert.True(source.TryResolve(v.Hit)));
        Assert.True(source.TryResolve(before.Points[4].Hit));
        Assert.Equal(before.Contributions.ToArray(), source.Query().Contributions.ToArray());
        var none = source.Query(new(visibleSeriesIds: [], interpolation: StackedAreaChartInterpolation.Spline, splineAlgorithm: algorithm));
        Assert.Equal(StackedAreaChartStatus.Ready, none.Status);
        Empty(none); Assert.Null(none.YDomain);
        Finite(frame);
    }

    [Fact]
    public void NormalizedRaggedBirthKeepsOriginalColumnsWithoutPhantomArea()
    {
        var source = new StackedAreaChartSource([
            new("A", "A", [new(1, 0, 2), new(2, 1, 2), new(3, 2, 2)]),
            new("B", "B", [new(4, 1, 2), new(5, 2, 2)])]);
        var frame = source.Query(new(StackedAreaChartMode.Normalized100, interpolation: StackedAreaChartInterpolation.Spline));
        Assert.Equal(5, frame.Points.Length);
        Assert.Equal(1, Assert.Single(frame.Contributions.Where(v => v.Hit.SeriesId == "A" && v.Hit.X == 0)).StackTop);
        Assert.Equal(.5, Assert.Single(frame.Contributions.Where(v => v.Hit.SeriesId == "A" && v.Hit.X == 1)).StackTop);
        Assert.Equal(1, Assert.Single(frame.Contributions.Where(v => v.Hit.SeriesId == "B" && v.Hit.X == 1)).StackTop);
        Assert.DoesNotContain(frame.Polygons, p => p.SeriesIndex == 1 && p.StartHit.X < 1);
        Assert.DoesNotContain(frame.Segments, e => e.SeriesIndex == 1 && e.StartHit.X < 1);
        Assert.Contains(frame.Segments, e => e.SeriesIndex == 0 && e.StartHit.X == 0 && e.UpperBoundary && e.End.Y > 0);
        Finite(frame);
    }

    [Fact]
    public void SeparateNegativeStackUsesSharedSignedBoundaries()
    {
        var source = new StackedAreaChartSource([
            new("P", "P", [new(1, 0, 2), new(2, 1, 3), new(3, 2, 1)]),
            new("N", "N", [new(4, 0, -2), new(5, 1, -4), new(6, 2, -1)]),
            new("M", "M", [new(7, 0, -3), new(8, 1, -1), new(9, 2, -2)])]);
        foreach (var algorithm in Enum.GetValues<StackedAreaSplineAlgorithm>())
        {
            var frame = source.Query(Spline(algorithm));
            Assert.Equal(StackedAreaChartStatus.Ready, frame.Status);
            Assert.Equal(source.Query().Contributions.ToArray(), frame.Contributions.ToArray());
            var n = frame.Segments.Where(e => e.SeriesIndex == 1 && !e.UpperBoundary).Select(e => (e.Start, e.End));
            var m = frame.Segments.Where(e => e.SeriesIndex == 2 && e.UpperBoundary).Select(e => (e.Start, e.End));
            Assert.Equal(n.ToArray(), m.ToArray());
            Finite(frame);
        }
    }

    [Theory]
    [InlineData(StackedAreaSplineAlgorithm.StrictMinMax)]
    [InlineData(StackedAreaSplineAlgorithm.Cardinal)]
    public void FixedClippingHasNoSelectableTessellationOrClosureStrokes(StackedAreaSplineAlgorithm algorithm)
    {
        var source = new StackedAreaChartSource([new("A", "A", [new(1, 0, 10), new(2, 1, 10), new(3, 2, 10)])]);
        var frame = source.Query(new(fixedXDomain: new(.2, .8), fixedYDomain: new(3, 4),
            interpolation: StackedAreaChartInterpolation.Spline, splineAlgorithm: algorithm));
        Assert.Equal(StackedAreaChartStatus.Ready, frame.Status);
        Assert.Empty(frame.Points); Assert.Empty(frame.Segments);
        Assert.NotEmpty(frame.Polygons);
        Assert.Equal(3, frame.Contributions.Length);
        Assert.Null(StackedAreaChartGeometry.HitTest(frame, .5, .5, 400, 300));
        Assert.All(frame.Polygons, p => { Assert.Equal(1, p.StartHit.Key); Assert.Equal(2, p.EndHit.Key); });
        Finite(frame);
        var visible = Example().Query(new(fixedXDomain: new(.25, 2.75), fixedYDomain: new(2, 4),
            interpolation: StackedAreaChartInterpolation.Spline, splineAlgorithm: algorithm));
        Assert.NotEmpty(visible.Segments); Assert.NotEmpty(visible.Polygons); Finite(visible);
    }

    [Fact]
    public void EveryBudgetRefusesAtomicallyAndZeroTensionAvoidsSplineCharges()
    {
        var source = Example();
        var frame = source.Query(Spline());
        Assert.Equal(StackedAreaChartStatus.Ready, frame.Status);
        Assert.True(frame.RequiredWork > source.RequiredWork);
        Assert.Equal(6 * 32, frame.RequiredPolygons);
        Assert.Equal(6 * 64, frame.RequiredSegments);
        var work = source.Query(new(workBudget: frame.RequiredWork - 1, interpolation: StackedAreaChartInterpolation.Spline));
        Assert.Equal(StackedAreaChartStatus.WorkBudgetExceeded, work.Status);
        Assert.False(work.GeometryCountsKnown); Assert.Equal(-1, work.RequiredSegments); Empty(work);
        StackedAreaChartOptions[] insufficient = [
            new(pointBudget: frame.RequiredPoints - 1, interpolation: StackedAreaChartInterpolation.Spline),
            new(polygonBudget: frame.RequiredPolygons - 1, interpolation: StackedAreaChartInterpolation.Spline),
            new(vertexBudget: frame.RequiredVertices - 1, interpolation: StackedAreaChartInterpolation.Spline),
            new(segmentBudget: frame.RequiredSegments - 1, interpolation: StackedAreaChartInterpolation.Spline),
            new(strokeVertexBudget: frame.RequiredStrokeVertices - 1, interpolation: StackedAreaChartInterpolation.Spline)];
        foreach (var options in insufficient)
        {
            var refused = source.Query(options);
            Assert.Equal(StackedAreaChartStatus.GeometryBudgetExceeded, refused.Status);
            Assert.True(refused.GeometryCountsKnown);
            Assert.Equal(frame.RequiredPolygons, refused.RequiredPolygons);
            Assert.Equal(frame.RequiredVertices, refused.RequiredVertices);
            Assert.Equal(frame.RequiredSegments, refused.RequiredSegments); Empty(refused);
        }
        Assert.Equal(StackedAreaChartStatus.Ready, source.Query(new(pointBudget: frame.RequiredPoints,
            polygonBudget: frame.RequiredPolygons, vertexBudget: frame.RequiredVertices,
            segmentBudget: frame.RequiredSegments, strokeVertexBudget: frame.RequiredStrokeVertices,
            workBudget: frame.RequiredWork, interpolation: StackedAreaChartInterpolation.Spline)).Status);
        Assert.Equal(StackedAreaChartStatus.Ready, source.Query(new(workBudget: source.RequiredWork,
            interpolation: StackedAreaChartInterpolation.Spline, lineTension: 0)).Status);
        var maximal = new StackedAreaChartSource(Enumerable.Range(0, 8).Select(s =>
            new AreaSeries(s.ToString(), "Series", Enumerable.Range(0, 256).Select(x => new AreaSample(x, x, 1)))));
        var hidden = maximal.Query(new(visibleSeriesIds: [], interpolation: StackedAreaChartInterpolation.Spline));
        Assert.Equal(StackedAreaChartStatus.WorkBudgetExceeded, hidden.Status); Empty(hidden);
    }

    [Theory]
    [InlineData(StackedAreaSplineAlgorithm.StrictMinMax)]
    [InlineData(StackedAreaSplineAlgorithm.Cardinal)]
    public void EmptySingletonZeroConstantAndExtremeFiniteCoordinatesStayBounded(StackedAreaSplineAlgorithm algorithm)
    {
        var empty = new StackedAreaChartSource([]).Query(Spline(algorithm));
        Assert.Equal(StackedAreaChartStatus.Ready, empty.Status); Empty(empty);
        var single = new StackedAreaChartSource([new("A", "A", [new(7, 3, 2)])]).Query(Spline(algorithm));
        Assert.Single(single.Points); Assert.Empty(single.Polygons); Assert.Empty(single.Segments); Finite(single);
        var zeroSource = new StackedAreaChartSource([new("Z", "Z", [new(1, 0, 0), new(2, 1, 0)])]);
        var zero = zeroSource.Query(new(StackedAreaChartMode.Normalized100,
            interpolation: StackedAreaChartInterpolation.Spline, splineAlgorithm: algorithm));
        Assert.Equal(2, zero.Points.Length); Assert.Empty(zero.Polygons);
        Assert.All(zero.Contributions, v => { Assert.Equal(0, v.StackTop); Assert.Equal(0, v.Proportion); });
        var flatY = zeroSource.Query(new(fixedYDomain: new(0, 0), interpolation: StackedAreaChartInterpolation.Spline, splineAlgorithm: algorithm));
        Assert.Empty(flatY.Polygons);
        Assert.All(flatY.Segments, s => { Assert.Equal(.5, s.Start.Y); Assert.Equal(.5, s.End.Y); });
        var flatX = Example().Query(new(fixedXDomain: new(1, 1), interpolation: StackedAreaChartInterpolation.Spline, splineAlgorithm: algorithm));
        Assert.Empty(flatX.Polygons); Finite(flatX);
        var extreme = new StackedAreaChartSource([new("A", "A", [
            new(1, -double.MaxValue, 2), new(2, 0, 4), new(3, double.MaxValue, 2)])]);
        var clipped = extreme.Query(new(fixedXDomain: new(-1, 1), interpolation: StackedAreaChartInterpolation.Spline, splineAlgorithm: algorithm));
        Assert.Equal(StackedAreaChartStatus.Ready, clipped.Status);
        Assert.Single(clipped.Points); Assert.NotEmpty(clipped.Polygons); Finite(clipped);
        var tiny = new StackedAreaChartSource([new("A", "A", [new(1, 0, double.Epsilon), new(2, 1, 2 * double.Epsilon)])]);
        var subnormal = tiny.Query(Spline(algorithm));
        Assert.Equal(StackedAreaChartStatus.Ready, subnormal.Status); Assert.Equal(2, subnormal.Points.Length); Finite(subnormal);
    }

    [Fact]
    public void UnrepresentableControlsRefuseRatherThanHideOvershootOrDegrade()
    {
        var source = new StackedAreaChartSource([new("A", "A", [
            new(1, 0, double.MaxValue), new(2, 1, double.MaxValue), new(3, 2, 1), new(4, 3, 1)])]);
        var cardinal = source.Query(Spline(StackedAreaSplineAlgorithm.Cardinal));
        Assert.Equal(StackedAreaChartStatus.NumericOverflow, cardinal.Status); Empty(cardinal);
        Assert.False(cardinal.GeometryCountsKnown);
        var strict = source.Query(Spline());
        Assert.Equal(StackedAreaChartStatus.Ready, strict.Status); Finite(strict);
        var overSum = new StackedAreaChartSource([
            new("A", "A", [new(1, 0, double.MaxValue), new(2, 1, double.MaxValue)]),
            new("B", "B", [new(3, 0, double.MaxValue), new(4, 1, double.MaxValue)])]);
        Assert.Equal(StackedAreaChartStatus.NumericOverflow, overSum.Query(Spline()).Status);
        var percent = overSum.Query(new(StackedAreaChartMode.Normalized100, interpolation: StackedAreaChartInterpolation.Spline));
        Assert.Equal(StackedAreaChartStatus.Ready, percent.Status);
        Assert.All(percent.Contributions, v => Assert.Equal(.5, v.Proportion)); Finite(percent);
    }

    [Fact]
    public void OldLinearAndBothStepDirectionsRetainGeometryAndWork()
    {
        var source = new StackedAreaChartSource([
            new("A", "A", [new(1, 0, 2), new(2, 1, 6)]),
            new("B", "B", [new(3, 0, 3), new(4, 1, 1)])]);
        foreach (var interpolation in new[] { StackedAreaChartInterpolation.Linear, StackedAreaChartInterpolation.Step, StackedAreaChartInterpolation.InvertedStep })
        {
            var frame = source.Query(new(fixedYDomain: new(0, 10), interpolation: interpolation,
                splineAlgorithm: StackedAreaSplineAlgorithm.Cardinal, lineTension: 1));
            Assert.Equal(StackedAreaChartStatus.Ready, frame.Status);
            Assert.Equal(2, frame.Polygons.Length);
            Assert.Equal(interpolation == StackedAreaChartInterpolation.Linear ? 4 : 7, frame.Segments.Length);
            Assert.Equal(source.RequiredWork + (interpolation == StackedAreaChartInterpolation.Linear ? 0 : 130), frame.RequiredWork);
            if (interpolation != StackedAreaChartInterpolation.Linear)
                Assert.All(frame.Segments, e => Assert.True(e.Start.X == e.End.X || e.Start.Y == e.End.Y));
            Finite(frame);
        }
    }

    private static StackedAreaChartSource Example() => new([
        new("A", "A", [new(101, 0, 1), new(102, 1, 5), new(103, 2, 2), new(104, 3, 4)], 7),
        new("B", "B", [new(201, 0, 4), new(202, 1, 1), new(203, 2, 6), new(204, 3, 2)], 11)], 13);

    private static StackedAreaChartOptions Spline(StackedAreaSplineAlgorithm algorithm = StackedAreaSplineAlgorithm.StrictMinMax,
        double tension = .8, HistoryValueDomain? fixedY = null) => new(fixedYDomain: fixedY,
            interpolation: StackedAreaChartInterpolation.Spline, splineAlgorithm: algorithm, lineTension: tension);

    private static double ScreenY(double value, StackedAreaChartSnapshot frame) =>
        DualAxisChartGeometry.NormalizeY(-value, new(-frame.YDomain!.Value.Maximum, -frame.YDomain.Value.Minimum));

    private static void Empty(StackedAreaChartSnapshot frame)
    { Assert.Empty(frame.Contributions); Assert.Empty(frame.Points); Assert.Empty(frame.Polygons); Assert.Empty(frame.Segments); }

    private static void Finite(StackedAreaChartSnapshot frame)
    {
        Assert.All(frame.Points, p =>
        {
            Assert.InRange(p.X, 0, 1); Assert.InRange(p.Top, 0, 1); Assert.InRange(p.Bottom, 0, 1);
            Assert.True(double.IsFinite(p.StackBase)); Assert.True(double.IsFinite(p.StackTop));
        });
        Assert.All(frame.Polygons.SelectMany(p => p.Vertices), p => { Assert.InRange(p.X, 0, 1); Assert.InRange(p.Y, 0, 1); });
        Assert.All(frame.Segments, s =>
        {
            Assert.InRange(s.Start.X, 0, 1); Assert.InRange(s.Start.Y, 0, 1);
            Assert.InRange(s.End.X, 0, 1); Assert.InRange(s.End.Y, 0, 1);
        });
    }
}
