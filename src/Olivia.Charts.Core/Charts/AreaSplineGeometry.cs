using System.Collections.Immutable;

namespace Olivia.Core.Charts;

public sealed partial class AreaChartSource
{
    // Fixed 32-slice tessellation: per slice, at most two convex zero-crossing fill clips
    // (64 units), one genuine curve boundary clip (32), evaluation/setup/projection (32).
    // The point charge covers run-local tangents, controls, hulls and marker projection.
    // These are conservative admission units, not elapsed time or CPU-operation counts.
    public const int WorkPerSplinePair = 32 * 128;
    public const int WorkPerSplinePoint = 64;
}

/// <summary>Independent run-local cubic area geometry. No shared X grid, cumulative values,
/// synthetic observations or gap-crossing tangents are introduced.</summary>
internal static class AreaSplineGeometry
{
    private const int Subdivisions = 32;
    private readonly record struct Curve(double A, double B, double C, double D)
    {
        public double At(double t) => t == 0 ? A : t == 1 ? D :
            Blend(Blend(Blend(A, B, t), Blend(B, C, t), t),
                Blend(Blend(B, C, t), Blend(C, D, t), t), t);
        public bool IsFinite => double.IsFinite(B) && double.IsFinite(C);
        public double Minimum => Math.Min(Math.Min(A, B), Math.Min(C, D));
        public double Maximum => Math.Max(Math.Max(A, B), Math.Max(C, D));
    }

