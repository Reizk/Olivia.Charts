using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>Presentation of the same original, strictly increasing numeric XY observations.
/// After holds the left value to the right X; Before jumps at the left X to the right value.
/// LinearArea is the original zero-fill presentation and remains the default.</summary>
public enum AreaChartMode { LinearArea = 0, Line = 1, StepLineAfter = 2, StepLineBefore = 3, StepAreaAfter = 4, StepAreaBefore = 5, SplineArea = 6 }

/// <summary>Independent bounded cubic policies, not proprietary DevExpress control-point formulas.</summary>
public enum AreaSplineAlgorithm { StrictMinMax = 0, Cardinal = 1 }
public enum AreaSplineStatus { Ready, WorkBudgetExceeded, GeometryBudgetExceeded, NumericOverflow }

/// <summary>A clipped stroke with original-pair provenance. Its endpoints are geometry, not
/// selectable observations. Points/hit testing exclusively expose original samples.</summary>
public readonly record struct AreaChartSegment(int SeriesIndex, AreaChartHit StartHit, AreaChartHit EndHit,
    HistoryChartPoint Start, HistoryChartPoint End);

public sealed partial class AreaChartSource
{
    public const int WorkPerLinePair = 33;
    public const int WorkPerStepLinePair = 65;
    public const int WorkPerStepAreaPair = 33;

    private AreaChartSnapshot QueryPath(AreaChartOptions options)
    {
        var entries = new List<(AreaChartHit Hit, int Series)>();
        var pairs = new List<(AreaChartHit First, AreaChartHit Second, int Series)>();
        var gaps = ImmutableArray.CreateBuilder<AreaChartHit>();
        var minX = double.PositiveInfinity; var maxX = double.NegativeInfinity;
        // The original zero-inclusive policy remains the default, including unfilled lines.
        var minY = options.IncludeZeroInAutoRange ? 0d : double.PositiveInfinity;
        var maxY = options.IncludeZeroInAutoRange ? 0d : double.NegativeInfinity;
        for (var s = 0; s < Series.Length; s++)
        {
            AreaChartHit? previous = null;
            foreach (var sample in Series[s].Samples)
            {
                var hit = new AreaChartHit(DatasetVersion, Series[s].Id, Series[s].SourceVersion, sample);
                if (sample.IsGap) { gaps.Add(hit); previous = null; continue; }
                minX = Math.Min(minX, sample.X!.Value); maxX = Math.Max(maxX, sample.X.Value);
                minY = Math.Min(minY, sample.Y!.Value); maxY = Math.Max(maxY, sample.Y.Value);
                entries.Add((hit, s));
                if (previous.HasValue) pairs.Add((previous.Value, hit, s));
                previous = hit;
            }
        }
        HistoryValueDomain? xd = options.FixedXDomain ?? (entries.Count == 0 ? null : new HistoryValueDomain(minX, maxX));
        HistoryValueDomain? yd = options.FixedYDomain ?? (entries.Count == 0 ? null : new HistoryValueDomain(minY, maxY));
        var points = ImmutableArray.CreateBuilder<AreaChartPoint>();
        if (xd.HasValue && yd.HasValue)
            foreach (var entry in entries)
            {
                var x = entry.Hit.X!.Value; var y = entry.Hit.Y!.Value;
                if (x < xd.Value.Minimum || x > xd.Value.Maximum || y < yd.Value.Minimum || y > yd.Value.Maximum) continue;
                points.Add(new(entry.Hit, entry.Series, DualAxisChartGeometry.NormalizeY(x, xd.Value),
                    AreaChartGeometry.NormalizeScreenY(y, yd.Value)));
            }
        var fill = options.Mode is AreaChartMode.StepAreaAfter or AreaChartMode.StepAreaBefore;
        var pairWork = fill ? WorkPerStepAreaPair : options.Mode == AreaChartMode.Line ? WorkPerLinePair : WorkPerStepLinePair;
        var work = checked(RawEntryCount + pairWork * pairs.Count);
        if (work > options.WorkBudget)
            return new(this, options, xd, yd, [], [], gaps.ToImmutable(), points.Count, -1, -1, work,
                false, ChartBudgetStatus.InsufficientBudget, [], -1, -1);
        var segments = ImmutableArray.CreateBuilder<AreaChartSegment>();
        var polygons = ImmutableArray.CreateBuilder<AreaChartPolygon>();
        var vertices = 0;
        if (xd.HasValue && yd.HasValue)
            foreach (var pair in pairs)
            {
                if (fill)
                {
                    if (xd.Value.IsConstant || yd.Value.IsConstant) continue;
                    var polygon = AreaChartGeometry.CreateStepPolygon(pair.First, pair.Second, pair.Series,
                        xd.Value, yd.Value, options.Mode == AreaChartMode.StepAreaAfter);
                    if (polygon is { } p) { polygons.Add(p); vertices += p.Vertices.Length; }
                }
                else
                    foreach (var segment in AreaChartGeometry.CreateSegments(pair.First, pair.Second, pair.Series,
                        xd.Value, yd.Value, options.Mode)) segments.Add(segment);
            }
        var strokeVertices = checked(segments.Count * 2);
        var accepted = points.Count <= options.GeometryBudget && polygons.Count <= options.PolygonBudget &&
            vertices <= options.VertexBudget && segments.Count <= options.SegmentBudget && strokeVertices <= options.StrokeVertexBudget;
        return new(this, options, xd, yd, accepted ? points.ToImmutable() : [], accepted ? polygons.ToImmutable() : [],
            gaps.ToImmutable(), points.Count, polygons.Count, vertices, work, true,
            accepted ? ChartBudgetStatus.Satisfied : ChartBudgetStatus.InsufficientBudget,
            accepted ? segments.ToImmutable() : [], segments.Count, strokeVertices);
    }
}

