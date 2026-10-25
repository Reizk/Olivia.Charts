using System.Collections;
using Olivia.Core.Charts;
using Olivia.Core.Workspaces;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class RangeChartTests
{
    private static RangeChartSource Source(params RangeSample[] samples) => new([new RangeSeries("A", "Original", samples)], 7);
    private static RangeChartSource Mixed() => new([
        new RangeSeries("A", "Same label", [new(1, 0, 2, 6), new(2, 2, -3, -1)]),
        new RangeSeries("B", "Same label", [new(1, 0, 4, 8), new(2, 2, -2, 2)])], 7);

    [Fact] public void Immutable_endpoints_identity_order_and_bounded_admission_are_explicit()
    {
        RangeSample[] raw = [new(9, -0d, -0d, 1), new(long.MaxValue, 2, null, 4)];
        var source = Source(raw); raw[0] = new(100, 100, 100, 101);
        Assert.Equal(9, source.Series[0].Samples[0].Key); Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(source.Series[0].Samples[0].Lower!.Value));
        Assert.Throws<ArgumentException>(() => Source(new RangeSample(1, 0, 4, 3)));
        Assert.Throws<ArgumentException>(() => Source(new(1, 0, 1, 2), new(1, 1, 2, 3)));
        Assert.Throws<ArgumentException>(() => Source(new(1, 0, null, 2), new(2, 0, 1, 2)));
        Assert.Throws<ArgumentException>(() => Source(new RangeSample(1, double.NaN, 1, 2)));
        Assert.Throws<ArgumentException>(() => new RangeChartSource([new RangeSeries("A", "a", []), new RangeSeries("A", "b", [])]));
        Assert.Throws<ArgumentException>(() => Source(Enumerable.Range(0, 257).Select(i => new RangeSample(i, i, 0, 1)).ToArray()));
        Assert.Throws<ArgumentException>(() => new RangeChartSource(Enumerable.Range(0, 9).Select(i => new RangeSeries("s" + i, "series", []))));
        var sequence = new CountingSamples(); Assert.Throws<ArgumentException>(() => new RangeSeries("A", "a", sequence)); Assert.Equal(2049, sequence.Moves);
        var copiedIds = new[] { "A" }; var options = new RangeChartOptions(visibleSeriesIds: copiedIds); copiedIds[0] = "foreign";
        Assert.Equal("A", options.VisibleSeriesIds[0]); Assert.Throws<ArgumentException>(() => source.Query(new(visibleSeriesIds: ["foreign"])));
        Assert.Throws<ArgumentException>(() => new RangeChartOptions(visibleSeriesIds: ["A", "A"]));
    }
    [Fact] public void Grouped_and_overlap_bars_use_literal_two_endpoint_rectangles()
    {
        var source = Mixed(); var grouped = source.Query(); Assert.Equal(RangeChartStatus.Ready, grouped.Status);
        Assert.Equal(new HistoryValueDomain(-3, 8), grouped.YDomain); Assert.Equal(4, grouped.Bars.Length);
        var a = grouped.Bars[0]; Assert.Equal(.05, a.Left, 12); Assert.Equal(.2, a.Width, 12); Assert.Equal(2d / 11, a.Top, 12); Assert.Equal(4d / 11, a.Height, 12);
        var b = grouped.Bars[2]; Assert.Equal(.25, b.Left, 12); Assert.Equal(0, b.Top); Assert.Equal(4d / 11, b.Height, 12);
        Assert.Same(source, a.Hit.Source); Assert.Equal(2, a.Hit.Lower); Assert.Equal(6, a.Hit.Upper);
        var overlap = source.Query(new(RangeChartMode.OverlapBars)); Assert.Equal(.05, overlap.Bars[0].Left, 12); Assert.Equal(.4, overlap.Bars[0].Width, 12);
        Assert.Equal(overlap.Bars[0].Left, overlap.Bars[2].Left);
        var hit = Assert.IsType<RangeChartHit>(RangeChartGeometry.HitTest(overlap, .2, .25, 500, 300)); Assert.Equal("B", hit.SeriesId); Assert.Equal(1, hit.Key);
    }
    [Fact] public void Missing_endpoint_and_nonfinite_values_break_bands_without_partial_pairs()
    {
        var source = Source(new(1, 0, 1, 2), new(2, 1, null, 999), new(3, 2, 3, 4), new(4, 3, double.NaN, 1000), new(5, 4, -1000, double.PositiveInfinity), new(6, 5, 5, 6));
        var s = source.Query(new(RangeChartMode.LinearArea)); Assert.Equal(new HistoryValueDomain(1, 6), s.YDomain);
        Assert.Equal(3, s.Gaps.Length); Assert.Equal(3, s.Intervals.Length); Assert.Empty(s.Polygons); Assert.Empty(s.Segments);
        Assert.Equal(999, s.Gaps[0].Upper); Assert.True(double.IsNaN(s.Gaps[1].Lower!.Value)); Assert.Equal(-1000, s.Gaps[2].Lower);
    }
    [Fact] public void Linear_range_bands_keep_numeric_spacing_and_endpoint_provenance()
    {
        var source = Source(new(1, 0, 1, 4), new(2, 2, 3, 6), new(3, 5, -2, 1));
        var s = source.Query(new(RangeChartMode.LinearArea)); Assert.Equal(2, s.Polygons.Length); Assert.Equal(4, s.Segments.Length);
        Assert.Equal(.4, s.Intervals[1].X, 12); Assert.Equal(1, s.Intervals[2].X);
        var first = s.Polygons[0]; Assert.Equal(1, first.StartHit.Key); Assert.Equal(2, first.EndHit.Key);
        Assert.Contains(new HistoryChartPoint(0, .625), first.Vertices); Assert.Contains(new HistoryChartPoint(0, .25), first.Vertices);
        Assert.Contains(new HistoryChartPoint(.4, 0), first.Vertices); Assert.Contains(new HistoryChartPoint(.4, .375), first.Vertices);
        Assert.Null(RangeChartGeometry.HitTest(s, .2, .3, 1000, 500));
    }
    [Fact] public void Degenerate_intervals_triangles_and_boundary_contacts_remain_distinct()
    {
        var triangle = Source(new(1, 0, 2, 2), new(2, 1, 1, 4)).Query(new(RangeChartMode.LinearArea));
        Assert.Single(triangle.Polygons); Assert.Equal(3, triangle.Polygons[0].Vertices.Length); Assert.Equal(2, triangle.Intervals.Length);
        var line = Source(new(1, 0, 1, 1), new(2, 1, 2, 2)).Query(new(RangeChartMode.LinearArea));
        Assert.Empty(line.Polygons); Assert.Equal(2, line.Segments.Length); Assert.Equal(2, line.Intervals.Length);
        var source = Source(new RangeSample(1, 0, 2, 6));
        var bar = source.Query(new(fixedXDomain: new(0, 1), fixedYDomain: new(6, 8))); Assert.Empty(bar.Bars); Assert.Single(bar.Intervals);
        Assert.Null(RangeChartGeometry.HitTest(bar, .5, 1, 500, 300));
        var area = source.Query(new(RangeChartMode.LinearArea, new(0, 1), new(6, 8)));
        Assert.Equal(1, RangeChartGeometry.HitTest(area, 0, 1, 500, 300)!.Value.Key);
        var zero = Source(new RangeSample(2, 0, 0, 0)).Query(new(RangeChartMode.LinearArea)); Assert.Equal(new HistoryValueDomain(0, 0), zero.YDomain); Assert.Equal(.5, zero.Intervals[0].Top);
    }
    [Fact] public void Fixed_windows_clip_external_neighbors_and_extreme_endpoints_exactly()
    {
        var source = Source(new(1, -5, 1, 9), new(2, 15, 1, 9));
        var s = source.Query(new(RangeChartMode.LinearArea, new(0, 10), new(3, 7))); Assert.Empty(s.Intervals); Assert.Single(s.Polygons);
        Assert.Equal(4, s.Polygons[0].Vertices.Length); Assert.Contains(new HistoryChartPoint(0, 0), s.Polygons[0].Vertices); Assert.Contains(new HistoryChartPoint(1, 1), s.Polygons[0].Vertices);
        Assert.Null(RangeChartGeometry.HitTest(s, .5, .5, 500, 300));
        var huge = Source(new(1, -double.MaxValue, -double.MaxValue, double.MaxValue), new(2, double.MaxValue, -double.MaxValue, double.MaxValue));
        var tiny = huge.Query(new(RangeChartMode.LinearArea, new(-double.Epsilon, double.Epsilon), new(double.Epsilon, 2 * double.Epsilon)));
        Assert.Single(tiny.Polygons); Assert.Equal(4, tiny.Polygons[0].Vertices.Length);
        foreach (var corner in new[] { new HistoryChartPoint(0, 0), new HistoryChartPoint(0, 1), new HistoryChartPoint(1, 0), new HistoryChartPoint(1, 1) }) Assert.Contains(corner, tiny.Polygons[0].Vertices);
        var bars = Mixed().Query(new(fixedXDomain: new(0, 0), fixedYDomain: new(-3, 8))); Assert.Single(bars.Categories); Assert.Equal(2, bars.Bars.Length); Assert.Equal(.1, bars.Bars[0].Left, 12);
    }
    [Fact] public void Each_geometry_and_work_budget_refuses_whole_candidate_at_boundaries()
    {
        var source = Source(new(1, 0, 1, 3), new(2, 1, 2, 4)); var ready = source.Query(new(RangeChartMode.LinearArea));
        Assert.Equal(360, source.RequiredWork); Assert.Equal(2, ready.RequiredIntervals); Assert.Equal(1, ready.RequiredPolygons); Assert.Equal(4, ready.RequiredVertices); Assert.Equal(2, ready.RequiredSegments);
        var exact = source.Query(new(RangeChartMode.LinearArea, intervalBudget: 2, polygonBudget: 1, vertexBudget: 4, segmentBudget: 2, strokeVertexBudget: 4, workBudget: 360)); Assert.Equal(RangeChartStatus.Ready, exact.Status);
        RangeChartOptions[] refused = [new(RangeChartMode.LinearArea, intervalBudget: 1),new(RangeChartMode.LinearArea, polygonBudget: 0),new(RangeChartMode.LinearArea, vertexBudget: 3),new(RangeChartMode.LinearArea, segmentBudget: 1),new(RangeChartMode.LinearArea, strokeVertexBudget: 3),new(RangeChartMode.LinearArea, workBudget: 359)];
        foreach (var options in refused) { var s = source.Query(options); Assert.NotEqual(RangeChartStatus.Ready, s.Status); Assert.Empty(s.Bars); Assert.Empty(s.Intervals); Assert.Empty(s.Polygons); Assert.Empty(s.Segments); Assert.Empty(s.Gaps); }
        Assert.False(source.Query(new(workBudget: 0)).GeometryCountsKnown);
        Assert.Equal(RangeChartStatus.GeometryBudgetExceeded, source.Query(new(barBudget: 1)).Status);
        var maximum = new RangeChartSource(Enumerable.Range(0, 8).Select(s => new RangeSeries("s" + s, "series", Enumerable.Range(0, 256).Select(i => new RangeSample(i, i, 1, 2)))));
        Assert.Equal(331840, maximum.RequiredWork); Assert.Equal(2048, maximum.Query().Bars.Length);
    }
    [Fact] public void Visible_series_recompute_y_without_shifting_categories_or_slots()
    {
        var source = Mixed(); var all = source.Query(); var onlyB = source.Query(new(visibleSeriesIds: ["B"]));
        Assert.Equal(all.Categories.ToArray(), onlyB.Categories.ToArray()); Assert.Equal(new HistoryValueDomain(-2, 8), onlyB.YDomain);
        Assert.Equal(all.Bars[2].Left, onlyB.Bars[0].Left); Assert.Equal(all.Bars[2].Width, onlyB.Bars[0].Width);
        var none = source.Query(new(visibleSeriesIds: [])); Assert.Null(none.YDomain); Assert.Empty(none.Bars); Assert.Empty(none.Intervals);
        var tie = new RangeChartSource([new RangeSeries("A", "a", [new(1, 0, 2, 6)]), new RangeSeries("B", "b", [new(1, 0, 2, 6)])]);
        Assert.Equal("A", RangeChartGeometry.HitTest(tie.Query(new(RangeChartMode.LinearArea)), .5, .5, 500, 300)!.Value.SeriesId);
        Assert.Equal("B", RangeChartGeometry.HitTest(tie.Query(new(RangeChartMode.OverlapBars)), .5, .5, 500, 300)!.Value.SeriesId);
    }
    [Fact] public void Host_candidate_failures_and_same_version_replacement_are_atomic()
    {
        using var session = new RangeChartSession(Mixed()); var old = session.Publication;
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Apply(old, RangeChartMode.LinearArea, ["A"], 0, 2, false, 4, 3)); Assert.Same(old, session.Publication);
        Assert.False(session.Apply(new(old.Revision, old.Snapshot), RangeChartMode.LinearArea, ["A"], 0, 2, true, -4, 9));
        RangeChartPublication? inner = null;
        IEnumerable<string> ReenterReset() { session.Reset(session.Publication); inner = session.Publication; yield return "A"; }
        Assert.False(session.Apply(old, RangeChartMode.LinearArea, ReenterReset(), 0, 2, true, -4, 9)); Assert.Same(inner, session.Publication); Assert.Equal(RangeChartMode.GroupedBars, session.Mode);
        var beforeReplace = session.Publication; var replacement = new RangeChartSource([new RangeSeries("P", "new", [new(99, 0, 4, 6)])], 7);
        Assert.True(session.ReplaceSource(beforeReplace, replacement)); Assert.Same(replacement, session.Publication.Snapshot.Source); Assert.False(session.Select(beforeReplace, "A", 1));
        using var constrained = new RangeChartSession(Source(new RangeSample(1, 0, 1, 2)), 184); var admitted = constrained.Publication;
        Assert.Throws<ArgumentException>(() => constrained.ReplaceSource(admitted, Mixed())); Assert.Same(admitted, constrained.Publication);
        var closing = new RangeChartSession(Mixed()); var beforeClose = closing.Publication;
        IEnumerable<string> DisposeDuringEnumeration() { try { yield return "A"; } finally { closing.Dispose(); } }
        Assert.False(closing.Apply(beforeClose, RangeChartMode.LinearArea, DisposeDuringEnumeration(), 0, 2, true, -4, 9)); Assert.True(closing.IsClosed); Assert.Same(beforeClose, closing.Publication);
    }
    [Fact] public void Selection_distinguishes_raw_interval_visible_clip_and_synthetic_band()
    {
        using var session = new RangeChartSession(Mixed()); Assert.True(session.Select(session.Publication, "A", 1));
        Assert.True(session.Apply(session.Publication, RangeChartMode.LinearArea, ["A", "B"], 0, 2, false, 3, 5));
        Assert.True(session.IsSelectionVisible); var selected = Assert.IsType<RangeChartHit>(session.SelectedHit); Assert.Equal(2, selected.Lower); Assert.Equal(6, selected.Upper);
        var interval = session.Publication.Snapshot.Intervals.Single(x => x.Hit.SeriesId == "A" && x.Hit.Key == 1); Assert.Equal(0, interval.Top); Assert.Equal(1, interval.Bottom);
        Assert.True(session.Apply(session.Publication, RangeChartMode.LinearArea, ["B"], 0, 2, false, 3, 5)); Assert.False(session.IsSelectionVisible); Assert.Equal(new RangeChartSelection("A", 1), session.Selection);
        var source = Source(new(1, 0, 2, 6), new(2, 2, 3, 7)); var s = source.Query(new(RangeChartMode.LinearArea));
        Assert.Null(RangeChartGeometry.HitTest(s, .5, .5, 1000, 500)); Assert.Equal(1, RangeChartGeometry.HitTest(s, .005, .5, 1000, 500)!.Value.Key);
        Assert.Null(RangeChartGeometry.HitTest(s, .007, .5, 1000, 500));
    }
    private sealed class CountingSamples : IEnumerable<RangeSample>
    {
        public int Moves { get; private set; }
        public IEnumerator<RangeSample> GetEnumerator() { while (true) { var i = Moves++; yield return new(i, i, 0, 1); } }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