    internal static AreaChartSnapshot Query(AreaChartSource source, AreaChartOptions options)
    {
        var runs = new List<(int Series, AreaChartHit[] Hits, Curve[] Curves)>();
        var gaps = ImmutableArray.CreateBuilder<AreaChartHit>();
        var minimumX = double.PositiveInfinity; var maximumX = double.NegativeInfinity;
        var minimumY = options.IncludeZeroInAutoRange ? 0d : double.PositiveInfinity;
        var maximumY = options.IncludeZeroInAutoRange ? 0d : double.NegativeInfinity;
        var finiteCount = 0; var pairCount = 0;
        for (var s = 0; s < source.Series.Length; s++)
        {
            var series = source.Series[s];
            var run = new List<AreaChartHit>();
            foreach (var sample in series.Samples)
            {
                var hit = new AreaChartHit(source.DatasetVersion, series.Id, series.SourceVersion, sample);
                if (sample.IsGap) { gaps.Add(hit); FinishRun(); continue; }
                finiteCount++;
                minimumX = Math.Min(minimumX, sample.X!.Value); maximumX = Math.Max(maximumX, sample.X.Value);
                minimumY = Math.Min(minimumY, sample.Y!.Value); maximumY = Math.Max(maximumY, sample.Y.Value);
                run.Add(hit);
            }
            FinishRun();

            void FinishRun()
            {
                if (run.Count == 0) return;
                pairCount += run.Count - 1;
                runs.Add((s, run.ToArray(), new Curve[Math.Max(0, run.Count - 1)]));
                run.Clear();
            }
        }
        HistoryValueDomain? xd = options.FixedXDomain ?? (finiteCount == 0 ? null : new HistoryValueDomain(minimumX, maximumX));
        var work = checked(source.RawEntryCount + finiteCount * AreaChartSource.WorkPerSplinePoint +
            pairCount * AreaChartSource.WorkPerSplinePair);
        // Before preflight, only the bounded raw scan has occurred. Cardinal's final automatic
        // Y domain and therefore its visible-marker count are not yet known.
        if (work > options.WorkBudget) return Refused(AreaSplineStatus.WorkBudgetExceeded);

        foreach (var run in runs)
        {
            var hits = run.Hits;
            var incoming = new double[hits.Length]; var outgoing = new double[hits.Length];
            for (var i = 0; i < hits.Length && hits.Length > 1; i++)
            {
                var left = Math.Max(0, i - 1); var right = Math.Min(hits.Length - 1, i + 1);
                var y = hits[i].Y!.Value; var before = hits[left].Y!.Value; var after = hits[right].Y!.Value;
                var offset = DifferenceScaled(after, before, options.LineTension / 3);
                if (i == 0) outgoing[i] = offset;
                else if (i == hits.Length - 1) incoming[i] = offset;
                else
                {
                    var x0 = hits[left].X!.Value; var x = hits[i].X!.Value; var x1 = hits[right].X!.Value;
                    incoming[i] = offset * DualAxisChartGeometry.NormalizeY(x, new(x0, x1));
                    outgoing[i] = offset * DualAxisChartGeometry.NormalizeY(-x, new(-x1, -x0));
                    if (options.SplineAlgorithm == AreaSplineAlgorithm.StrictMinMax)
                    {
                        var factor = y == before || after == y || (y > before) != (after > y) ? 0d : 1d;
                        factor = Limit(factor, Math.Abs(DifferenceScaled(y, before, 1d / 3)), Math.Abs(incoming[i]));
                        factor = Limit(factor, Math.Abs(DifferenceScaled(after, y, 1d / 3)), Math.Abs(outgoing[i]));
                        incoming[i] *= factor; outgoing[i] *= factor;
                    }
                }
            }
            for (var i = 0; i < run.Curves.Length; i++)
            {
                var a = hits[i].Y!.Value; var d = hits[i + 1].Y!.Value;
                var b = a + outgoing[i]; var c = d - incoming[i + 1];
                if (options.SplineAlgorithm == AreaSplineAlgorithm.StrictMinMax)
                {
                    // Strict policy limits even an overflowing provisional sum before use.
                    // Finite endpoints and safely scaled offsets cannot produce NaN here.
                    b = Math.Clamp(b, Math.Min(a, d), Math.Max(a, d));
                    c = Math.Clamp(c, Math.Min(a, d), Math.Max(a, d));
                }
                var curve = new Curve(a, b, c, d);
                if (!curve.IsFinite) return Refused(AreaSplineStatus.NumericOverflow);
                run.Curves[i] = curve;
                minimumY = Math.Min(minimumY, curve.Minimum); maximumY = Math.Max(maximumY, curve.Maximum);
            }
        }

        // The hull is deliberately conservative. It cannot hide a Cardinal overshoot, and
        // no zero is added when the caller opts out, although fills still close to actual zero.
        HistoryValueDomain? yd = options.FixedYDomain ?? (finiteCount == 0 ? null : new HistoryValueDomain(minimumY, maximumY));
        var points = ImmutableArray.CreateBuilder<AreaChartPoint>();
        var polygons = ImmutableArray.CreateBuilder<AreaChartPolygon>();
        var segments = ImmutableArray.CreateBuilder<AreaChartSegment>();
        var vertexCount = 0;
        foreach (var run in runs)
            for (var i = 0; i < run.Hits.Length; i++)
            {
                var first = run.Hits[i];
                if (xd is not { } xDomain || yd is not { } yDomain) continue;
                var x = first.X!.Value; var y = first.Y!.Value;
                if (x >= xDomain.Minimum && x <= xDomain.Maximum && y >= yDomain.Minimum && y <= yDomain.Maximum)
                    points.Add(new(first, run.Series, DualAxisChartGeometry.NormalizeY(x, xDomain),
                        AreaChartGeometry.NormalizeScreenY(y, yDomain)));
                if (i + 1 == run.Hits.Length) continue;
                var last = run.Hits[i + 1]; var nextX = last.X!.Value;
                var xCurve = new Curve(x, Blend(x, nextX, options.LineTension / 3),
                    Blend(x, nextX, 1 - options.LineTension / 3), nextX);
                var curve = run.Curves[i];
                var previousX = x; var previousY = y;
                for (var k = 1; k <= Subdivisions; k++)
                {
                    var t = (double)k / Subdivisions;
                    var currentX = xCurve.At(t); var currentY = curve.At(t);
                    if (!xDomain.IsConstant && !yDomain.IsConstant)
                        foreach (var polygon in AreaChartGeometry.CreateStackedSplineBand(previousX, 0, previousY,
                            currentX, 0, currentY, xDomain, yDomain))
                        {
                            polygons.Add(new(run.Series, first, last, polygon));
                            vertexCount = checked(vertexCount + polygon.Length);
                        }
                    if (AreaChartGeometry.CreateStackedSplineBoundary(previousX, previousY, currentX, currentY,
                        xDomain, yDomain) is { } segment)
                        segments.Add(new(run.Series, first, last, segment.Start, segment.End));
                    previousX = currentX; previousY = currentY;
                }
            }
        var strokeVertices = checked(segments.Count * 2);
        var accepted = points.Count <= options.GeometryBudget && polygons.Count <= options.PolygonBudget &&
            vertexCount <= options.VertexBudget && segments.Count <= options.SegmentBudget && strokeVertices <= options.StrokeVertexBudget;
        return new(source, options, xd, yd, accepted ? points.ToImmutable() : [], accepted ? polygons.ToImmutable() : [],
            gaps.ToImmutable(), points.Count, polygons.Count, vertexCount, work, true,
            accepted ? ChartBudgetStatus.Satisfied : ChartBudgetStatus.InsufficientBudget,
            accepted ? segments.ToImmutable() : [], segments.Count, strokeVertices,
            accepted ? AreaSplineStatus.Ready : AreaSplineStatus.GeometryBudgetExceeded);

        AreaChartSnapshot Refused(AreaSplineStatus status) => new(source, options, xd, options.FixedYDomain,
            [], [], gaps.ToImmutable(), -1, -1, -1, work, false, ChartBudgetStatus.InsufficientBudget, [], -1, -1, status);
    }

    private static double Limit(double factor, double allowance, double change) =>
        change > allowance ? Math.Min(factor, allowance / change) : factor;

    // factor is at most 1/3. Same-sign subtraction is finite; opposite-sign operands are
    // scaled first, so even a MaxValue-to-negative-MaxValue difference remains representable.
    private static double DifferenceScaled(double a, double b, double factor) =>
        Math.Sign(a) == Math.Sign(b) ? (a - b) * factor : a * factor - b * factor;

    private static double Blend(double a, double b, double t)
    {
        if (t == 0 || a == b) return a;
        if (t == 1) return b;
        return Math.Clamp(Math.Sign(a) == Math.Sign(b) ? a + (b - a) * t : a * (1 - t) + b * t,
            Math.Min(a, b), Math.Max(a, b));
    }
}
