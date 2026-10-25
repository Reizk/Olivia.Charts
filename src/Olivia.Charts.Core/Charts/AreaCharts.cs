using System.Collections.Immutable;
using System.Numerics;

namespace Olivia.Core.Charts;

/// <summary>An original keyed XY sample. An absent or nonfinite coordinate breaks connectivity.</summary>
public readonly record struct AreaSample(long Key, double? X, double? Y)
{
    public bool IsGap => !X.HasValue || !Y.HasValue || !double.IsFinite(X.Value) || !double.IsFinite(Y.Value);
}

/// <summary>Immutable original samples. Every finite X, including an X on a Y-gap, must strictly
/// increase over the whole series. A gap does not reset this ordering requirement.</summary>
public sealed class AreaSeries
{
    public AreaSeries(string id, string label, IEnumerable<AreaSample> samples, long sourceVersion = 0)
    {
        BarChartSource.ValidateText(id, nameof(id), identity: true);
        BarChartSource.ValidateText(label, nameof(label));
        if (sourceVersion < 0) throw new ArgumentOutOfRangeException(nameof(sourceVersion));
        Samples = BarChartSource.CopyBounded(samples, AreaChartSource.MaximumRawEntries, nameof(samples));
        var keys = new HashSet<long>();
        double? previousX = null;
        foreach (var sample in Samples)
        {
            if (!keys.Add(sample.Key)) throw new ArgumentException("Duplicate sample key within series.", nameof(samples));
            if (sample.X is not { } x || !double.IsFinite(x)) continue;
            if (previousX.HasValue && x <= previousX.Value)
                throw new ArgumentException("Finite X coordinates must strictly increase across the entire series, including gaps.", nameof(samples));
            previousX = x;
        }
        Id = id; Label = label; SourceVersion = sourceVersion;
    }
    public string Id { get; }
    public string Label { get; }
    public long SourceVersion { get; }
    public ImmutableArray<AreaSample> Samples { get; }
}

/// <summary>Fixed inclusive domains clip the selected keyed XY presentation, not clamped endpoints.
/// LinearArea preserves the original zero-fill algorithm and 61-unit pair charge. Other modes use
/// their explicit segment, rectangle or spline charges. All geometry budgets admit or refuse the entire frame.</summary>
public sealed class AreaChartOptions
{
    public AreaChartOptions(int geometryBudget = AreaChartSource.MaximumRawEntries,
        HistoryValueDomain? fixedXDomain = null, HistoryValueDomain? fixedYDomain = null,
        int polygonBudget = AreaChartSource.MaximumPolygons, int vertexBudget = AreaChartSource.MaximumVertices,
        int workBudget = AreaChartSource.MaximumWork,
        AreaChartMode mode = AreaChartMode.LinearArea,
        int segmentBudget = AreaChartSource.MaximumSegments, int strokeVertexBudget = AreaChartSource.MaximumStrokeVertices,
        AreaSplineAlgorithm splineAlgorithm = AreaSplineAlgorithm.StrictMinMax, double lineTension = .8,
        bool includeZeroInAutoRange = true)
    {
        if (geometryBudget < 0 || geometryBudget > AreaChartSource.MaximumRawEntries)
            throw new ArgumentOutOfRangeException(nameof(geometryBudget));
        if (polygonBudget < 0 || polygonBudget > AreaChartSource.MaximumPolygons)
            throw new ArgumentOutOfRangeException(nameof(polygonBudget));
        if (vertexBudget < 0 || vertexBudget > AreaChartSource.MaximumVertices)
            throw new ArgumentOutOfRangeException(nameof(vertexBudget));
        if (workBudget < 0 || workBudget > AreaChartSource.MaximumWork)
            throw new ArgumentOutOfRangeException(nameof(workBudget));
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!Enum.IsDefined(splineAlgorithm)) throw new ArgumentOutOfRangeException(nameof(splineAlgorithm));
        if (!double.IsFinite(lineTension) || lineTension < 0 || lineTension > 1)
            throw new ArgumentOutOfRangeException(nameof(lineTension));
        if (segmentBudget < 0 || segmentBudget > AreaChartSource.MaximumSegments) throw new ArgumentOutOfRangeException(nameof(segmentBudget));
        if (strokeVertexBudget < 0 || strokeVertexBudget > AreaChartSource.MaximumStrokeVertices) throw new ArgumentOutOfRangeException(nameof(strokeVertexBudget));
        Mode = mode; SegmentBudget = segmentBudget; StrokeVertexBudget = strokeVertexBudget;
        GeometryBudget = geometryBudget; FixedXDomain = fixedXDomain; FixedYDomain = fixedYDomain;
        PolygonBudget = polygonBudget; VertexBudget = vertexBudget; WorkBudget = workBudget;
        SplineAlgorithm = splineAlgorithm; LineTension = lineTension; IncludeZeroInAutoRange = includeZeroInAutoRange;
    }
    public static AreaChartOptions Default { get; } = new();
    /// <summary>Maximum visible original points, separate from fill polygon and vertex budgets.</summary>
    public int GeometryBudget { get; }
    public HistoryValueDomain? FixedXDomain { get; }
    public HistoryValueDomain? FixedYDomain { get; }
    public int PolygonBudget { get; }
    public int VertexBudget { get; }
    public int WorkBudget { get; }
    public AreaChartMode Mode { get; }
    public int SegmentBudget { get; }
    public int StrokeVertexBudget { get; }
    public AreaSplineAlgorithm SplineAlgorithm { get; }
    /// <summary>For SplineArea, zero takes the original LinearArea path exactly; positive values scale the
    /// independent cubic controls. The .8 default is the reference demo setting.</summary>
    public double LineTension { get; }
    /// <summary>Include zero in automatic Y domains in every mode. Fixed domains are unchanged.</summary>
    public bool IncludeZeroInAutoRange { get; }
}

