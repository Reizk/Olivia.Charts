using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class StackedStepAreaChartTests
{
    [Theory]
    [InlineData(StackedAreaChartInterpolation.Step)]
    [InlineData(StackedAreaChartInterpolation.InvertedStep)]
    public void DirectionUsesOneEndpointBandAndOnlyOrthogonalEdges(StackedAreaChartInterpolation interpolation)
    {
        var source = Example();
        var frame = source.Query(new(fixedYDomain: new(0, 10), interpolation: interpolation));
        Assert.Equal(StackedAreaChartStatus.Ready, frame.Status);
        Assert.Equal(4, frame.Points.Length);
        Assert.Equal(2, frame.Polygons.Length);
        Assert.Equal(7, frame.Segments.Length);
        var after = interpolation == StackedAreaChartInterpolation.Step;
        Rectangle(frame, 0, 0, 1, after ? .8 : .4, 1);
        Rectangle(frame, 1, 0, 1, after ? .5 : .3, after ? .8 : .4);
        var jumpX = after ? 1d : 0d;
        Segment(frame, 0, true, jumpX, .8, jumpX, .4);
        Segment(frame, 1, false, jumpX, .8, jumpX, .4);
        Segment(frame, 1, true, jumpX, .5, jumpX, .3);
        Segment(frame, 1, false, 0, after ? .8 : .4, 1, after ? .8 : .4);
        Segment(frame, 1, true, 0, after ? .5 : .3, 1, after ? .5 : .3);
        Assert.All(frame.Segments, edge =>
        {
            Assert.True(edge.Start.X == edge.End.X || edge.Start.Y == edge.End.Y);
            Assert.NotEqual(edge.Start, edge.End);
            Assert.True(source.TryResolve(edge.StartHit));
            Assert.True(source.TryResolve(edge.EndHit));
        });
    }

    [Theory]
    [InlineData(StackedAreaChartInterpolation.Step)]
    [InlineData(StackedAreaChartInterpolation.InvertedStep)]
    public void InteriorOnlyXWindowHasExpectedClippedPlateauWithoutSelectableCorners(StackedAreaChartInterpolation interpolation)
    {
        var source = Example();
        var frame = source.Query(new(fixedXDomain: new(12.5, 17.5), fixedYDomain: new(1, 6), interpolation: interpolation));
        Assert.Equal(StackedAreaChartStatus.Ready, frame.Status);
        Assert.Empty(frame.Points);
        Assert.Equal(4, frame.Contributions.Length);
        Assert.Null(StackedAreaChartGeometry.HitTest(frame, .5, .5, 100, 100, 0));
        if (interpolation == StackedAreaChartInterpolation.Step)
        {
            Assert.Equal(2, frame.Polygons.Length);
            Rectangle(frame, 0, 0, 1, .8, 1);
            Rectangle(frame, 1, 0, 1, .2, .8);
            Assert.Equal(3, frame.Segments.Length);
            Segment(frame, 0, true, 0, .8, 1, .8);
            Segment(frame, 1, false, 0, .8, 1, .8);
            Segment(frame, 1, true, 0, .2, 1, .2);
        }
        else
        {
            Assert.Single(frame.Polygons);
            Rectangle(frame, 0, 0, 1, 0, 1);
            Assert.Equal(2, frame.Segments.Length);
            Segment(frame, 0, true, 0, 0, 1, 0);
            Segment(frame, 1, false, 0, 0, 1, 0);
        }
        Assert.All(frame.Polygons, polygon =>
        {
            Assert.Equal(10, polygon.StartHit.X);
            Assert.Equal(20, polygon.EndHit.X);
            Assert.True(source.TryResolve(polygon.StartHit));
            Assert.True(source.TryResolve(polygon.EndHit));
        });
    }

    [Fact]
    public void FillClosureAtViewportIsNeverInventedAsBoundaryStroke()
    {
        var frame = Example().Query(new(fixedXDomain: new(12, 18), fixedYDomain: new(3, 4),
            interpolation: StackedAreaChartInterpolation.Step));
        Assert.Single(frame.Polygons);
        Rectangle(frame, 1, 0, 1, 0, 1);
        Assert.Empty(frame.Points);
        Assert.Empty(frame.Segments);
    }

    [Theory]
    [InlineData(StackedAreaChartInterpolation.Step, 15, 20, 1)]
    [InlineData(StackedAreaChartInterpolation.InvertedStep, 10, 15, 0)]
    public void VerticalStepEdgesAreClippedAtTheirActualX(StackedAreaChartInterpolation interpolation,
        double left, double right, double expectedX)
    {
        var frame = Example().Query(new(fixedXDomain: new(left, right), fixedYDomain: new(3, 4),
            interpolation: interpolation));
        Assert.Equal(2, frame.Segments.Length);
        Segment(frame, 0, true, expectedX, 1, expectedX, 0);
        Segment(frame, 1, false, expectedX, 1, expectedX, 0);
        Assert.DoesNotContain(frame.Segments, segment => segment.Start.X != segment.End.X);
    }

    [Theory]
    [InlineData(StackedAreaChartInterpolation.Step)]
    [InlineData(StackedAreaChartInterpolation.InvertedStep)]
    public void NormalizationAndVisibilityKeepTheOriginalRawSelection(StackedAreaChartInterpolation interpolation)
    {
        var source = Example();
        var ordinary = source.Query(new(interpolation: interpolation));
        var percent = source.Query(new(StackedAreaChartMode.Normalized100, interpolation: interpolation));
        Assert.Equal(new HistoryValueDomain(0, 1), percent.YDomain);
        Assert.Equal(2d / 5, Value(percent, "A", 10).StackTop, 12);
        Assert.Equal(6d / 7, Value(percent, "A", 20).StackTop, 12);
        Assert.Equal(3d / 5, Value(percent, "B", 10).Proportion, 12);
        Assert.Equal(1d / 7, Value(percent, "B", 20).Proportion, 12);
        Rectangle(percent, 1, 0, 1, 0, interpolation == StackedAreaChartInterpolation.Step ? .6 : 1d / 7);
        Assert.Equal(ordinary.Contributions.Select(v => v.Hit).ToArray(), percent.Contributions.Select(v => v.Hit).ToArray());
        var selected = percent.Points[0].Hit;
        Assert.Equal(101, selected.Key);
        Assert.Equal(2, selected.Y);
        Assert.Equal(5, selected.SeriesSourceVersion);
        Assert.Equal(11, selected.DatasetVersion);
        Assert.Equal(selected, StackedAreaChartGeometry.HitTest(percent, 0, .8, 100, 100, 0));
        Assert.Null(StackedAreaChartGeometry.HitTest(percent, .5, .8, 100, 100, 0));
        var ids = new List<string> { "B" };
        var options = new StackedAreaChartOptions(StackedAreaChartMode.Normalized100,
            visibleSeriesIds: ids, interpolation: interpolation);
        ids.Clear();
        var hidden = source.Query(options);
        Assert.Equal(2, hidden.Points.Length);
        Rectangle(hidden, 1, 0, 1, 0, 1);
        Assert.All(hidden.Contributions, value =>
        {
            Assert.Equal("B", value.Hit.SeriesId);
            Assert.Equal(0, value.StackBase);
            Assert.Equal(1, value.StackTop);
            Assert.Equal(1, value.Proportion);
        });
        Assert.True(source.TryResolve(selected));
        Assert.False(Example().TryResolve(selected));
        Assert.False(source.TryResolve(selected with { Sample = selected.Sample with { Y = .4 } }));
        Assert.Equal(selected, source.Query(new(interpolation: interpolation)).Points[0].Hit);
        var none = source.Query(new(visibleSeriesIds: [], interpolation: interpolation));
        Assert.Empty(none.Contributions); Assert.Empty(none.Points); Assert.Empty(none.Polygons); Assert.Empty(none.Segments);
        Assert.Null(none.YDomain);
    }

    [Theory]
    [InlineData(StackedAreaChartInterpolation.Step)]
    [InlineData(StackedAreaChartInterpolation.InvertedStep)]
    public void NegativeOrdinaryBandsStackSeparatelyAndUseTheSameStepDirection(StackedAreaChartInterpolation interpolation)
    {
        var source = new StackedAreaChartSource([
            new("P", "Positive", [new(1, 0, 4), new(2, 1, 2)]),
            new("N", "Negative", [new(3, 0, -2), new(4, 1, -4)]),
            new("M", "Negative second", [new(5, 0, -3), new(6, 1, -1)])]);
        var frame = source.Query(new(fixedYDomain: new(-10, 10), interpolation: interpolation));
        Assert.Equal(-2, Value(frame, "M", 0).StackBase);
        Assert.Equal(-4, Value(frame, "M", 1).StackBase);
        Assert.Equal(-5, Value(frame, "M", 0).StackTop);
        Assert.Equal(-5, Value(frame, "M", 1).StackTop);
        Rectangle(frame, 2, 0, 1, interpolation == StackedAreaChartInterpolation.Step ? .6 : .7, .75);
        var jumpX = interpolation == StackedAreaChartInterpolation.Step ? 1d : 0d;
        Segment(frame, 2, true, jumpX, .6, jumpX, .7);
        Assert.Throws<ArgumentException>(() => source.Query(new(StackedAreaChartMode.Normalized100, interpolation: interpolation)));
        Assert.Equal(StackedAreaChartStatus.Ready, source.Query(new(StackedAreaChartMode.Normalized100,
            visibleSeriesIds: ["P"], interpolation: interpolation)).Status);
    }

    [Theory]
    [InlineData(StackedAreaChartInterpolation.Step)]
    [InlineData(StackedAreaChartInterpolation.InvertedStep)]
    public void StaggeredSupportNeverExtendsBandsOrInventsPreBirthObservations(StackedAreaChartInterpolation interpolation)
    {
        var source = new StackedAreaChartSource([
            new("A", "A", [new(1, 0, 2), new(2, 1, 2), new(3, 2, 2)]),
            new("B", "B", [new(4, 1, 3), new(5, 2, 3)])]);
        var frame = source.Query(new(interpolation: interpolation));
        Assert.Equal(5, frame.Points.Length);
        Assert.Equal(3, frame.Polygons.Length);
        var band = Assert.Single(frame.Polygons.Where(p => p.SeriesIndex == 1));
        Assert.Equal(4, band.StartHit.Key);
        Assert.Equal(5, band.EndHit.Key);
        Assert.All(band.Vertices, vertex => Assert.InRange(vertex.X, .5, 1));
        Assert.DoesNotContain(frame.Contributions, value => value.Hit.SeriesId == "B" && value.Hit.X == 0);
        var beforeBirth = source.Query(new(fixedXDomain: new(.2, .8), interpolation: interpolation));
        Assert.Single(beforeBirth.Polygons);
        Assert.All(beforeBirth.Polygons, polygon => Assert.Equal(0, polygon.SeriesIndex));
    }

    [Theory]
    [InlineData(StackedAreaChartInterpolation.Step)]
    [InlineData(StackedAreaChartInterpolation.InvertedStep)]
    public void ZeroTotalsAndConstantDomainsStayFiniteWithoutFakeFill(StackedAreaChartInterpolation interpolation)
    {
        var source = new StackedAreaChartSource([new("Z", "Zero", [new(1, 0, 0), new(2, 1, 0)])]);
        var zero = source.Query(new(StackedAreaChartMode.Normalized100, interpolation: interpolation));
        Assert.Equal(2, zero.Points.Length);
        Assert.Empty(zero.Polygons);
        Assert.All(zero.Contributions, value =>
        {
            Assert.Equal(0, value.Proportion); Assert.Equal(0, value.StackBase); Assert.Equal(0, value.StackTop);
        });
        Assert.Equal(zero.Points[0].Hit, StackedAreaChartGeometry.HitTest(zero, 0, 1, 100, 100, 0));
        var flatY = source.Query(new(fixedYDomain: new(0, 0), interpolation: interpolation));
        Assert.Empty(flatY.Polygons);
        Assert.All(flatY.Segments, segment => { Assert.Equal(.5, segment.Start.Y); Assert.Equal(.5, segment.End.Y); });
        var x = interpolation == StackedAreaChartInterpolation.Step ? 20d : 10d;
        var flatX = Example().Query(new(fixedXDomain: new(x, x), fixedYDomain: new(3, 4), interpolation: interpolation));
        Assert.Empty(flatX.Polygons);
        Assert.Equal(2, flatX.Segments.Length);
        Assert.All(flatX.Segments, segment => { Assert.Equal(.5, segment.Start.X); Assert.Equal(.5, segment.End.X); });
    }

    [Theory]
    [InlineData(StackedAreaChartInterpolation.Step)]
    [InlineData(StackedAreaChartInterpolation.InvertedStep)]
    public void ExtremeFiniteXClipsHorizontalPlateauExactly(StackedAreaChartInterpolation interpolation)
    {
        var source = new StackedAreaChartSource([new("A", "A", [
            new(1, -double.MaxValue, 2), new(2, double.MaxValue, 6)])]);
        var frame = source.Query(new(fixedXDomain: new(-1, 1), fixedYDomain: new(0, 10), interpolation: interpolation));
        Assert.Equal(StackedAreaChartStatus.Ready, frame.Status);
        Assert.Empty(frame.Points);
        Rectangle(frame, 0, 0, 1, interpolation == StackedAreaChartInterpolation.Step ? .8 : .4, 1);
        Assert.Equal(2, frame.Segments.Length);
        Assert.All(frame.Segments, segment =>
        {
            Assert.Equal(0, segment.Start.X); Assert.Equal(1, segment.End.X);
            Assert.Equal(segment.Start.Y, segment.End.Y);
        });
    }

    [Theory]
    [InlineData(StackedAreaChartInterpolation.Step)]
    [InlineData(StackedAreaChartInterpolation.InvertedStep)]
    public void AdditionalBoundaryWorkAndEveryGeometryBudgetRefuseAtomically(StackedAreaChartInterpolation interpolation)
    {
        var source = Example();
        var expectedWork = source.RequiredWork + 65 * 2;
        var frame = source.Query(new(fixedYDomain: new(0, 10), workBudget: expectedWork, interpolation: interpolation));
        Assert.Equal(StackedAreaChartStatus.Ready, frame.Status);
        Assert.Equal(expectedWork, frame.RequiredWork);
        Assert.Equal(4, frame.RequiredPoints);
        Assert.Equal(2, frame.RequiredPolygons);
        Assert.Equal(8, frame.RequiredVertices);
        Assert.Equal(7, frame.RequiredSegments);
        Assert.Equal(14, frame.RequiredStrokeVertices);
        var workRefusal = source.Query(new(workBudget: expectedWork - 1, interpolation: interpolation));
        Assert.Equal(StackedAreaChartStatus.WorkBudgetExceeded, workRefusal.Status);
        Assert.Equal(expectedWork, workRefusal.RequiredWork);
        Assert.False(workRefusal.GeometryCountsKnown);
        Assert.Equal(-1, workRefusal.RequiredSegments);
        Empty(workRefusal);
        StackedAreaChartOptions[] insufficient = [
            new(fixedYDomain: new(0, 10), pointBudget: 3, interpolation: interpolation),
            new(fixedYDomain: new(0, 10), polygonBudget: 1, interpolation: interpolation),
            new(fixedYDomain: new(0, 10), vertexBudget: 7, interpolation: interpolation),
            new(fixedYDomain: new(0, 10), segmentBudget: 6, interpolation: interpolation),
            new(fixedYDomain: new(0, 10), strokeVertexBudget: 13, interpolation: interpolation)];
        foreach (var options in insufficient)
        {
            var refused = source.Query(options);
            Assert.Equal(StackedAreaChartStatus.GeometryBudgetExceeded, refused.Status);
            Assert.True(refused.GeometryCountsKnown);
            Assert.Equal(7, refused.RequiredSegments);
            Assert.Equal(8, refused.RequiredVertices);
            Empty(refused);
        }
        Assert.Equal(StackedAreaChartStatus.Ready, source.Query(new(fixedYDomain: new(0, 10), pointBudget: 4,
            polygonBudget: 2, vertexBudget: 8, segmentBudget: 7, strokeVertexBudget: 14,
            workBudget: expectedWork, interpolation: interpolation)).Status);
    }

    [Fact]
    public void MaximumInputIncludesExtraStepWorkEvenWhenEverySeriesIsHidden()
    {
        var source = new StackedAreaChartSource(Enumerable.Range(0, 8).Select(s =>
            new AreaSeries(s.ToString(), "Series", Enumerable.Range(0, 256).Select(x => new AreaSample(x, x, 1)))));
        Assert.Equal(397376, source.RequiredWork);
        var frame = source.Query(new(visibleSeriesIds: [], interpolation: StackedAreaChartInterpolation.Step));
        Assert.Equal(529976, frame.RequiredWork);
        Assert.Equal(StackedAreaChartStatus.WorkBudgetExceeded, frame.Status);
        Assert.False(frame.GeometryCountsKnown);
        Empty(frame);
    }

    [Theory]
    [InlineData(StackedAreaChartInterpolation.Step)]
    [InlineData(StackedAreaChartInterpolation.InvertedStep)]
    public void ScaledNormalizationRemainsFiniteWhenOrdinaryStackOverflows(StackedAreaChartInterpolation interpolation)
    {
        var source = new StackedAreaChartSource([
            new("A", "A", [new(1, 0, double.MaxValue), new(2, 1, double.MaxValue)]),
            new("B", "B", [new(3, 0, double.MaxValue), new(4, 1, double.MaxValue)])]);
        var ordinary = source.Query(new(interpolation: interpolation));
        Assert.Equal(StackedAreaChartStatus.NumericOverflow, ordinary.Status);
        Empty(ordinary);
        var percent = source.Query(new(StackedAreaChartMode.Normalized100, interpolation: interpolation));
        Assert.Equal(StackedAreaChartStatus.Ready, percent.Status);
        Rectangle(percent, 0, 0, 1, .5, 1);
        Rectangle(percent, 1, 0, 1, 0, .5);
        Assert.Equal(4, percent.Points.Length);
        Assert.All(percent.Contributions, value =>
        {
            Assert.Equal(double.MaxValue, value.Hit.Y);
            Assert.Equal(.5, value.Proportion);
            Assert.True(source.TryResolve(value.Hit));
        });
    }

    [Fact]
    public void DefaultLinearRetainsIndependentExpectedGeometryAndOriginalWorkCharge()
    {
        var source = Example();
        var frame = source.Query(new(fixedYDomain: new(0, 10)));
        Assert.Equal(StackedAreaChartInterpolation.Linear, StackedAreaChartOptions.Default.Interpolation);
        Assert.Equal(StackedAreaChartInterpolation.Linear, frame.Options.Interpolation);
        Assert.Equal(source.RequiredWork, frame.RequiredWork);
        Assert.Equal(StackedAreaChartStatus.Ready, source.Query(new(workBudget: source.RequiredWork)).Status);
        Assert.Equal(4, frame.Segments.Length);
        var band = Assert.Single(frame.Polygons.Where(p => p.SeriesIndex == 1));
        Vertices(band, [(0, .8), (0, .5), (1, .3), (1, .4)]);
        Segment(frame, 1, false, 0, .8, 1, .4);
        Segment(frame, 1, true, 0, .5, 1, .3);
        var explicitLinear = source.Query(new(fixedYDomain: new(0, 10), interpolation: StackedAreaChartInterpolation.Linear));
        Assert.Equal(frame.Contributions.ToArray(), explicitLinear.Contributions.ToArray());
        Assert.Equal(frame.Points.ToArray(), explicitLinear.Points.ToArray());
        Assert.Equal(frame.Segments.ToArray(), explicitLinear.Segments.ToArray());
        Assert.Equal(frame.Polygons.SelectMany(p => p.Vertices).ToArray(), explicitLinear.Polygons.SelectMany(p => p.Vertices).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => new StackedAreaChartOptions(interpolation: (StackedAreaChartInterpolation)99));
    }

    private static StackedAreaChartSource Example() => new([
        new("A", "A", [new(101, 10, 2), new(102, 20, 6)], 5),
        new("B", "B", [new(201, 10, 3), new(202, 20, 1)], 7)], 11);

    private static StackedAreaChartContribution Value(StackedAreaChartSnapshot frame, string series, double x) =>
        Assert.Single(frame.Contributions.Where(value => value.Hit.SeriesId == series && value.Hit.X == x));

    private static void Rectangle(StackedAreaChartSnapshot frame, int series, double left, double right, double top, double bottom) =>
        Vertices(Assert.Single(frame.Polygons.Where(p => p.SeriesIndex == series)),
            [(left, bottom), (left, top), (right, top), (right, bottom)]);

    private static void Vertices(StackedAreaChartPolygon polygon, (double X, double Y)[] expected)
    {
        Assert.Equal(expected.Length, polygon.Vertices.Length);
        var actual = polygon.Vertices.OrderBy(v => v.X).ThenBy(v => v.Y).ToArray();
        var sorted = expected.OrderBy(v => v.X).ThenBy(v => v.Y).ToArray();
        for (var i = 0; i < sorted.Length; i++)
        {
            Assert.Equal(sorted[i].X, actual[i].X, 12);
            Assert.Equal(sorted[i].Y, actual[i].Y, 12);
        }
    }

    private static void Segment(StackedAreaChartSnapshot frame, int series, bool upper,
        double x0, double y0, double x1, double y1)
    {
        var segment = Assert.Single(frame.Segments.Where(s => s.SeriesIndex == series && s.UpperBoundary == upper &&
            Math.Abs(s.Start.X - x0) < 1e-12 && Math.Abs(s.Start.Y - y0) < 1e-12 &&
            Math.Abs(s.End.X - x1) < 1e-12 && Math.Abs(s.End.Y - y1) < 1e-12));
        Assert.Equal(x0, segment.Start.X, 12); Assert.Equal(y0, segment.Start.Y, 12);
        Assert.Equal(x1, segment.End.X, 12); Assert.Equal(y1, segment.End.Y, 12);
    }

    private static void Empty(StackedAreaChartSnapshot frame)
    {
        Assert.Empty(frame.Contributions); Assert.Empty(frame.Points);
        Assert.Empty(frame.Polygons); Assert.Empty(frame.Segments);
    }
}