public static partial class AreaChartGeometry
{
    // Same exact finite-IEEE rational representation as the original area clipping. A line has
    // two endpoints, four fixed half-plane clips and one final projection: <=32 admission units.
    // A step line has two such segments plus one setup unit (65). A rectangle's convex four clips
    // visit <=4+5+6+7 vertices, project <=8 vertices, and use bounded setup (33). No recursion,
    // subdivision or floating-point endpoint clamp is involved; these are conservative units.
    internal static IEnumerable<AreaChartSegment> CreateSegments(AreaChartHit first, AreaChartHit second,
        int series, HistoryValueDomain xDomain, HistoryValueDomain yDomain, AreaChartMode mode)
    {
        var a = new ExactPoint(Exact.FromDouble(first.X!.Value), Exact.FromDouble(first.Y!.Value));
        var b = new ExactPoint(Exact.FromDouble(second.X!.Value), Exact.FromDouble(second.Y!.Value));
        var bounds = new ExactBounds(Exact.FromDouble(xDomain.Minimum), Exact.FromDouble(xDomain.Maximum),
            Exact.FromDouble(yDomain.Minimum), Exact.FromDouble(yDomain.Maximum));
        if (mode == AreaChartMode.Line)
        {
            var segment = ClipSegment(a, b, bounds, series, first, second);
            if (segment.HasValue) yield return segment.Value;
            yield break;
        }
        var corner = mode == AreaChartMode.StepLineAfter ? new ExactPoint(b.X, a.Y) : new ExactPoint(a.X, b.Y);
        var firstPart = ClipSegment(a, corner, bounds, series, first, second);
        if (firstPart.HasValue) yield return firstPart.Value;
        var secondPart = ClipSegment(corner, b, bounds, series, first, second);
        if (secondPart.HasValue) yield return secondPart.Value;
    }

    internal static AreaChartPolygon? CreateStepPolygon(AreaChartHit first, AreaChartHit second, int series,
        HistoryValueDomain xDomain, HistoryValueDomain yDomain, bool after)
    {
        var left = Exact.FromDouble(first.X!.Value); var right = Exact.FromDouble(second.X!.Value);
        var height = Exact.FromDouble(after ? first.Y!.Value : second.Y!.Value);
        if (height.IsZero) return null;
        var bounds = new ExactBounds(Exact.FromDouble(xDomain.Minimum), Exact.FromDouble(xDomain.Maximum),
            Exact.FromDouble(yDomain.Minimum), Exact.FromDouble(yDomain.Maximum));
        var vertices = ClipAndProject([new(left, Exact.Zero), new(left, height), new(right, height), new(right, Exact.Zero)], bounds);
        return vertices.IsEmpty ? null : new AreaChartPolygon(series, first, second, vertices);
    }

    private static AreaChartSegment? ClipSegment(ExactPoint a, ExactPoint b, ExactBounds bounds, int series,
        AreaChartHit first, AreaChartHit second)
    {
        if (!ClipSegmentPlane(ref a, ref b, bounds.MinX, true, true) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MaxX, true, false) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MinY, false, true) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MaxY, false, false) || a == b) return null;
        return new(series, first, second, ProjectSegmentPoint(a, bounds), ProjectSegmentPoint(b, bounds));
    }

    private static bool ClipSegmentPlane(ref ExactPoint a, ref ExactPoint b, Exact bound, bool xAxis, bool lower)
    {
        var av = xAxis ? a.X : a.Y; var bv = xAxis ? b.X : b.Y;
        var ai = lower ? av.CompareTo(bound) >= 0 : av.CompareTo(bound) <= 0;
        var bi = lower ? bv.CompareTo(bound) >= 0 : bv.CompareTo(bound) <= 0;
        if (!ai && !bi) return false;
        if (ai == bi) return true;
        var fraction = (bound - av) / (bv - av);
        var intersection = xAxis ? new ExactPoint(bound, a.Y + (b.Y - a.Y) * fraction)
            : new ExactPoint(a.X + (b.X - a.X) * fraction, bound);
        if (!ai) a = intersection; else b = intersection;
        return true;
    }

    private static HistoryChartPoint ProjectSegmentPoint(ExactPoint point, ExactBounds bounds)
        => new(bounds.MinX == bounds.MaxX ? .5 : ((point.X - bounds.MinX) / (bounds.MaxX - bounds.MinX)).ToUnitDouble(),
            bounds.MinY == bounds.MaxY ? .5 : ((bounds.MaxY - point.Y) / (bounds.MaxY - bounds.MinY)).ToUnitDouble());
}