public readonly record struct AreaChartHit(long DatasetVersion, string SeriesId, long SeriesSourceVersion, AreaSample Sample)
{
    public long Key => Sample.Key;
    public double? X => Sample.X;
    public double? Y => Sample.Y;
}

/// <summary>One visible original sample; normalized screen coordinates increase right and down.</summary>
public readonly record struct AreaChartPoint(AreaChartHit Hit, int SeriesIndex, double X, double Y);

/// <summary>A clipped convex fill from one original adjacent pair. StartHit and EndHit are provenance,
/// never hits at interpolated vertices. Vertices are finite normalized screen coordinates; final
/// binary64 rounding can collapse extremely narrow positive areas. No minimum size is invented.</summary>
public readonly record struct AreaChartPolygon(int SeriesIndex, AreaChartHit StartHit, AreaChartHit EndHit,
    ImmutableArray<HistoryChartPoint> Vertices);

public sealed class AreaChartSnapshot
{
    internal AreaChartSnapshot(AreaChartSource source, AreaChartOptions options, HistoryValueDomain? xDomain,
        HistoryValueDomain? yDomain, ImmutableArray<AreaChartPoint> points, ImmutableArray<AreaChartPolygon> polygons,
        ImmutableArray<AreaChartHit> gaps, int requiredPointCount, int requiredPolygonCount,
        int requiredVertexCount, int requiredWorkCount, bool geometryCountsKnown, ChartBudgetStatus budgetStatus,
        ImmutableArray<AreaChartSegment> segments = default, int requiredSegmentCount = 0, int requiredStrokeVertexCount = 0,
        AreaSplineStatus? splineStatus = null)
    {
        Source = source; Options = options; XDomain = xDomain; YDomain = yDomain;
        Segments = segments.IsDefault ? [] : segments; RequiredSegmentCount = requiredSegmentCount; RequiredStrokeVertexCount = requiredStrokeVertexCount;
        Points = points; Polygons = polygons; Gaps = gaps; RequiredPointCount = requiredPointCount;
        RequiredPolygonCount = requiredPolygonCount; RequiredVertexCount = requiredVertexCount;
        RequiredWorkCount = requiredWorkCount; GeometryCountsKnown = geometryCountsKnown; BudgetStatus = budgetStatus;
        SplineStatus = splineStatus;
        XTicks = DualAxisChartGeometry.CreateTicks(xDomain); YTicks = DualAxisChartGeometry.CreateTicks(yDomain);
        BaselinePosition = yDomain.HasValue ? AreaChartGeometry.NormalizeScreenY(0, yDomain.Value) : .5;
    }
    public AreaChartSource Source { get; }
    public long DatasetVersion => Source.DatasetVersion;
    public AreaChartOptions Options { get; }
    public HistoryValueDomain? XDomain { get; }
    public HistoryValueDomain? YDomain { get; }
    public ImmutableArray<HistoryAxisTick> XTicks { get; }
    public ImmutableArray<HistoryAxisTick> YTicks { get; }
    public double BaselinePosition { get; }
    public bool IsBaselineVisible => YDomain is { Minimum: <= 0, Maximum: >= 0 };
    public ImmutableArray<AreaChartPoint> Points { get; }
    public ImmutableArray<AreaChartPolygon> Polygons { get; }
    public ImmutableArray<AreaChartSegment> Segments { get; }
    /// <summary>Exact clipped segment count, or -1 when work refusal prevented clipping.</summary>
    public int RequiredSegmentCount { get; }
    /// <summary>Two per clipped segment, or -1 when work refusal prevented clipping.</summary>
    public int RequiredStrokeVertexCount { get; }
    public ImmutableArray<AreaChartHit> Gaps { get; }
    /// <summary>Visible original points; -1 if a positive-tension spline refusal prevents its final domain.</summary>
    public int RequiredPointCount { get; }
    /// <summary>Exact admitted-work count; -1 when work refusal prevented geometry calculation.</summary>
    public int RequiredPolygonCount { get; }
    /// <summary>Exact admitted-work count; -1 when work refusal prevented geometry calculation.</summary>
    public int RequiredVertexCount { get; }
    /// <summary>Conservative admission charge, not elapsed time or a CPU-instruction counter.</summary>
    public int RequiredWorkCount { get; }
    public bool GeometryCountsKnown { get; }
    public ChartBudgetStatus BudgetStatus { get; }
    /// <summary>Specific admission result for positive-tension SplineArea only. Null in all old
    /// modes and at zero tension, which uses the unchanged LinearArea path and counts.</summary>
    public AreaSplineStatus? SplineStatus { get; }
}

