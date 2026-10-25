using Olivia.Core.Charts;
using Olivia.Core.Workspaces;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class KeyedPathChartTests
{
    private static readonly AreaChartMode[] Modes = [AreaChartMode.Line, AreaChartMode.StepLineAfter,
        AreaChartMode.StepLineBefore, AreaChartMode.StepAreaAfter, AreaChartMode.StepAreaBefore];
    private static AreaChartSource Source(params AreaSample[] samples) => new([new AreaSeries("s", "Original", samples, 7)], 11);
    private static AreaChartOptions Options(AreaChartMode mode) => new(fixedXDomain: new(0, 10), fixedYDomain: new(-10, 10), mode: mode);
    private static void Point(HistoryChartPoint point, double x, double y)
    { Assert.Equal(x, point.X, 13); Assert.Equal(y, point.Y, 13); }
    private static void Refused(AreaChartSnapshot snapshot)
    {
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, snapshot.BudgetStatus);
        Assert.Empty(snapshot.Points); Assert.Empty(snapshot.Polygons); Assert.Empty(snapshot.Segments);
    }

    [Fact]
    public void Linear_segments_clip_exactly_and_preserve_original_pair_identity()
    {
        var source = Source(new(101, -5, -20), new(102, 15, 20));
        var snapshot = source.Query(Options(AreaChartMode.Line));
        Assert.Empty(snapshot.Points); Assert.Empty(snapshot.Polygons);
        var segment = Assert.Single(snapshot.Segments);
        Point(segment.Start, 0, 1); Point(segment.End, 1, 0);
        Assert.Equal(101, segment.StartHit.Key); Assert.Equal(102, segment.EndHit.Key);
        Assert.Equal("s", segment.StartHit.SeriesId); Assert.Equal(11, segment.StartHit.DatasetVersion);
        Assert.Equal(7, segment.StartHit.SeriesSourceVersion); Assert.Equal(-5, segment.StartHit.X);
        Assert.Equal(1, snapshot.RequiredSegmentCount); Assert.Equal(2, snapshot.RequiredStrokeVertexCount);
        Assert.Equal(35, snapshot.RequiredWorkCount);
        Assert.Null(AreaChartGeometry.HitTest(snapshot, .5, .5, .02));
        var missed = Source(new(1, -5, 20), new(2, 15, 20)).Query(Options(AreaChartMode.Line));
        Assert.Empty(missed.Segments);
        // Only touches the top-left corner: a mathematical point is not a visible stroke.
        var touch = Source(new(1, -1, 9), new(2, 1, 11)).Query(Options(AreaChartMode.Line));
        Assert.Empty(touch.Segments);
    }

    [Fact]
    public void Step_directions_have_literal_horizontal_vertical_geometry_without_diagonals()
    {
        var source = Source(new(31, 2, 6), new(47, 8, -4));
        var after = source.Query(Options(AreaChartMode.StepLineAfter));
        Assert.Equal(2, after.Segments.Length); Assert.Empty(after.Polygons);
        Point(after.Segments[0].Start, .2, .2); Point(after.Segments[0].End, .8, .2);
        Point(after.Segments[1].Start, .8, .2); Point(after.Segments[1].End, .8, .7);
        var before = source.Query(Options(AreaChartMode.StepLineBefore));
        Point(before.Segments[0].Start, .2, .2); Point(before.Segments[0].End, .2, .7);
        Point(before.Segments[1].Start, .2, .7); Point(before.Segments[1].End, .8, .7);
        foreach (var snapshot in new[] { after, before })
        {
            Assert.Equal(67, snapshot.RequiredWorkCount); Assert.Equal(4, snapshot.RequiredStrokeVertexCount);
            Assert.Equal(new long[] { 31, 47 }, snapshot.Points.Select(p => p.Hit.Key));
            Assert.All(snapshot.Segments, p => { Assert.True(p.Start.X == p.End.X || p.Start.Y == p.End.Y); Assert.Equal(31, p.StartHit.Key); Assert.Equal(47, p.EndHit.Key); });
        }
        var same = Source(new(1, 2, 6), new(2, 8, 6)).Query(Options(AreaChartMode.StepLineAfter));
        Assert.Single(same.Segments); // The zero-length vertical leg is not fabricated.
    }

    [Fact]
    public void Step_area_signed_rectangles_and_offscreen_baseline_clip_exactly()
    {
        var source = Source(new(31, 2, 6), new(47, 8, -4));
        var after = source.Query(Options(AreaChartMode.StepAreaAfter));
        var before = source.Query(Options(AreaChartMode.StepAreaBefore));
        var ap = Assert.Single(after.Polygons); var bp = Assert.Single(before.Polygons);
        Assert.Equal(4, ap.Vertices.Length); Assert.Equal(4, bp.Vertices.Length);
        Assert.Equal(new[] { .2, .8 }, ap.Vertices.Select(v => v.X).Distinct().OrderBy(x => x));
        Assert.Equal(new[] { .2, .5 }, ap.Vertices.Select(v => v.Y).Distinct().OrderBy(x => x));
        Assert.Equal(new[] { .5, .7 }, bp.Vertices.Select(v => v.Y).Distinct().OrderBy(x => x));
        Assert.Empty(after.Segments); Assert.Empty(before.Segments);
        Assert.Equal(35, after.RequiredWorkCount); Assert.Equal(0, after.RequiredStrokeVertexCount);
        var clipped = Source(new(1, -5, 8), new(2, 15, -8)).Query(new(fixedXDomain: new(0, 10), fixedYDomain: new(5, 10), mode: AreaChartMode.StepAreaAfter));
        Assert.Empty(clipped.Points); Assert.False(clipped.IsBaselineVisible);
        var cp = Assert.Single(clipped.Polygons);
        Assert.Equal(new[] { 0d, 1d }, cp.Vertices.Select(v => v.X).Distinct().OrderBy(x => x));
        Assert.Equal(new[] { .4, 1d }, cp.Vertices.Select(v => v.Y).Distinct().OrderBy(x => x));
        Assert.Empty(Source(new(1, 1, 0), new(2, 2, 9)).Query(Options(AreaChartMode.StepAreaAfter)).Polygons);
    }

    [Fact]
    public void Gaps_isolated_points_and_constant_axes_do_not_invent_connectivity()
    {
        var source = Source(new(1, 0, 1), new(2, 1, 2), new(3, 2, null), new(4, 3, 3),
            new(5, double.NaN, 4), new(6, 4, 4), new(7, 5, double.PositiveInfinity), new(8, 6, 0));
        foreach (var mode in Modes)
        {
            var snapshot = source.Query(Options(mode)); Assert.Equal(3, snapshot.Gaps.Length); Assert.Equal(5, snapshot.Points.Length);
            Assert.All(snapshot.Segments, segment => { Assert.Equal(1, segment.StartHit.Key); Assert.Equal(2, segment.EndHit.Key); });
            Assert.All(snapshot.Polygons, polygon => { Assert.Equal(1, polygon.StartHit.Key); Assert.Equal(2, polygon.EndHit.Key); });
            var isolated = Source(new AreaSample(1, 5, 0)).Query(new(mode: mode));
            Assert.Single(isolated.Points); Assert.Empty(isolated.Segments); Assert.Empty(isolated.Polygons);
            Assert.Equal(.5, isolated.Points[0].X); Assert.Equal(.5, isolated.Points[0].Y);
        }
        var zero = Source(new(1, 0, 0), new(2, 10, 0)).Query(new(mode: AreaChartMode.Line));
        Point(Assert.Single(zero.Segments).Start, 0, .5); Point(zero.Segments[0].End, 1, .5);
        var vertical = Source(new(1, 0, -5), new(2, 10, 5)).Query(new(fixedXDomain: new(10, 10), fixedYDomain: new(-10, 10), mode: AreaChartMode.StepLineAfter));
        Point(Assert.Single(vertical.Segments).Start, .5, .75); Point(vertical.Segments[0].End, .5, .25);
        Assert.Throws<ArgumentException>(() => Source(new(1, 2, 1), new(2, 3, null), new(3, 1, 2)));
    }

    [Fact]
    public void Original_point_hits_exclude_clipped_endpoints_corners_and_fill_interiors()
    {
        var source = Source(new(31, 2, 6), new(47, 8, -4));
        foreach (var mode in Modes)
        {
            var s = source.Query(Options(mode));
            Assert.Equal(31, AreaChartGeometry.HitTest(s, .2, .2, .001)!.Value.Key);
            Assert.Equal(47, AreaChartGeometry.HitTest(s, .8, .7, .001)!.Value.Key);
            Assert.Null(AreaChartGeometry.HitTest(s, .8, .2, .001));
            Assert.Null(AreaChartGeometry.HitTest(s, .2, .7, .001));
            Assert.Null(AreaChartGeometry.HitTest(s, .5, .35, .001));
        }
        var coincident = new AreaChartSource([new("first", "Same", [new(1, 2, 6)]), new("second", "Same", [new(1, 2, 6)])]);
        Assert.Equal("second", AreaChartGeometry.HitTest(coincident.Query(Options(AreaChartMode.Line)), .2, .2, 0)!.Value.SeriesId);
    }

    [Fact]
    public void Extreme_finite_coordinates_remain_finite_and_exact_boundary_cases_hold()
    {
        var source = Source(new(long.MinValue, -double.MaxValue, -double.MaxValue), new(long.MaxValue, double.MaxValue, double.MaxValue));
        foreach (var mode in Modes)
        {
            var s = source.Query(new(fixedXDomain: new(-1, 1), fixedYDomain: new(-1, 1), mode: mode));
            Assert.Equal(ChartBudgetStatus.Satisfied, s.BudgetStatus); Assert.Empty(s.Points);
            foreach (var point in s.Segments.SelectMany(x => new[] { x.Start, x.End }).Concat(s.Polygons.SelectMany(p => p.Vertices)))
            { Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y)); Assert.InRange(point.X, 0, 1); Assert.InRange(point.Y, 0, 1); }
        }
        var line = Assert.Single(source.Query(new(fixedXDomain: new(-1, 1), fixedYDomain: new(-1, 1), mode: AreaChartMode.Line)).Segments);
        Point(line.Start, 0, 1); Point(line.End, 1, 0);
        var subnormal = Source(new(1, 0, 0), new(2, double.Epsilon, double.Epsilon)).Query(new(mode: AreaChartMode.Line));
        Point(Assert.Single(subnormal.Segments).Start, 0, 1); Point(subnormal.Segments[0].End, 1, 0);
    }

    [Fact]
    public void Every_budget_is_atomic_and_options_validate_known_modes()
    {
        var source = Source(new(1, 2, 6), new(2, 8, -4));
        Refused(source.Query(new(mode: AreaChartMode.Line, geometryBudget: 1)));
        Refused(source.Query(new(mode: AreaChartMode.StepLineAfter, segmentBudget: 1)));
        Refused(source.Query(new(mode: AreaChartMode.StepLineBefore, strokeVertexBudget: 3)));
        Refused(source.Query(new(mode: AreaChartMode.StepAreaAfter, polygonBudget: 0)));
        Refused(source.Query(new(mode: AreaChartMode.StepAreaBefore, vertexBudget: 3)));
        foreach (var mode in Modes)
        {
            var admitted = source.Query(new(mode: mode));
            Refused(source.Query(new(mode: mode, workBudget: admitted.RequiredWorkCount - 1)));
            Assert.Equal(ChartBudgetStatus.Satisfied, source.Query(new(mode: mode, workBudget: admitted.RequiredWorkCount)).BudgetStatus);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaChartOptions(mode: (AreaChartMode)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaChartOptions(segmentBudget: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaChartOptions(strokeVertexBudget: 8193));
        var many = Source(Enumerable.Range(0, 2048).Select(i => new AreaSample(i, i, (i & 1) == 0 ? 1 : -1)).ToArray());
        var refusal = many.Query(new(mode: AreaChartMode.StepLineAfter)); Refused(refusal);
        Assert.Equal(135103, refusal.RequiredWorkCount); Assert.False(refusal.GeometryCountsKnown); Assert.Equal(-1, refusal.RequiredSegmentCount);
        var old = source.Query(); Assert.Empty(old.Segments); Assert.Equal(63, old.RequiredWorkCount); Assert.Equal(0, old.RequiredStrokeVertexCount);
    }

    [Fact]
    public void Host_modes_preserve_keys_publications_replacement_and_invalid_apply_atomicity()
    {
        Assert.Equal(0, (int)MultiSeriesChartKind.Area); Assert.Equal(1, (int)MultiSeriesChartKind.Scatter); Assert.Equal(2, (int)MultiSeriesChartKind.Bar);
        using var session = new MultiSeriesChartSession(); Assert.True(session.Select("B", 2));
        foreach (var kind in new[] { MultiSeriesChartKind.Line, MultiSeriesChartKind.StepLineAfter, MultiSeriesChartKind.StepLineBefore, MultiSeriesChartKind.StepAreaAfter, MultiSeriesChartKind.StepAreaBefore })
        {
            var old = session.Publication;
            Assert.True(session.Apply(kind, ["A", "B", "C"], 0, 7, true, -5, 12)); Assert.NotSame(old, session.Publication);
            Assert.Equal(new MultiSeriesSelection("B", 2), session.Selection); Assert.Equal(5, session.SelectedSample!.Value);
            Assert.False(session.Select("A", 1, old));
            var current = session.Publication;
            Assert.Throws<ArgumentOutOfRangeException>(() => session.Apply(kind, ["A"], 3, 2, true, -5, 12));
            Assert.Same(current, session.Publication); Assert.Equal(new MultiSeriesSelection("B", 2), session.Selection);
        }
        Assert.True(session.ReplaceData(true)); Assert.Equal(MultiSeriesChartKind.StepAreaBefore, session.Kind);
        Assert.Equal(new MultiSeriesSelection("B", 2), session.Selection); Assert.Equal(6, session.SelectedSample!.Value);
        Assert.True(session.Reset()); Assert.Equal(MultiSeriesChartKind.Area, session.Kind);
    }
}
