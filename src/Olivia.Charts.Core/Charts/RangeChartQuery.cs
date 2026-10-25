using System.Collections.Immutable;

namespace Olivia.Core.Charts;

public sealed partial class RangeChartSource
{
    public RangeChartSnapshot Query(RangeChartOptions? options = null)
    {
        options ??= RangeChartOptions.Default;
        // Identity checks are bounded by 8x8 comparisons and allocate no lookup table.
        foreach (var id in options.VisibleSeriesIds)
        {
            var found = false;
            foreach (var series in Series) if (StringComparer.Ordinal.Equals(id, series.Id)) { found = true; break; }
            if (!found) throw new ArgumentException("Unknown visible range series identity.", nameof(options));
        }
        if (RequiredWork > options.WorkBudget)
            return new(this, options, null, null, [], [], [], [], [], [],
                RangeChartStatus.WorkBudgetExceeded, false, -1, -1, -1, -1, -1);
        // All variable query builders and exact geometry work follow whole-query admission.
        var categories = Coordinates.Where(x => !options.FixedXDomain.HasValue ||
            (x >= options.FixedXDomain.Value.Minimum && x <= options.FixedXDomain.Value.Maximum)).ToImmutableArray();
        var categoryIndices = new Dictionary<double, int>();
        for (var i = 0; i < categories.Length; i++) categoryIndices.Add(categories[i], i);
        HistoryValueDomain? xd = Coordinates.IsEmpty ? null : options.FixedXDomain ??
            new HistoryValueDomain(Coordinates[0], Coordinates[^1]);
        double? minimum = null, maximum = null;
        for (var s = 0; s < Series.Length; s++)
        {
            if (!Visible(Series[s].Id)) continue;
            foreach (var sample in Series[s].Samples)
            {
                if (sample.IsGap) continue;
                var lower = sample.Lower!.Value; var upper = sample.Upper!.Value;
                minimum = minimum.HasValue ? Math.Min(minimum.Value, lower) : lower;
                maximum = maximum.HasValue ? Math.Max(maximum.Value, upper) : upper;
            }
        }
        HistoryValueDomain? yd = options.FixedYDomain ??
            (minimum.HasValue ? new HistoryValueDomain(minimum.Value, maximum!.Value) : null);
        var intervals = ImmutableArray.CreateBuilder<RangeChartInterval>();
        var bars = ImmutableArray.CreateBuilder<RangeChartBar>();
        var polygons = ImmutableArray.CreateBuilder<RangeChartPolygon>();
        var segments = ImmutableArray.CreateBuilder<RangeChartSegment>();
        var gaps = ImmutableArray.CreateBuilder<RangeChartHit>();
        var vertices = 0;
        for (var s = 0; s < Series.Length; s++)
        {
            var series = Series[s];
            if (!Visible(series.Id)) continue;
            RangeChartHit? previous = null;
            foreach (var sample in series.Samples)
            {
                var hit = new RangeChartHit(this, series.Id, series.SourceVersion, sample);
                if (sample.IsGap) { gaps.Add(hit); previous = null; continue; }
                if (xd.HasValue && yd.HasValue)
                {
                    var x = sample.X; var low = sample.Lower!.Value; var high = sample.Upper!.Value;
                    var clippedLow = Math.Max(low, yd.Value.Minimum);
                    var clippedHigh = Math.Min(high, yd.Value.Maximum);
                    if (x >= xd.Value.Minimum && x <= xd.Value.Maximum && clippedLow <= clippedHigh)
                    {
                        var top = AreaChartGeometry.NormalizeScreenY(clippedHigh, yd.Value);
                        var bottom = AreaChartGeometry.NormalizeScreenY(clippedLow, yd.Value);
                        if (options.Mode == RangeChartMode.LinearArea)
                            intervals.Add(new(hit, s, DualAxisChartGeometry.NormalizeY(x, xd.Value), top, bottom));
                        else
                        {
                            if (categoryIndices.TryGetValue(x, out var index))
                            {
                                var categoryWidth = 1d / categories.Length;
                                var groupLeft = (index + .1) * categoryWidth;
                                var groupWidth = .8 * categoryWidth;
                                var width = options.Mode == RangeChartMode.GroupedBars ? groupWidth / Series.Length : groupWidth;
                                var left = options.Mode == RangeChartMode.GroupedBars ? groupLeft + s * width : groupLeft;
                                intervals.Add(new(hit, s, left + width / 2, top, bottom));
                                // Boundary-only contact and original zero-thickness have no filled bar.
                                if (clippedLow < clippedHigh && bottom > top)
                                    bars.Add(new(hit, s, index, left, top, width, bottom - top));
                            }
                        }
                    }
                    if (options.Mode == RangeChartMode.LinearArea && previous is { } first)
                    {
                        if (!xd.Value.IsConstant && !yd.Value.IsConstant)
                        {
                            var polygon = AreaChartGeometry.CreateRangePolygon(first, hit, s, xd.Value, yd.Value);
                            if (polygon is { } shape) { vertices = checked(vertices + shape.Vertices.Length); polygons.Add(shape); }
                        }
                        var lower = AreaChartGeometry.CreateRangeBoundary(first, hit, s, false, xd.Value, yd.Value);
                        if (lower is { } lowerLine) segments.Add(lowerLine);
                        var upper = AreaChartGeometry.CreateRangeBoundary(first, hit, s, true, xd.Value, yd.Value);
                        if (upper is { } upperLine) segments.Add(upperLine);
                    }
                }
                previous = hit;
            }
        }
        var admitted = intervals.Count <= options.IntervalBudget && bars.Count <= options.BarBudget &&
            polygons.Count <= options.PolygonBudget && vertices <= options.VertexBudget &&
            segments.Count <= options.SegmentBudget && checked(2 * segments.Count) <= options.StrokeVertexBudget;
        return new(this, options, xd, yd, categories,
            admitted ? intervals.ToImmutable() : [], admitted ? bars.ToImmutable() : [],
            admitted ? polygons.ToImmutable() : [], admitted ? segments.ToImmutable() : [],
            admitted ? gaps.ToImmutable() : [], admitted ? RangeChartStatus.Ready : RangeChartStatus.GeometryBudgetExceeded,
            true, intervals.Count, bars.Count, polygons.Count, vertices, segments.Count);

        bool Visible(string id) => options.AllSeries || options.VisibleSeriesIds.Contains(id, StringComparer.Ordinal);
    }
}
