using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class SplineAreaChartTests
{
    [Fact]
    public void OptionsAppendWithoutChangingOldDefaultsAndValidateUnusedArguments()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, Enum.GetValues<AreaChartMode>().Select(v => (int)v).ToArray());
        Assert.Equal(AreaChartMode.LinearArea, AreaChartOptions.Default.Mode);
        Assert.Equal(AreaSplineAlgorithm.StrictMinMax, AreaChartOptions.Default.SplineAlgorithm);
        Assert.Equal(.8, AreaChartOptions.Default.LineTension);
        Assert.True(AreaChartOptions.Default.IncludeZeroInAutoRange);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaChartOptions(mode: (AreaChartMode)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaChartOptions(splineAlgorithm: (AreaSplineAlgorithm)99));
        foreach (var tension in new[] { -.01, 1.01, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => new AreaChartOptions(lineTension: tension));
    }

    [Theory]
    [InlineData(AreaSplineAlgorithm.StrictMinMax, true)]
    [InlineData(AreaSplineAlgorithm.StrictMinMax, false)]
    [InlineData(AreaSplineAlgorithm.Cardinal, true)]
    [InlineData(AreaSplineAlgorithm.Cardinal, false)]
    public void ZeroTensionExactlyMatchesLinearGeometryCountsAndAdmission(AreaSplineAlgorithm algorithm, bool zero)
    {
        var source = new AreaChartSource([new("A", "A", [new(1, -2, -4), new(2, 1, 8),
            new(3, 2, null), new(4, 3, 2), new(5, 5, -3)], 7)], 11);
        foreach (var clipped in new[] { false, true })
        {
            HistoryValueDomain? xd = clipped ? new(-1, 4) : null;
            HistoryValueDomain? yd = clipped ? new(-2, 6) : null;
            var linear = source.Query(new(fixedXDomain: xd, fixedYDomain: yd, includeZeroInAutoRange: zero));
            var spline = source.Query(new(fixedXDomain: xd, fixedYDomain: yd, mode: AreaChartMode.SplineArea,
                splineAlgorithm: algorithm, lineTension: 0, includeZeroInAutoRange: zero));
            SameGeometry(linear, spline);
            Assert.Null(spline.SplineStatus);
            Assert.Equal(source.RawEntryCount + 2 * AreaChartSource.WorkPerAdjacentPair, spline.RequiredWorkCount);
            var refusedLinear = source.Query(new(workBudget: linear.RequiredWorkCount - 1,
                fixedXDomain: xd, fixedYDomain: yd, includeZeroInAutoRange: zero));
            var refusedSpline = source.Query(new(workBudget: linear.RequiredWorkCount - 1,
                fixedXDomain: xd, fixedYDomain: yd, mode: AreaChartMode.SplineArea, splineAlgorithm: algorithm,
                lineTension: 0, includeZeroInAutoRange: zero));
            SameGeometry(refusedLinear, refusedSpline);
        }
    }

    [Theory]
    [InlineData(AreaSplineAlgorithm.StrictMinMax)]
    [InlineData(AreaSplineAlgorithm.Cardinal)]
    public void OriginalIdentityAndOnlyActualSamplesRemainInteractive(AreaSplineAlgorithm algorithm)
    {
        var source = Example(); var frame = source.Query(Spline(algorithm));
        ReadyFinite(frame);
        Assert.Same(source, frame.Source); Assert.Equal(13, frame.DatasetVersion);
        Assert.Equal(4, frame.Points.Length); Assert.Empty(frame.Gaps);
        Assert.All(frame.Points, point =>
        {
            Assert.Equal(7, point.Hit.SeriesSourceVersion);
            Assert.Equal(source.Series[0].Samples.Single(s => s.Key == point.Hit.Key), point.Hit.Sample);
            Assert.Equal(point.Hit, AreaChartGeometry.HitTest(frame, point.X, point.Y));
        });
        Assert.All(frame.Segments, edge =>
        {
            Assert.NotEqual(edge.Start, edge.End);
            Assert.Equal(edge.StartHit.SeriesId, edge.EndHit.SeriesId);
            Assert.Equal(edge.StartHit.Key + 1, edge.EndHit.Key);
            Assert.Equal(13, edge.StartHit.DatasetVersion);
        });
        var middle = frame.Segments[15].End;
        Assert.Null(AreaChartGeometry.HitTest(frame, middle.X, middle.Y));
        Assert.Null(AreaChartGeometry.GetHit(frame, 4));
    }

    [Theory]
    [InlineData(AreaSplineAlgorithm.StrictMinMax, 3.4)]
    [InlineData(AreaSplineAlgorithm.Cardinal, 3.35)]
    public void FirstPairMidpointMatchesAnIndependentCubicOracle(AreaSplineAlgorithm algorithm, double expected)
    {
        // Knots 1,5,2,4, unit X intervals, tension .8: first control 31/15.
        // Strict's peak tangent is zero, so second control is 5; Cardinal's is 73/15.
        // At t=.5 the Bezier weights are 1,3,3,1 divided by 8 and X=.5.
        var frame = Example().Query(Spline(algorithm, fixedY: new(0, 10)));
        ReadyFinite(frame);
        var middle = frame.Segments.Where(s => s.StartHit.Key == 101).ElementAt(15).End;
        Assert.Equal(1d / 6, middle.X, 14);
        Assert.Equal(expected, 10 * (1 - middle.Y), 12);
    }

    [Theory]
    [InlineData(AreaSplineAlgorithm.StrictMinMax)]
    [InlineData(AreaSplineAlgorithm.Cardinal)]
    public void TensionChangesCurvatureAndSmallPositiveTensionApproachesTheChord(AreaSplineAlgorithm algorithm)
    {
        var source = Example();
        var small = source.Query(Spline(algorithm, .001, new(0, 10)));
        var normal = source.Query(Spline(algorithm, .8, new(0, 10)));
        var strong = source.Query(Spline(algorithm, 1, new(0, 10)));
        ReadyFinite(small); ReadyFinite(normal); ReadyFinite(strong);
        Assert.True(Deviation(normal) > .01);
        Assert.True(Deviation(small) < Deviation(normal) / 100);
        Assert.NotEqual(normal.Segments.ToArray(), strong.Segments.ToArray());

        static double Deviation(AreaChartSnapshot frame) => frame.Segments.SelectMany(s => new[] { (s, s.Start), (s, s.End) })
            .Max(item =>
            {
                var (segment, point) = item; var x = point.X * 3;
                var a = segment.StartHit; var b = segment.EndHit;
                var chord = a.Y!.Value + (b.Y!.Value - a.Y.Value) * (x - a.X!.Value) / (b.X!.Value - a.X.Value);
                return Math.Abs((1 - point.Y) * 10 - chord);
            });
    }

    [Fact]
    public void StrictMinMaxKeepsEverySegmentInsideItsOriginalPairHull()
    {
        var source = new AreaChartSource([new("A", "A", [new(1, 0, -8), new(2, .001, 7),
            new(3, 3, -2), new(4, 4, -2), new(5, 9, 9)])]);
        foreach (var tension in new[] { .8, 1d })
        {
            var frame = source.Query(Spline(tension: tension)); ReadyFinite(frame);
            Assert.Equal(new HistoryValueDomain(-8, 9), frame.YDomain);
            foreach (var edge in frame.Segments)
            {
                var a = ScreenY(edge.StartHit.Y!.Value, frame); var b = ScreenY(edge.EndHit.Y!.Value, frame);
                Assert.InRange(edge.Start.Y, Math.Min(a, b) - 1e-14, Math.Max(a, b) + 1e-14);
                Assert.InRange(edge.End.Y, Math.Min(a, b) - 1e-14, Math.Max(a, b) + 1e-14);
            }
        }
    }

    [Fact]
    public void CardinalOvershootExpandsAutoDomainAndZeroCrossingsNeverBecomeBowTies()
    {
        var source = new AreaChartSource([new("A", "A", [new(10, 0, 20), new(11, 1, 1), new(12, 2, 1), new(13, 3, 20)])]);
        var frame = source.Query(Spline(AreaSplineAlgorithm.Cardinal, includeZero: false)); ReadyFinite(frame);
        Assert.Equal(-23d / 15, frame.YDomain!.Value.Minimum, 12);
        Assert.Equal(20, frame.YDomain.Value.Maximum);
        var middle = frame.Segments.Where(s => s.StartHit.Key == 11).ElementAt(15).End;
        Assert.Equal(-.9, frame.YDomain.Value.Maximum - middle.Y * (frame.YDomain.Value.Maximum - frame.YDomain.Value.Minimum), 12);
        var baseline = ScreenY(0, frame);
        var pieces = frame.Polygons.Where(p => p.StartHit.Key == 11).ToArray();
        Assert.True(pieces.Length > 32);
        Assert.All(pieces, p => Assert.True(p.Vertices.All(v => v.Y <= baseline + 1e-14) ||
            p.Vertices.All(v => v.Y >= baseline - 1e-14)));
        Assert.Equal(4, frame.Points.Length); Assert.Empty(frame.Gaps);
        Assert.Equal(new HistoryValueDomain(1, 20), source.Query(Spline(includeZero: false)).YDomain);
    }

    [Theory]
    [InlineData(AreaSplineAlgorithm.StrictMinMax)]
    [InlineData(AreaSplineAlgorithm.Cardinal)]
    public void GapsResetBothConnectivityAndTangentSupport(AreaSplineAlgorithm algorithm)
    {
        AreaSample[] left = [new(1, 0, 2), new(2, 1, 5), new(3, 2, 3)];
        AreaSample[] right = [new(5, 4, -30), new(6, 5, 70), new(7, 6, -10)];
        AreaSample[] gaps = [new(4, 3, null), new(8, 7, double.NaN), new(9, null, 10),
            new(10, double.PositiveInfinity, 1), new(11, 8, double.NegativeInfinity)];
        var combined = new AreaChartSource([new("A", "A", left.Concat(gaps.Take(1)).Concat(right).Concat(gaps.Skip(1)), 7)], 13);
        var options = new AreaChartOptions(fixedXDomain: new(0, 8), fixedYDomain: new(-100, 100),
            mode: AreaChartMode.SplineArea, splineAlgorithm: algorithm);
        var frame = combined.Query(options); ReadyFinite(frame);
        Assert.Equal(gaps.Select(g => g.Key), frame.Gaps.Select(g => g.Key));
        Assert.Equal(6, frame.Points.Length);
        Assert.DoesNotContain(frame.Segments, s => s.StartHit.Key == 3 || s.EndHit.Key == 5);
        foreach (var samples in new[] { left, right })
        {
            var isolated = new AreaChartSource([new("A", "A", samples, 7)], 13).Query(options);
            Assert.Equal(isolated.Segments.ToArray(), frame.Segments.Where(s => samples.Any(p => p.Key == s.StartHit.Key)).ToArray());
        }
    }

    [Fact]
    public void IndependentUnalignedSeriesNeverStackOrInventSharedXObservations()
    {
        var first = new AreaSeries("A", "A", [new(1, 0, 2), new(2, 2, 7), new(3, 4, -1)], 5);
        var second = new AreaSeries("B", "B", [new(1, 1, 10), new(2, 3, -20)], 6);
        var options = Spline(AreaSplineAlgorithm.Cardinal, fixedY: new(-30, 30));
        var source = new AreaChartSource([first, second], 7);
        var frame = source.Query(options); ReadyFinite(frame);
        Assert.Equal(5, frame.Points.Length);
        Assert.Equal(new[] { 0d, 2d, 4d }, frame.Points.Where(p => p.SeriesIndex == 0).Select(p => p.Hit.X!.Value));
        Assert.Equal(new[] { 1d, 3d }, frame.Points.Where(p => p.SeriesIndex == 1).Select(p => p.Hit.X!.Value));
        var alone = new AreaChartSource([first], 7).Query(options);
        Assert.Equal(alone.Segments.ToArray(), frame.Segments.Where(s => s.SeriesIndex == 0).ToArray());
        Assert.All(frame.Segments.Where(s => s.SeriesIndex == 1), s =>
        { Assert.Equal(1, s.StartHit.X); Assert.Equal(3, s.EndHit.X); Assert.Equal("B", s.StartHit.SeriesId); });
        Assert.Equal(10, frame.Points.Single(p => p.SeriesIndex == 1 && p.Hit.Key == 1).Hit.Y);
    }

    [Fact]
    public void FixedClippingDoesNotExposeClosureStrokesOrSyntheticHits()
    {
        var source = new AreaChartSource([new("A", "A", [new(1, 0, 10), new(2, 1, 10)])]);
        var frame = source.Query(new(fixedXDomain: new(.2, .8), fixedYDomain: new(3, 4),
            mode: AreaChartMode.SplineArea, splineAlgorithm: AreaSplineAlgorithm.Cardinal));
        ReadyFinite(frame);
        Assert.Empty(frame.Points); Assert.Empty(frame.Segments); Assert.NotEmpty(frame.Polygons);
        Assert.Null(AreaChartGeometry.HitTest(frame, .5, .5, 1));
        Assert.All(frame.Polygons, p => { Assert.Equal(1, p.StartHit.Key); Assert.Equal(2, p.EndHit.Key); });
        var crossing = Example().Query(new(fixedXDomain: new(.25, 2.75), fixedYDomain: new(2, 4), mode: AreaChartMode.SplineArea));
        ReadyFinite(crossing); Assert.NotEmpty(crossing.Segments); Assert.NotEmpty(crossing.Polygons);
    }

    [Fact]
    public void EveryBudgetRefusesTheEntireFrameAndExactLimitsAreAdmitted()
    {
        var source = Example(); var accepted = source.Query(Spline()); ReadyFinite(accepted);
        Assert.Equal(96, accepted.RequiredSegmentCount); Assert.Equal(96, accepted.RequiredPolygonCount);
        Assert.Equal(4 + 4 * AreaChartSource.WorkPerSplinePoint + 3 * AreaChartSource.WorkPerSplinePair, accepted.RequiredWorkCount);
        var work = source.Query(new(workBudget: accepted.RequiredWorkCount - 1, mode: AreaChartMode.SplineArea));
        Assert.Equal(AreaSplineStatus.WorkBudgetExceeded, work.SplineStatus); Empty(work);
        Assert.False(work.GeometryCountsKnown); Assert.Equal(-1, work.RequiredPointCount); Assert.Equal(-1, work.RequiredSegmentCount);
        AreaChartOptions[] insufficient = [
            new(geometryBudget: accepted.RequiredPointCount - 1, mode: AreaChartMode.SplineArea),
            new(polygonBudget: accepted.RequiredPolygonCount - 1, mode: AreaChartMode.SplineArea),
            new(vertexBudget: accepted.RequiredVertexCount - 1, mode: AreaChartMode.SplineArea),
            new(segmentBudget: accepted.RequiredSegmentCount - 1, mode: AreaChartMode.SplineArea),
            new(strokeVertexBudget: accepted.RequiredStrokeVertexCount - 1, mode: AreaChartMode.SplineArea)];
        foreach (var options in insufficient)
        {
            var refused = source.Query(options); Empty(refused);
            Assert.Equal(AreaSplineStatus.GeometryBudgetExceeded, refused.SplineStatus);
            Assert.True(refused.GeometryCountsKnown);
            Assert.Equal(accepted.RequiredPolygonCount, refused.RequiredPolygonCount);
            Assert.Equal(accepted.RequiredVertexCount, refused.RequiredVertexCount);
            Assert.Equal(accepted.RequiredSegmentCount, refused.RequiredSegmentCount);
        }
        ReadyFinite(source.Query(new(geometryBudget: accepted.RequiredPointCount, polygonBudget: accepted.RequiredPolygonCount,
            vertexBudget: accepted.RequiredVertexCount, segmentBudget: accepted.RequiredSegmentCount,
            strokeVertexBudget: accepted.RequiredStrokeVertexCount, workBudget: accepted.RequiredWorkCount, mode: AreaChartMode.SplineArea)));
    }

    [Fact]
    public void WorkRefusalPreservesOriginalGapsAndDoesNotDowngradeToLinear()
    {
        var samples = Enumerable.Range(0, 40).Select(i => new AreaSample(i, i, i == 20 ? null : i + 1d));
        var source = new AreaChartSource([new("A", "A", samples)]);
        var refused = source.Query(Spline());
        Assert.Equal(AreaSplineStatus.WorkBudgetExceeded, refused.SplineStatus); Empty(refused);
        Assert.Equal(20, Assert.Single(refused.Gaps).Key);
        Assert.True(refused.RequiredWorkCount > AreaChartSource.MaximumWork);
        Assert.Equal(ChartBudgetStatus.Satisfied, source.Query().BudgetStatus);
    }

    [Theory]
    [InlineData(AreaSplineAlgorithm.StrictMinMax)]
    [InlineData(AreaSplineAlgorithm.Cardinal)]
    public void EmptySingletonAllGapAndZeroDataHaveNoInventedArea(AreaSplineAlgorithm algorithm)
    {
        var empty = new AreaChartSource([]).Query(Spline(algorithm, includeZero: false)); ReadyFinite(empty); Empty(empty);
        Assert.Null(empty.XDomain); Assert.Null(empty.YDomain);
        var singleton = new AreaChartSource([new("A", "A", [new(1, 3, 2)])]).Query(Spline(algorithm, includeZero: false));
        ReadyFinite(singleton); Assert.Single(singleton.Points); Assert.Empty(singleton.Polygons); Assert.Empty(singleton.Segments);
        Assert.Equal(.5, singleton.Points[0].X); Assert.Equal(.5, singleton.Points[0].Y);
        var gaps = new AreaChartSource([new("A", "A", [new(1, 0, null), new(2, 1, double.NaN)])]).Query(Spline(algorithm));
        ReadyFinite(gaps); Empty(gaps); Assert.Equal(2, gaps.Gaps.Length); Assert.Null(gaps.YDomain);
        var zero = new AreaChartSource([new("A", "A", [new(1, 0, 0), new(2, 1, -0d)])]).Query(Spline(algorithm));
        ReadyFinite(zero); Assert.Equal(2, zero.Points.Length); Assert.Empty(zero.Polygons);
        Assert.All(zero.Segments, s => { Assert.Equal(.5, s.Start.Y); Assert.Equal(.5, s.End.Y); });
    }

    [Fact]
    public void ConstantFixedDomainsRemainFiniteAndHaveNoFillArea()
    {
        var source = new AreaChartSource([new("A", "A", [new(1, 0, 2), new(2, 1, 2)])]);
        var x = source.Query(new(fixedXDomain: new(.5, .5), mode: AreaChartMode.SplineArea));
        ReadyFinite(x); Assert.Empty(x.Polygons); Assert.Empty(x.Points);
        var y = source.Query(Spline(fixedY: new(2, 2)));
        ReadyFinite(y); Assert.Empty(y.Polygons); Assert.Equal(2, y.Points.Length);
        Assert.All(y.Segments, s => { Assert.Equal(.5, s.Start.Y); Assert.Equal(.5, s.End.Y); });
    }

    [Theory]
    [InlineData(AreaSplineAlgorithm.StrictMinMax)]
    [InlineData(AreaSplineAlgorithm.Cardinal)]
    public void OppositeSignExtremeValuesAndTinyCoordinatesDoNotOverflowIntermediateTangents(AreaSplineAlgorithm algorithm)
    {
        var extreme = new AreaChartSource([new("A", "A", [new(1, -double.MaxValue, -double.MaxValue),
            new(2, 0, 0), new(3, double.MaxValue, double.MaxValue)])]);
        var frame = extreme.Query(new(fixedXDomain: new(-1, 1), fixedYDomain: new(-1, 1),
            mode: AreaChartMode.SplineArea, splineAlgorithm: algorithm));
        ReadyFinite(frame); Assert.Single(frame.Points); Assert.NotEmpty(frame.Segments); Assert.NotEmpty(frame.Polygons);
        var tiny = new AreaChartSource([new("A", "A", [new(1, 0, double.Epsilon),
            new(2, double.Epsilon, 2 * double.Epsilon), new(3, 2 * double.Epsilon, double.Epsilon)])]);
        var small = tiny.Query(Spline(algorithm)); ReadyFinite(small); Assert.Equal(3, small.Points.Length);
        var uneven = new AreaChartSource([new("A", "A", [new(1, 0, 1),
            new(2, double.Epsilon, 3), new(3, double.MaxValue, 2)])]).Query(Spline(algorithm));
        ReadyFinite(uneven); Assert.Equal(3, uneven.Points.Length);
    }

    [Fact]
    public void UnrepresentableCardinalControlsRefuseAtomicallyRatherThanClampOvershoot()
    {
        var source = new AreaChartSource([new("A", "A", [new(1, 0, double.MaxValue),
            new(2, 1, double.MaxValue), new(3, 2, 1), new(4, 3, 1), new(5, 4, null)])]);
        var cardinal = source.Query(Spline(AreaSplineAlgorithm.Cardinal));
        Assert.Equal(AreaSplineStatus.NumericOverflow, cardinal.SplineStatus); Empty(cardinal);
        Assert.False(cardinal.GeometryCountsKnown); Assert.Equal(-1, cardinal.RequiredPointCount);
        Assert.Equal(5, Assert.Single(cardinal.Gaps).Key);
        Assert.True(cardinal.RequiredWorkCount <= AreaChartSource.MaximumWork);
        ReadyFinite(source.Query(Spline()));
        Assert.Equal(ChartBudgetStatus.Satisfied, source.Query(new(mode: AreaChartMode.SplineArea,
            splineAlgorithm: AreaSplineAlgorithm.Cardinal, lineTension: 0)).BudgetStatus);
    }

    [Fact]
    public void AutoZeroPolicyWorksForBothSignsAndFixedDomainsWin()
    {
        foreach (var mode in Enum.GetValues<AreaChartMode>())
            foreach (var sign in new[] { -1, 1 })
            {
                var source = new AreaChartSource([new("A", "A", [new(1, 0, sign * 2), new(2, 1, sign * 4)])]);
                var original = source.Query(new(mode: mode));
                var excluded = source.Query(new(mode: mode, includeZeroInAutoRange: false));
                Assert.Equal(sign > 0 ? new HistoryValueDomain(0, 4) : new HistoryValueDomain(-4, 0), original.YDomain);
                Assert.Equal(sign > 0 ? new HistoryValueDomain(2, 4) : new HistoryValueDomain(-4, -2), excluded.YDomain);
                Assert.False(excluded.IsBaselineVisible);
                var fixedDomain = new HistoryValueDomain(-10, 10);
                Assert.Equal(fixedDomain, source.Query(new(mode: mode, fixedYDomain: fixedDomain, includeZeroInAutoRange: false)).YDomain);
            }
    }

    [Fact]
    public void OldSixModesRetainTheirDefaultGeometryAndWorkCharges()
    {
        var source = new AreaChartSource([new("A", "A", [new(1, 0, 2), new(2, 1, 4)])]);
        foreach (var mode in Enum.GetValues<AreaChartMode>().Where(m => m != AreaChartMode.SplineArea))
        {
            var frame = source.Query(new(mode: mode, splineAlgorithm: AreaSplineAlgorithm.Cardinal, lineTension: 1));
            Assert.Equal(ChartBudgetStatus.Satisfied, frame.BudgetStatus); Assert.Null(frame.SplineStatus);
            Assert.Equal(new HistoryValueDomain(0, 4), frame.YDomain); Assert.Equal(2, frame.Points.Length);
            Assert.Equal(new HistoryChartPoint(0, .5), new HistoryChartPoint(frame.Points[0].X, frame.Points[0].Y));
            Assert.Equal(new HistoryChartPoint(1, 0), new HistoryChartPoint(frame.Points[1].X, frame.Points[1].Y));
            var fill = mode is AreaChartMode.LinearArea or AreaChartMode.StepAreaAfter or AreaChartMode.StepAreaBefore;
            Assert.Equal(fill ? 1 : 0, frame.Polygons.Length);
            Assert.Equal(fill ? 0 : mode == AreaChartMode.Line ? 1 : 2, frame.Segments.Length);
            var work = mode == AreaChartMode.LinearArea ? AreaChartSource.WorkPerAdjacentPair
                : mode is AreaChartMode.StepAreaAfter or AreaChartMode.StepAreaBefore ? AreaChartSource.WorkPerStepAreaPair
                : mode == AreaChartMode.Line ? AreaChartSource.WorkPerLinePair : AreaChartSource.WorkPerStepLinePair;
            Assert.Equal(2 + work, frame.RequiredWorkCount);
            if (mode is AreaChartMode.StepLineAfter or AreaChartMode.StepLineBefore)
                Assert.All(frame.Segments, s => Assert.True(s.Start.X == s.End.X || s.Start.Y == s.End.Y));
        }
    }

    private static AreaChartSource Example() => new([new("A", "A", [
        new(101, 0, 1), new(102, 1, 5), new(103, 2, 2), new(104, 3, 4)], 7)], 13);

    private static AreaChartOptions Spline(AreaSplineAlgorithm algorithm = AreaSplineAlgorithm.StrictMinMax,
        double tension = .8, HistoryValueDomain? fixedY = null, bool includeZero = true) => new(mode: AreaChartMode.SplineArea,
            splineAlgorithm: algorithm, lineTension: tension, fixedYDomain: fixedY, includeZeroInAutoRange: includeZero);

    private static double ScreenY(double value, AreaChartSnapshot frame) =>
        DualAxisChartGeometry.NormalizeY(-value, new(-frame.YDomain!.Value.Maximum, -frame.YDomain.Value.Minimum));

    private static void Empty(AreaChartSnapshot frame)
    { Assert.Empty(frame.Points); Assert.Empty(frame.Polygons); Assert.Empty(frame.Segments); }

    private static void ReadyFinite(AreaChartSnapshot frame)
    {
        Assert.Equal(AreaSplineStatus.Ready, frame.SplineStatus);
        Assert.Equal(ChartBudgetStatus.Satisfied, frame.BudgetStatus);
        Assert.True(frame.GeometryCountsKnown);
        Assert.All(frame.Points, p => { Assert.InRange(p.X, 0, 1); Assert.InRange(p.Y, 0, 1); });
        Assert.All(frame.Polygons.SelectMany(p => p.Vertices), p => { Assert.InRange(p.X, 0, 1); Assert.InRange(p.Y, 0, 1); });
        Assert.All(frame.Segments, s =>
        {
            Assert.InRange(s.Start.X, 0, 1); Assert.InRange(s.Start.Y, 0, 1);
            Assert.InRange(s.End.X, 0, 1); Assert.InRange(s.End.Y, 0, 1);
        });
    }

    private static void SameGeometry(AreaChartSnapshot expected, AreaChartSnapshot actual)
    {
        Assert.Equal(expected.XDomain, actual.XDomain); Assert.Equal(expected.YDomain, actual.YDomain);
        Assert.Equal(expected.BudgetStatus, actual.BudgetStatus); Assert.Equal(expected.GeometryCountsKnown, actual.GeometryCountsKnown);
        Assert.Equal(expected.RequiredPointCount, actual.RequiredPointCount); Assert.Equal(expected.RequiredPolygonCount, actual.RequiredPolygonCount);
        Assert.Equal(expected.RequiredVertexCount, actual.RequiredVertexCount); Assert.Equal(expected.RequiredSegmentCount, actual.RequiredSegmentCount);
        Assert.Equal(expected.RequiredStrokeVertexCount, actual.RequiredStrokeVertexCount); Assert.Equal(expected.RequiredWorkCount, actual.RequiredWorkCount);
        Assert.Equal(expected.Points.ToArray(), actual.Points.ToArray()); Assert.Equal(expected.Gaps.ToArray(), actual.Gaps.ToArray());
        Assert.Equal(expected.Segments.ToArray(), actual.Segments.ToArray());
        Assert.Equal(expected.Polygons.Select(p => (p.SeriesIndex, p.StartHit, p.EndHit, p.Vertices.Length)).ToArray(),
            actual.Polygons.Select(p => (p.SeriesIndex, p.StartHit, p.EndHit, p.Vertices.Length)).ToArray());
        Assert.Equal(expected.Polygons.SelectMany(p => p.Vertices).ToArray(), actual.Polygons.SelectMany(p => p.Vertices).ToArray());
    }
}
