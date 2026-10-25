using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>
/// Olivia's independent, bounded cubic policy, not a reconstruction of proprietary tangents.
/// Cumulative knots are interpolated, rather than inventing raw observations. Shared prefix
/// boundaries use identical controls and subdivision coordinates in every adjacent band.
/// </summary>
internal static class StackedAreaSplineGeometry
{
    // Fixed tessellation is intentional: no recursive/adaptive unbounded work, and no silent
    // linear fallback. These are conservative admission units, not machine-operation counts.
    // Per slice: two convex clips if a Cardinal band crosses (<=2*32), two real boundary clips
    // (<=2*32), cubic evaluation, exact crossing setup and projection. 192 covers that work.
    internal const int Subdivisions = 32;
    internal const int WorkPerPair = Subdivisions * 192;
    // Two sign groups, cumulative knots, centered tangents, common strict-order limiting,
    // control construction, support aliases and hull-domain scans are bounded by this charge.
    internal const int WorkPerAlignedEntry = 256;

    private readonly record struct Curve(double A, double B, double C, double D)
    {
        public double At(double t) => t == 0 ? A : t == 1 ? D :
            Blend(Blend(Blend(A, B, t), Blend(B, C, t), t),
                Blend(Blend(B, C, t), Blend(C, D, t), t), t);
        public bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D);
        public double Minimum => Math.Min(Math.Min(A, B), Math.Min(C, D));
        public double Maximum => Math.Max(Math.Max(A, B), Math.Max(C, D));
    }

    internal static StackedAreaChartSnapshot Query(StackedAreaChartSource source, StackedAreaChartOptions options,
        StackedAreaChartContribution?[,] values, ImmutableArray<bool> negativeSeries,
        HistoryValueDomain? xd, double minimum, double maximum, bool hasValues)
    {
        var count = source.Coordinates.Length;
        var seriesCount = source.Series.Length;
        var knots = new double[2, seriesCount + 1, count];
        for (var c = 0; c < count; c++)
            for (var s = 0; s < seriesCount; s++)
                for (var sign = 0; sign < 2; sign++)
                    knots[sign, s + 1, c] = values[s, c] is { } value && negativeSeries[s] == (sign == 1)
                        ? Math.Abs(value.StackTop) : knots[sign, s, c];

        // Tangent offsets are in Y units, never dy/dx: finite extreme or subnormal X spans
        // therefore cannot overflow a slope. Reflected ratios retain tiny right-hand spans.
        var incoming = new double[2, seriesCount + 1, count];
        var outgoing = new double[2, seriesCount + 1, count];
        for (var sign = 0; sign < 2; sign++)
            for (var b = 0; b <= seriesCount; b++)
                for (var c = 0; c < count && count > 1; c++)
                {
                    var left = c == 0 ? 0 : c - 1;
                    var right = c == count - 1 ? count - 1 : c + 1;
                    var offset = (knots[sign, b, right] - knots[sign, b, left]) * (options.LineTension / 3);
                    if (c == 0) outgoing[sign, b, c] = offset;
                    else if (c == count - 1) incoming[sign, b, c] = offset;
                    else
                    {
                        var x0 = source.Coordinates[left]; var x = source.Coordinates[c]; var x1 = source.Coordinates[right];
                        incoming[sign, b, c] = offset * DualAxisChartGeometry.NormalizeY(x, new(x0, x1));
                        outgoing[sign, b, c] = offset * DualAxisChartGeometry.NormalizeY(-x, new(-x1, -x0));
                        if (options.SplineAlgorithm == StackedAreaSplineAlgorithm.StrictMinMax)
                        {
                            var dl = knots[sign, b, c] - knots[sign, b, left];
                            var dr = knots[sign, b, right] - knots[sign, b, c];
                            var factor = dl == 0 || dr == 0 || Math.Sign(dl) != Math.Sign(dr) ? 0 : 1d;
                            factor = Limit(factor, Math.Abs(dl) / 3, Math.Abs(incoming[sign, b, c]));
                            factor = Limit(factor, Math.Abs(dr) / 3, Math.Abs(outgoing[sign, b, c]));
                            incoming[sign, b, c] *= factor; outgoing[sign, b, c] *= factor;
                        }
                    }
                }

        if (options.SplineAlgorithm == StackedAreaSplineAlgorithm.StrictMinMax)
            for (var sign = 0; sign < 2; sign++)
                for (var c = 0; c < count; c++)
                {
                    // A common knot factor preserves tangent directions while making the two
                    // control columns ordered across ALL cumulative boundaries. This additional
                    // band-order guarantee is Olivia policy, not a documented vendor guarantee.
                    var factor = 1d;
                    for (var b = 1; b <= seriesCount; b++)
                    {
                        var gap = knots[sign, b, c] - knots[sign, b - 1, c];
                        factor = Limit(factor, gap, incoming[sign, b, c] - incoming[sign, b - 1, c]);
                        factor = Limit(factor, gap, outgoing[sign, b - 1, c] - outgoing[sign, b, c]);
                    }
                    for (var b = 0; b <= seriesCount; b++)
                    { incoming[sign, b, c] *= factor; outgoing[sign, b, c] *= factor; }
                }

        var curves = new Curve[2, seriesCount + 1, Math.Max(0, count - 1)];
        for (var sign = 0; sign < 2; sign++)
            for (var c = 0; c + 1 < count; c++)
                for (var b = 0; b <= seriesCount; b++)
                {
                    var a = knots[sign, b, c]; var d = knots[sign, b, c + 1];
                    var first = a + outgoing[sign, b, c]; var second = d - incoming[sign, b, c + 1];
                    if (options.SplineAlgorithm == StackedAreaSplineAlgorithm.StrictMinMax)
                    {
                        first = Math.Clamp(first, Math.Min(a, d), Math.Max(a, d));
                        second = Math.Clamp(second, Math.Min(a, d), Math.Max(a, d));
                        // Guard binary64 rounding of a mathematically ordered control column.
                        if (b > 0)
                        { first = Math.Max(first, curves[sign, b - 1, c].B); second = Math.Max(second, curves[sign, b - 1, c].C); }
                    }
                    var curve = new Curve(a, first, second, d);
                    // A missing contribution is a boundary alias ONLY, never a source sample.
                    // This also shares a boundary across hidden or opposite-sign series.
                    if (b > 0 && (negativeSeries[b - 1] != (sign == 1) ||
                        (values[b - 1, c] is null && values[b - 1, c + 1] is null)))
                        curve = curves[sign, b - 1, c];
                    if (!curve.IsFinite) return Refused();
                    curves[sign, b, c] = curve;
                }

        if (options.Mode == StackedAreaChartMode.Normalized100)
            for (var c = 0; c + 1 < count; c++)
            {
                // The cumulative outer boundary stays exactly 100% at positive-total intervals.
                // A newborn with no pair is still NOT drawn: the last drawable band can fall
                // below 100% before its birth, as in the original linear support contract.
                // Zero totals remain genuine zeros; no phantom pre-birth area is inserted.
                var outer = seriesCount;
                while (outer > 0 && values[outer - 1, c] is null && values[outer - 1, c + 1] is null) outer--;
                if (knots[0, outer, c] == 1 && knots[0, outer, c + 1] == 1)
                    for (var b = outer; b <= seriesCount; b++) curves[0, b, c] = new(1, 1, 1, 1);
            }

        // The convex hull is deliberately conservative, not an extrema-root approximation.
        // Include only boundaries of actual source pairs. Cardinal overshoot is never cropped
        // by an automatic domain, including internal normalized boundaries outside [0,1].
        for (var s = 0; s < seriesCount; s++)
            for (var c = 0; c + 1 < count; c++)
                if (values[s, c].HasValue && values[s, c + 1].HasValue)
                    for (var b = s; b <= s + 1; b++)
                    {
                        var curve = curves[negativeSeries[s] ? 1 : 0, b, c];
                        minimum = Math.Min(minimum, negativeSeries[s] ? -curve.Maximum : curve.Minimum);
                        maximum = Math.Max(maximum, negativeSeries[s] ? -curve.Minimum : curve.Maximum);
                    }
        if (options.Mode == StackedAreaChartMode.Normalized100 && hasValues) maximum = Math.Max(1, maximum);
        HistoryValueDomain? yd = options.FixedYDomain ?? (hasValues ? new HistoryValueDomain(minimum, maximum) : null);
        var contributions = ImmutableArray.CreateBuilder<StackedAreaChartContribution>();
        var points = ImmutableArray.CreateBuilder<StackedAreaChartPoint>();
        var polygons = ImmutableArray.CreateBuilder<StackedAreaChartPolygon>();
        var segments = ImmutableArray.CreateBuilder<StackedAreaChartSegment>();
        var vertices = 0;
        for (var s = 0; s < seriesCount; s++)
            for (var c = 0; c < count; c++)
            {
                if (values[s, c] is not { } value) continue;
                contributions.Add(value);
                if (xd is not { } xDomain || yd is not { } yDomain) continue;
                var low = Math.Max(Math.Min(value.StackBase, value.StackTop), yDomain.Minimum);
                var high = Math.Min(Math.Max(value.StackBase, value.StackTop), yDomain.Maximum);
                var x = value.Hit.X!.Value;
                if (x >= xDomain.Minimum && x <= xDomain.Maximum && low <= high)
                    points.Add(new(value.Hit, s, DualAxisChartGeometry.NormalizeY(x, xDomain),
                        AreaChartGeometry.NormalizeScreenY(high, yDomain), AreaChartGeometry.NormalizeScreenY(low, yDomain),
                        value.StackBase, value.StackTop, value.Contribution, value.Proportion));
                if (c + 1 >= count || values[s, c + 1] is not { } next) continue;
                var sign = negativeSeries[s] ? 1 : 0;
                var baseline = curves[sign, s, c]; var top = curves[sign, s + 1, c];
                var nextX = next.Hit.X!.Value;
                var xCurve = new Curve(x, Blend(x, nextX, options.LineTension / 3),
                    Blend(x, nextX, 1 - options.LineTension / 3), nextX);
                var multiplier = negativeSeries[s] ? -1 : 1;
                var previousX = x; var previousBase = value.StackBase; var previousTop = value.StackTop;
                for (var k = 1; k <= Subdivisions; k++)
                {
                    var t = (double)k / Subdivisions;
                    var currentX = xCurve.At(t);
                    var currentBase = k == Subdivisions ? next.StackBase : multiplier * baseline.At(t);
                    var currentTop = k == Subdivisions ? next.StackTop : multiplier * top.At(t);
                    if (!xDomain.IsConstant && !yDomain.IsConstant)
                        foreach (var polygon in AreaChartGeometry.CreateStackedSplineBand(previousX, previousBase, previousTop,
                            currentX, currentBase, currentTop, xDomain, yDomain))
                        { vertices = checked(vertices + polygon.Length); polygons.Add(new(s, value.Hit, next.Hit, polygon)); }
                    AddBoundary(previousBase, currentBase, negativeSeries[s]);
                    AddBoundary(previousTop, currentTop, !negativeSeries[s]);
                    previousX = currentX; previousBase = currentBase; previousTop = currentTop;

                    void AddBoundary(double a, double b, bool upper)
                    {
                        if (AreaChartGeometry.CreateStackedSplineBoundary(previousX, a, currentX, b, xDomain, yDomain) is { } segment)
                            segments.Add(new(s, value.Hit, next.Hit, upper, segment.Start, segment.End));
                    }
                }
            }
        var accepted = points.Count <= options.PointBudget && polygons.Count <= options.PolygonBudget &&
            vertices <= options.VertexBudget && segments.Count <= options.SegmentBudget &&
            checked(2 * segments.Count) <= options.StrokeVertexBudget;
        return new(source, options, xd, yd, accepted ? contributions.ToImmutable() : [], accepted ? points.ToImmutable() : [],
            accepted ? polygons.ToImmutable() : [], accepted ? segments.ToImmutable() : [],
            accepted ? StackedAreaChartStatus.Ready : StackedAreaChartStatus.GeometryBudgetExceeded,
            true, points.Count, polygons.Count, vertices, segments.Count);

        StackedAreaChartSnapshot Refused() => new(source, options, xd, null, [], [], [], [],
            StackedAreaChartStatus.NumericOverflow, false, -1, -1, -1, -1);
    }

    private static double Limit(double factor, double allowance, double change) =>
        change > allowance ? Math.Min(factor, allowance / change) : factor;

    private static double Blend(double a, double b, double t)
    {
        if (t == 0 || a == b) return a;
        if (t == 1) return b;
        // Same-sign differences cannot overflow; opposite-sign weighted terms cannot overflow.
        // The clamp corrects only roundoff of a mathematically convex combination.
        return Math.Clamp(Math.Sign(a) == Math.Sign(b) ? a + (b - a) * t : a * (1 - t) + b * t,
            Math.Min(a, b), Math.Max(a, b));
    }
}