/// <summary>Original bounded numeric XY area data, always unstacked. At most eight series and
/// 2048 total samples. Only consecutive complete pairs connect; fills split at every zero crossing.
/// Queries never aggregate, decimate, merge, reorder, or manufacture interactive samples.</summary>
public sealed partial class AreaChartSource
{
    public const int MaximumTextLength = 256;
    public const int MaximumSeries = 8;
    public const int MaximumRawEntries = 2048;
    public const int MaximumPolygons = 4096;
    public const int MaximumVertices = 32768;
    public const int MaximumWork = 131072;
    public const int MaximumSegments = 4096;
    public const int MaximumStrokeVertices = 8192;
    public const int WorkPerAdjacentPair = 61;
    public AreaChartSource(IEnumerable<AreaSeries> series, long datasetVersion = 0)
    {
        if (datasetVersion < 0) throw new ArgumentOutOfRangeException(nameof(datasetVersion));
        Series = BarChartSource.CopyBounded(series, MaximumSeries, nameof(series));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var item in Series)
        {
            ArgumentNullException.ThrowIfNull(item);
            total += item.Samples.Length;
            if (total > MaximumRawEntries) throw new ArgumentException("Raw-entry admission limit exceeded.", nameof(series));
            if (!ids.Add(item.Id)) throw new ArgumentException("Duplicate series identifier.", nameof(series));
        }
        DatasetVersion = datasetVersion; RawEntryCount = total;
    }
    public ImmutableArray<AreaSeries> Series { get; }
    public long DatasetVersion { get; }
    public int RawEntryCount { get; }

    public AreaChartSnapshot Query(AreaChartOptions? options = null)
    {
        options ??= AreaChartOptions.Default;
        if (options.Mode == AreaChartMode.SplineArea && options.LineTension > 0)
            return AreaSplineGeometry.Query(this, options);
        if (options.Mode is not (AreaChartMode.LinearArea or AreaChartMode.SplineArea)) return QueryPath(options);
        var entries = new List<(AreaChartHit Hit, int Series)>();
        var pairs = new List<(AreaChartHit First, AreaChartHit Second, int Series)>();
        var gaps = ImmutableArray.CreateBuilder<AreaChartHit>();
        var minX = double.PositiveInfinity; var maxX = double.NegativeInfinity;
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
        HistoryValueDomain? xDomain = options.FixedXDomain ?? (entries.Count == 0 ? null : new HistoryValueDomain(minX, maxX));
        HistoryValueDomain? yDomain = options.FixedYDomain ?? (entries.Count == 0 ? null : new HistoryValueDomain(minY, maxY));
        var points = ImmutableArray.CreateBuilder<AreaChartPoint>();
        if (xDomain.HasValue && yDomain.HasValue)
            foreach (var entry in entries)
            {
                var x = entry.Hit.X!.Value; var y = entry.Hit.Y!.Value;
                if (x < xDomain.Value.Minimum || x > xDomain.Value.Maximum ||
                    y < yDomain.Value.Minimum || y > yDomain.Value.Maximum) continue;
                points.Add(new(entry.Hit, entry.Series, DualAxisChartGeometry.NormalizeY(x, xDomain.Value),
                    AreaChartGeometry.NormalizeScreenY(y, yDomain.Value)));
            }
        // One bounded raw scan is always necessary for domains, gaps and the charge. No exact
        // clipping is attempted unless the caller admits its entire conservative work charge.
        var work = RawEntryCount + WorkPerAdjacentPair * pairs.Count;
        if (work > options.WorkBudget)
            return new(this, options, xDomain, yDomain, [], [], gaps.ToImmutable(), points.Count,
                -1, -1, work, false, ChartBudgetStatus.InsufficientBudget);
        var polygons = ImmutableArray.CreateBuilder<AreaChartPolygon>();
        var vertices = 0;
        if (xDomain.HasValue && yDomain.HasValue && !xDomain.Value.IsConstant && !yDomain.Value.IsConstant)
            foreach (var pair in pairs)
                foreach (var polygon in AreaChartGeometry.CreatePolygons(pair.First, pair.Second, pair.Series,
                    xDomain.Value, yDomain.Value))
                {
                    vertices += polygon.Vertices.Length;
                    polygons.Add(polygon);
                }
        var accepted = points.Count <= options.GeometryBudget && polygons.Count <= options.PolygonBudget && vertices <= options.VertexBudget;
        return new(this, options, xDomain, yDomain, accepted ? points.ToImmutable() : [], accepted ? polygons.ToImmutable() : [],
            gaps.ToImmutable(), points.Count, polygons.Count, vertices, work, true,
            accepted ? ChartBudgetStatus.Satisfied : ChartBudgetStatus.InsufficientBudget);
    }
}