public static partial class AreaChartGeometry
{
    // Clip actual tessellated chords, not a control hull. No viewport/fill closure is a stroke.
    // Exact crossing splitting prevents Cardinal's intentionally inverted bands becoming bow ties.
    internal static IEnumerable<ImmutableArray<HistoryChartPoint>> CreateStackedSplineBand(
        double x0, double base0, double top0, double x1, double base1, double top1,
        HistoryValueDomain xd, HistoryValueDomain yd)
    {
        var a = new ExactPoint(Exact.FromDouble(x0), Exact.FromDouble(base0));
        var b = new ExactPoint(a.X, Exact.FromDouble(top0));
        var d = new ExactPoint(Exact.FromDouble(x1), Exact.FromDouble(base1));
        var c = new ExactPoint(d.X, Exact.FromDouble(top1));
        var bounds = new ExactBounds(Exact.FromDouble(xd.Minimum), Exact.FromDouble(xd.Maximum),
            Exact.FromDouble(yd.Minimum), Exact.FromDouble(yd.Maximum));
        var firstDifference = b.Y - a.Y; var lastDifference = c.Y - d.Y;
        if (firstDifference.Sign != 0 && lastDifference.Sign != 0 && firstDifference.Sign != lastDifference.Sign)
        {
            var fraction = firstDifference / (firstDifference - lastDifference);
            var crossing = new ExactPoint(a.X + (d.X - a.X) * fraction, a.Y + (d.Y - a.Y) * fraction);
            var left = ClipAndProject([a, b, crossing], bounds);
            if (!left.IsEmpty) yield return left;
            var right = ClipAndProject([crossing, c, d], bounds);
            if (!right.IsEmpty) yield return right;
        }
        else
        {
            var polygon = ClipAndProject([a, b, c, d], bounds);
            if (!polygon.IsEmpty) yield return polygon;
        }
    }

    internal static HistoryChartLine? CreateStackedSplineBoundary(double x0, double y0, double x1, double y1,
        HistoryValueDomain xd, HistoryValueDomain yd)
    {
        var a = new ExactPoint(Exact.FromDouble(x0), Exact.FromDouble(y0));
        var b = new ExactPoint(Exact.FromDouble(x1), Exact.FromDouble(y1));
        var bounds = new ExactBounds(Exact.FromDouble(xd.Minimum), Exact.FromDouble(xd.Maximum),
            Exact.FromDouble(yd.Minimum), Exact.FromDouble(yd.Maximum));
        if (!ClipSegmentPlane(ref a, ref b, bounds.MinX, true, true) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MaxX, true, false) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MinY, false, true) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MaxY, false, false) || a == b) return null;
        return new(ProjectSegmentPoint(a, bounds), ProjectSegmentPoint(b, bounds));
    }
}