public static partial class AreaChartGeometry
{
    // Reflect the domain instead of subtracting a normalized fraction from one. This preserves
    // subnormal screen distances at the top boundary when the ascending fraction rounds to one.
    internal static double NormalizeScreenY(double value, HistoryValueDomain domain)
        => DualAxisChartGeometry.NormalizeY(-value, new(-domain.Maximum, -domain.Minimum));

    public static AreaChartHit? HitTest(AreaChartSnapshot snapshot, double x, double y, double tolerance = 0)
        => HitTest(snapshot, x, y, tolerance, tolerance);

    /// <summary>Nearest visible original marker in an elliptical tolerance; last drawn wins equal
    /// distances. Fill interiors, edges, clipped intersections and zero crossings are never hits.</summary>
    public static AreaChartHit? HitTest(AreaChartSnapshot snapshot, double x, double y, double xTolerance, double yTolerance)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (!double.IsFinite(xTolerance) || xTolerance < 0 || xTolerance > 1) throw new ArgumentOutOfRangeException(nameof(xTolerance));
        if (!double.IsFinite(yTolerance) || yTolerance < 0 || yTolerance > 1) throw new ArgumentOutOfRangeException(nameof(yTolerance));
        if (x < 0 || x > 1 || y < 0 || y > 1) return null;
        AreaChartHit? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var point in snapshot.Points)
        {
            var dx = Math.Abs(point.X - x); var dy = Math.Abs(point.Y - y);
            if (dx > xTolerance || dy > yTolerance) continue;
            var nx = xTolerance == 0 ? 0 : dx / xTolerance;
            var ny = yTolerance == 0 ? 0 : dy / yTolerance;
            var largest = Math.Max(nx, ny); var smallest = Math.Min(nx, ny);
            var ratio = largest == 0 ? 0 : smallest / largest;
            var distance = largest * Math.Sqrt(1 + ratio * ratio);
            if (distance <= 1 && distance <= bestDistance) { bestDistance = distance; best = point.Hit; }
        }
        return best;
    }

    public static AreaChartHit? GetHit(AreaChartSnapshot snapshot, int index)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return index >= 0 && index < snapshot.Points.Length ? snapshot.Points[index].Hit : null;
    }

    // Original rectangle clipping uses exact rational coordinates in units of 2^-1074. Finite IEEE
    // inputs require at most 2098 integer bits. Every reduced output vertex lies on an original
    // segment or viewport side: its coordinate numerator is <= 4200 bits, denominator <= 2100 bits.
    // The fixed-depth expressions below have intermediates below 32768 bits; there is no iterative
    // convergence, recursion, unbounded input, or data-dependent subdivision. Four convex clips
    // visit at most 4+5+6+7 vertices and return at most 8; two pieces plus projection cost <= 60
    // vertex units per adjacent pair. One additional pair unit covers bounded setup, zero splitting,
    // deduplication and degeneracy checks (WorkPerAdjacentPair = 61). This is a conservative
    // admission model, not a count of individual rational/BigInteger or machine operations.
    internal static IEnumerable<AreaChartPolygon> CreatePolygons(AreaChartHit first, AreaChartHit second,
        int series, HistoryValueDomain xDomain, HistoryValueDomain yDomain)
    {
        var a = new ExactPoint(Exact.FromDouble(first.X!.Value), Exact.FromDouble(first.Y!.Value));
        var b = new ExactPoint(Exact.FromDouble(second.X!.Value), Exact.FromDouble(second.Y!.Value));
        if (a.Y.IsZero && b.Y.IsZero) yield break;
        var bounds = new ExactBounds(Exact.FromDouble(xDomain.Minimum), Exact.FromDouble(xDomain.Maximum),
            Exact.FromDouble(yDomain.Minimum), Exact.FromDouble(yDomain.Maximum));
        if (a.Y.Sign != 0 && b.Y.Sign != 0 && a.Y.Sign != b.Y.Sign)
        {
            var crossing = new ExactPoint(a.X + (b.X - a.X) * ((Exact.Zero - a.Y) / (b.Y - a.Y)), Exact.Zero);
            var left = ClipAndProject(new List<ExactPoint> { new(a.X, Exact.Zero), a, crossing }, bounds);
            if (!left.IsEmpty) yield return new(series, first, second, left);
            var right = ClipAndProject(new List<ExactPoint> { crossing, b, new(b.X, Exact.Zero) }, bounds);
            if (!right.IsEmpty) yield return new(series, first, second, right);
        }
        else
        {
            var polygon = ClipAndProject(new List<ExactPoint> { new(a.X, Exact.Zero), a, b, new(b.X, Exact.Zero) }, bounds);
            if (!polygon.IsEmpty) yield return new(series, first, second, polygon);
        }
    }

    private static ImmutableArray<HistoryChartPoint> ClipAndProject(List<ExactPoint> vertices, ExactBounds bounds)
    {
        vertices = Clip(vertices, bounds.MinX, xAxis: true, lower: true);
        vertices = Clip(vertices, bounds.MaxX, xAxis: true, lower: false);
        vertices = Clip(vertices, bounds.MinY, xAxis: false, lower: true);
        vertices = Clip(vertices, bounds.MaxY, xAxis: false, lower: false);
        if (vertices.Count < 3) return [];
        // Collinear contact with a viewport boundary is not an area. Test before projection, so
        // tiny positive regions remain represented even if screen rounding makes them collinear.
        var origin = vertices[0];
        var direction = vertices[1];
        var hasArea = false;
        for (var i = 2; i < vertices.Count; i++)
            if (!((direction.X - origin.X) * (vertices[i].Y - origin.Y) -
                  (direction.Y - origin.Y) * (vertices[i].X - origin.X)).IsZero) { hasArea = true; break; }
        if (!hasArea) return [];
        var output = ImmutableArray.CreateBuilder<HistoryChartPoint>(vertices.Count);
        foreach (var vertex in vertices)
            output.Add(new(((vertex.X - bounds.MinX) / (bounds.MaxX - bounds.MinX)).ToUnitDouble(),
                ((bounds.MaxY - vertex.Y) / (bounds.MaxY - bounds.MinY)).ToUnitDouble()));
        return output.ToImmutable();
    }

    private static List<ExactPoint> Clip(List<ExactPoint> input, Exact bound, bool xAxis, bool lower)
    {
        if (input.Count == 0) return input;
        var output = new List<ExactPoint>(input.Count + 1);
        var previous = input[^1];
        var previousCoordinate = xAxis ? previous.X : previous.Y;
        var previousInside = lower ? previousCoordinate.CompareTo(bound) >= 0 : previousCoordinate.CompareTo(bound) <= 0;
        foreach (var current in input)
        {
            var coordinate = xAxis ? current.X : current.Y;
            var inside = lower ? coordinate.CompareTo(bound) >= 0 : coordinate.CompareTo(bound) <= 0;
            if (inside != previousInside)
            {
                var fraction = (bound - previousCoordinate) / (coordinate - previousCoordinate);
                var intersection = xAxis
                    ? new ExactPoint(bound, previous.Y + (current.Y - previous.Y) * fraction)
                    : new ExactPoint(previous.X + (current.X - previous.X) * fraction, bound);
                AddDistinct(output, intersection);
            }
            if (inside) AddDistinct(output, current);
            previous = current; previousCoordinate = coordinate; previousInside = inside;
        }
        if (output.Count > 1 && output[0] == output[^1]) output.RemoveAt(output.Count - 1);
        return output;
    }

    private static void AddDistinct(List<ExactPoint> output, ExactPoint point)
    {
        if (output.Count == 0 || output[^1] != point) output.Add(point);
    }

    private readonly record struct ExactPoint(Exact X, Exact Y);
    private readonly record struct ExactBounds(Exact MinX, Exact MaxX, Exact MinY, Exact MaxY);

    private readonly record struct Exact : IComparable<Exact>
    {
        private Exact(BigInteger numerator, BigInteger denominator)
        {
            if (denominator.IsZero) throw new DivideByZeroException();
            if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
            var divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
            Numerator = numerator / divisor; Denominator = denominator / divisor;
        }
        private BigInteger Numerator { get; }
        private BigInteger Denominator { get; }
        public static Exact Zero { get; } = new(BigInteger.Zero, BigInteger.One);
        public bool IsZero => Numerator.IsZero;
        public int Sign => Numerator.Sign;
        public static Exact FromDouble(double value)
        {
            var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
            var exponent = (int)((bits >> 52) & 0x7ff);
            var mantissa = bits & 0x000f_ffff_ffff_ffff;
            var units = new BigInteger(exponent == 0 ? mantissa : mantissa | (1UL << 52));
            if (exponent != 0) units <<= exponent - 1;
            if ((bits >> 63) != 0) units = -units;
            return new(units, BigInteger.One);
        }
        public int CompareTo(Exact other) => (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);
        public static Exact operator +(Exact a, Exact b) => new(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator);
        public static Exact operator -(Exact a, Exact b) => new(a.Numerator * b.Denominator - b.Numerator * a.Denominator, a.Denominator * b.Denominator);
        public static Exact operator *(Exact a, Exact b) => new(a.Numerator * b.Numerator, a.Denominator * b.Denominator);
        public static Exact operator /(Exact a, Exact b) => new(a.Numerator * b.Denominator, a.Denominator * b.Numerator);

        // Correct nearest-even binary64 conversion for a proven normalized ratio. Converting the
        // BigIntegers to double first would produce Infinity/Infinity for extreme finite inputs.
        public double ToUnitDouble()
        {
            if (Numerator.Sign <= 0) return 0;
            if (Numerator >= Denominator) return 1;
            var exponent = (int)(Numerator.GetBitLength() - Denominator.GetBitLength());
            if (exponent >= 0 ? Numerator < (Denominator << exponent) : (Numerator << -exponent) < Denominator) exponent--;
            var shift = Math.Max(-1074, exponent - 52);
            var quotient = BigInteger.DivRem(Numerator << -shift, Denominator, out var remainder);
            var comparison = (remainder << 1).CompareTo(Denominator);
            if (comparison > 0 || (comparison == 0 && !quotient.IsEven)) quotient++;
            return Math.Clamp(Math.ScaleB((double)quotient, shift), 0, 1);
        }
    }
}
