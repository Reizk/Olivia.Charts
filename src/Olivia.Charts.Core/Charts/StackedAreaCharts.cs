using System.Collections.Immutable;

namespace Olivia.Core.Charts;

public enum StackedAreaChartMode { Stacked, Normalized100 }
/// <summary>Linear connects observations directly. Step holds the left band to the right X;
/// InvertedStep jumps at the left X to the right band. Spline uses bounded independently defined cubic geometry. None creates new observations.</summary>
// Direction matches the official WPF LineStepSeries2D.InvertedStep False/True illustrations:
// https://docs.devexpress.com/WPF/DevExpress.Xpf.Charts.LineStepSeries2D.InvertedStep?v=25.2#remarks
public enum StackedAreaChartInterpolation { Linear = 0, Step = 1, InvertedStep = 2, Spline = 3 }
/// <summary>Independent spline policies, not the proprietary DevExpress control-point formulas.</summary>
public enum StackedAreaSplineAlgorithm { StrictMinMax = 0, Cardinal = 1 }
public enum StackedAreaChartStatus { Ready, WorkBudgetExceeded, GeometryBudgetExceeded, NumericOverflow }

/// <summary>Immutable original XY input for the bounded linear/step/spline stacked-area contract.
/// Series may start/end at different actual X coordinates, but must contain every source X inside
/// their own support. Interior holes, explicit missing/nonfinite samples and mixed-sign series are
/// rejected atomically: their interpolation/stacking parity has not been established. Ordinary
/// positive and negative series stack separately. Normalized100 admits nonnegative visible series
/// only. Absent pre-birth/post-support values never become zero-valued observations or hits.</summary>
public sealed class StackedAreaChartSource
{
    public const int MaximumSeries = RangeChartSource.MaximumSeries;
    public const int MaximumCoordinates = RangeChartSource.MaximumCategories;
    public const int MaximumRawEntries = RangeChartSource.MaximumRawEntries;
    public const int MaximumPolygons = RangeChartSource.MaximumPolygons;
    public const int MaximumVertices = RangeChartSource.MaximumVertices;
    public const int MaximumSegments = RangeChartSource.MaximumSegments;
    public const int MaximumStrokeVertices = RangeChartSource.MaximumStrokeVertices;
    public const int MaximumWork = RangeChartSource.MaximumWork;
    private readonly ImmutableArray<ImmutableArray<int>> _sampleIndices;
    private readonly ImmutableArray<bool> _negativeSeries;
    private readonly int _stepBoundaryWork;
    private readonly int _splineWork;

    public StackedAreaChartSource(IEnumerable<AreaSeries> series, long datasetVersion = 0)
    {
        if (datasetVersion < 0) throw new ArgumentOutOfRangeException(nameof(datasetVersion));
        var copy = BarChartSource.CopyBounded(series, MaximumSeries, nameof(series));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var coordinates = new SortedSet<double>();
        var negativeSeries = ImmutableArray.CreateBuilder<bool>();
        var total = 0;
        foreach (var item in copy)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!ids.Add(item.Id)) throw new ArgumentException("Duplicate stacked-area series identity.", nameof(series));
            total = checked(total + item.Samples.Length);
            if (total > MaximumRawEntries) throw new ArgumentException("Stacked-area raw-entry limit exceeded.", nameof(series));
            var positive = false; var negative = false;
            foreach (var sample in item.Samples)
            {
                if (sample.IsGap)
                    throw new ArgumentException("Stacked area requires finite X/Y observations; explicit gaps are not supported.", nameof(series));
                coordinates.Add(sample.X!.Value);
                if (coordinates.Count > MaximumCoordinates)
                    throw new ArgumentException("Stacked-area distinct-X limit exceeded.", nameof(series));
                positive |= sample.Y!.Value > 0; negative |= sample.Y.Value < 0;
            }
            if (positive && negative)
                throw new ArgumentException("Mixed signs within one stacked-area series are not supported.", nameof(series));
            negativeSeries.Add(negative);
        }
        var xs = coordinates.ToImmutableArray();
        var indices = new Dictionary<double, int>();
        for (var c = 0; c < xs.Length; c++) indices.Add(xs[c], c);
        var maps = ImmutableArray.CreateBuilder<ImmutableArray<int>>();
        foreach (var item in copy)
        {
            var map = Enumerable.Repeat(-1, xs.Length).ToArray();
            for (var i = 0; i < item.Samples.Length; i++) map[indices[item.Samples[i].X!.Value]] = i;
            if (!item.Samples.IsEmpty)
            {
                var first = indices[item.Samples[0].X!.Value];
                var last = indices[item.Samples[^1].X!.Value];
                for (var c = first; c <= last; c++)
                    if (map[c] < 0)
                        throw new ArgumentException("Interior missing X contributions are unknown, not zero; this source is not supported.", nameof(series));
            }
            maps.Add(map.ToImmutableArray());
        }
        Series = copy; Coordinates = xs; RawEntryCount = total; DatasetVersion = datasetVersion;
        _sampleIndices = maps.ToImmutable(); _negativeSeries = negativeSeries.ToImmutable();
        // Bounded alignment/normalization plus the existing Range linear-band clipping charge.
        RequiredWork = checked(160 * total + 32 * copy.Length * xs.Length + 16 * xs.Length + 8 * copy.Length);
        // Each step pair clips two additional genuine boundary segments (<=32 units each)
        // and creates one private bend. Charge every source pair, even if hidden/clipped away.
        _stepBoundaryWork = checked(65 * copy.Sum(item => Math.Max(0, item.Samples.Length - 1)));
        _splineWork = checked(StackedAreaSplineGeometry.WorkPerPair * copy.Sum(item => Math.Max(0, item.Samples.Length - 1)) +
            StackedAreaSplineGeometry.WorkPerAlignedEntry * (copy.Length + 1) * xs.Length);
    }

    public ImmutableArray<AreaSeries> Series { get; }
    public ImmutableArray<double> Coordinates { get; }
    public long DatasetVersion { get; }
    public int RawEntryCount { get; }
    /// <summary>Conservative linear-query admission score, not elapsed time. Step snapshots
    /// include the additional boundary-clipping charge in their RequiredWork.</summary>
    public int RequiredWork { get; }

    internal int RequiredWorkFor(StackedAreaChartOptions options) => checked(RequiredWork +
        (options.Interpolation == StackedAreaChartInterpolation.Spline
            ? options.LineTension == 0 ? 0 : _splineWork
            : options.Interpolation == StackedAreaChartInterpolation.Linear ? 0 : _stepBoundaryWork));

    /// <summary>Validate a retained original selection, including source reference and raw value.
    /// Visibility and presentation mode never change identity. A replacement source is not equal.</summary>
    public bool TryResolve(StackedAreaChartHit hit)
    {
        if (!ReferenceEquals(hit.Source, this)) return false;
        foreach (var series in Series)
        {
            if (!StringComparer.Ordinal.Equals(series.Id, hit.SeriesId) || series.SourceVersion != hit.SeriesSourceVersion) continue;
            foreach (var sample in series.Samples) if (sample.Key == hit.Key) return sample == hit.Sample;
        }
        return false;
    }

    public StackedAreaChartSnapshot Query(StackedAreaChartOptions? options = null)
    {
        options ??= StackedAreaChartOptions.Default;
        foreach (var id in options.VisibleSeriesIds)
            if (!Series.Any(s => StringComparer.Ordinal.Equals(s.Id, id)))
                throw new ArgumentException("Unknown visible stacked-area series identity.", nameof(options));
        for (var s = 0; s < Series.Length; s++)
            if (Visible(s) && options.Mode == StackedAreaChartMode.Normalized100 && _negativeSeries[s])
                throw new ArgumentException("Normalized100 supports nonnegative visible series only; negative normalization parity is unconfirmed.", nameof(options));
        HistoryValueDomain? xd = options.FixedXDomain ?? (Coordinates.IsEmpty ? null : new HistoryValueDomain(Coordinates[0], Coordinates[^1]));
        if (RequiredWorkFor(options) > options.WorkBudget) return Refused(StackedAreaChartStatus.WorkBudgetExceeded);

        var values = new StackedAreaChartContribution?[Series.Length, Coordinates.Length];
        var minimum = 0d; var maximum = 0d; var hasValues = false;
        for (var c = 0; c < Coordinates.Length; c++)
        {
            // Scaling makes each denominator finite, even when the ordinary raw sum overflows.
            var positiveScale = 0d; var negativeScale = 0d;
            for (var s = 0; s < Series.Length; s++)
            {
                var i = _sampleIndices[s][c];
                if (!Visible(s) || i < 0) continue;
                var magnitude = Math.Abs(Series[s].Samples[i].Y!.Value);
                if (_negativeSeries[s]) negativeScale = Math.Max(negativeScale, magnitude);
                else positiveScale = Math.Max(positiveScale, magnitude);
            }
            var positiveTotal = 0d; var negativeTotal = 0d;
            for (var s = 0; s < Series.Length; s++)
            {
                var i = _sampleIndices[s][c];
                if (!Visible(s) || i < 0) continue;
                var magnitude = Math.Abs(Series[s].Samples[i].Y!.Value);
                if (_negativeSeries[s]) negativeTotal += negativeScale == 0 ? 0 : magnitude / negativeScale;
                else positiveTotal += positiveScale == 0 ? 0 : magnitude / positiveScale;
            }
            var positiveBase = 0d; var negativeBase = 0d; var normalizedNumerator = 0d;
            for (var s = 0; s < Series.Length; s++)
            {
                var i = _sampleIndices[s][c];
                if (!Visible(s) || i < 0) continue;
                var series = Series[s]; var sample = series.Samples[i]; var raw = sample.Y!.Value;
                var negative = _negativeSeries[s];
                var scale = negative ? negativeScale : positiveScale;
                var total = negative ? negativeTotal : positiveTotal;
                var proportion = scale == 0 || total == 0 ? 0 : (Math.Abs(raw) / scale) / total;
                var baseline = negative ? negativeBase : positiveBase;
                double top;
                var contribution = raw;
                if (options.Mode == StackedAreaChartMode.Normalized100)
                {
                    normalizedNumerator += positiveScale == 0 ? 0 : raw / positiveScale;
                    top = positiveTotal == 0 ? 0 : normalizedNumerator / positiveTotal;
                    contribution = proportion;
                }
                else top = baseline + raw;
                if (!double.IsFinite(top)) return Refused(StackedAreaChartStatus.NumericOverflow);
                if (negative) negativeBase = top; else positiveBase = top;
                minimum = Math.Min(minimum, top); maximum = Math.Max(maximum, top); hasValues = true;
                values[s, c] = new(new(this, series.Id, series.SourceVersion, sample), s,
                    baseline, top, contribution, proportion);
            }
        }
        if (options.Interpolation == StackedAreaChartInterpolation.Spline && options.LineTension > 0)
            return StackedAreaSplineGeometry.Query(this, options, values, _negativeSeries, xd, minimum, maximum, hasValues);
        HistoryValueDomain? yd = options.FixedYDomain ?? (hasValues
            ? options.Mode == StackedAreaChartMode.Normalized100 ? new HistoryValueDomain(0, 1) : new HistoryValueDomain(minimum, maximum)
            : null);
        var contributions = ImmutableArray.CreateBuilder<StackedAreaChartContribution>();
        var derivedSeries = ImmutableArray.CreateBuilder<RangeSeries>();
        for (var s = 0; s < Series.Length; s++)
        {
            var samples = ImmutableArray.CreateBuilder<RangeSample>();
            for (var c = 0; c < Coordinates.Length; c++)
            {
                if (values[s, c] is not { } value) continue;
                contributions.Add(value);
                samples.Add(new(value.Hit.Key, value.Hit.X!.Value,
                    Math.Min(value.StackBase, value.StackTop), Math.Max(value.StackBase, value.StackTop)));
            }
            derivedSeries.Add(new(Series[s].Id, Series[s].Label, samples, Series[s].SourceVersion));
        }
        // Only a private geometry adapter. Its derived Range hits never escape this query.
        var range = new RangeChartSource(derivedSeries, DatasetVersion);
        var points = ImmutableArray.CreateBuilder<StackedAreaChartPoint>();
        var polygons = ImmutableArray.CreateBuilder<StackedAreaChartPolygon>();
        var segments = ImmutableArray.CreateBuilder<StackedAreaChartSegment>();
        var vertices = 0;
        if (xd.HasValue && yd.HasValue)
            for (var s = 0; s < Series.Length; s++)
            {
                StackedAreaChartContribution? previous = null;
                RangeChartHit? previousRange = null;
                var rangeIndex = 0;
                for (var c = 0; c < Coordinates.Length; c++)
                {
                    if (values[s, c] is not { } value) { previous = null; previousRange = null; continue; }
                    var sample = range.Series[s].Samples[rangeIndex++];
                    var rangeHit = new RangeChartHit(range, Series[s].Id, Series[s].SourceVersion, sample);
                    var low = Math.Max(sample.Lower!.Value, yd.Value.Minimum);
                    var high = Math.Min(sample.Upper!.Value, yd.Value.Maximum);
                    if (sample.X >= xd.Value.Minimum && sample.X <= xd.Value.Maximum && low <= high)
                        points.Add(new(value.Hit, s, DualAxisChartGeometry.NormalizeY(sample.X, xd.Value),
                            AreaChartGeometry.NormalizeScreenY(high, yd.Value), AreaChartGeometry.NormalizeScreenY(low, yd.Value),
                            value.StackBase, value.StackTop, value.Contribution, value.Proportion));
                    if (previous is { } first && previousRange is { } firstRange)
                    {
                        var fillStart = firstRange; var fillEnd = rangeHit;
                        RangeChartHit? bend = null;
                        if (options.Interpolation is StackedAreaChartInterpolation.Step or StackedAreaChartInterpolation.InvertedStep)
                        {
                            // This private Range hit is geometry only: never added to source,
                            // contributions, points or returned provenance. Both boundaries use
                            // the SAME endpoint band, including its already-normalized baseline.
                            var after = options.Interpolation == StackedAreaChartInterpolation.Step;
                            bend = after
                                ? firstRange with { Sample = firstRange.Sample with { X = rangeHit.X } }
                                : rangeHit with { Sample = rangeHit.Sample with { X = firstRange.X } };
                            if (after) fillEnd = bend.Value; else fillStart = bend.Value;
                        }
                        if (!xd.Value.IsConstant && !yd.Value.IsConstant &&
                            AreaChartGeometry.CreateRangePolygon(fillStart, fillEnd, s, xd.Value, yd.Value) is { } polygon)
                        {
                            vertices = checked(vertices + polygon.Vertices.Length);
                            polygons.Add(new(s, first.Hit, value.Hit, polygon.Vertices));
                        }
                        if (bend is { } corner)
                        {
                            AddBoundaries(firstRange, corner);
                            AddBoundaries(corner, rangeHit);
                        }
                        else AddBoundaries(firstRange, rangeHit);

                        void AddBoundaries(RangeChartHit start, RangeChartHit end)
                        {
                            // Clip real edges separately. Viewport/fill closure edges must not
                            // become strokes, and zero-length horizontal/vertical legs vanish.
                            if (AreaChartGeometry.CreateRangeBoundary(start, end, s, false, xd.Value, yd.Value) is { } lower)
                                segments.Add(new(s, first.Hit, value.Hit, false, lower.Start, lower.End));
                            if (AreaChartGeometry.CreateRangeBoundary(start, end, s, true, xd.Value, yd.Value) is { } upper)
                                segments.Add(new(s, first.Hit, value.Hit, true, upper.Start, upper.End));
                        }
                    }
                    previous = value; previousRange = rangeHit;
                }
            }
        var accepted = points.Count <= options.PointBudget && polygons.Count <= options.PolygonBudget &&
            vertices <= options.VertexBudget && segments.Count <= options.SegmentBudget &&
            checked(2 * segments.Count) <= options.StrokeVertexBudget;
        return new(this, options, xd, yd, accepted ? contributions.ToImmutable() : [], accepted ? points.ToImmutable() : [],
            accepted ? polygons.ToImmutable() : [], accepted ? segments.ToImmutable() : [],
            accepted ? StackedAreaChartStatus.Ready : StackedAreaChartStatus.GeometryBudgetExceeded,
            true, points.Count, polygons.Count, vertices, segments.Count);

        bool Visible(int s) => options.AllSeries || options.VisibleSeriesIds.Contains(Series[s].Id, StringComparer.Ordinal);
        StackedAreaChartSnapshot Refused(StackedAreaChartStatus status) =>
            new(this, options, xd, null, [], [], [], [], status, false, -1, -1, -1, -1);
    }
}

/// <summary>Whole-frame budgets and immutable visibility. Fixed domains clip geometry, not raw
/// values. Unknown IDs and unsupported normalized signs reject the query without modifying source.</summary>
public sealed class StackedAreaChartOptions
{
    public StackedAreaChartOptions(StackedAreaChartMode mode = StackedAreaChartMode.Stacked,
        HistoryValueDomain? fixedXDomain = null, HistoryValueDomain? fixedYDomain = null,
        IEnumerable<string>? visibleSeriesIds = null, int pointBudget = StackedAreaChartSource.MaximumRawEntries,
        int polygonBudget = StackedAreaChartSource.MaximumPolygons, int vertexBudget = StackedAreaChartSource.MaximumVertices,
        int segmentBudget = StackedAreaChartSource.MaximumSegments, int strokeVertexBudget = StackedAreaChartSource.MaximumStrokeVertices,
        int workBudget = StackedAreaChartSource.MaximumWork,
        StackedAreaChartInterpolation interpolation = StackedAreaChartInterpolation.Linear,
        StackedAreaSplineAlgorithm splineAlgorithm = StackedAreaSplineAlgorithm.StrictMinMax, double lineTension = .8)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!Enum.IsDefined(interpolation)) throw new ArgumentOutOfRangeException(nameof(interpolation));
        if (!Enum.IsDefined(splineAlgorithm)) throw new ArgumentOutOfRangeException(nameof(splineAlgorithm));
        if (!double.IsFinite(lineTension) || lineTension < 0 || lineTension > 1)
            throw new ArgumentOutOfRangeException(nameof(lineTension));
        Check(pointBudget, StackedAreaChartSource.MaximumRawEntries, nameof(pointBudget));
        Check(polygonBudget, StackedAreaChartSource.MaximumPolygons, nameof(polygonBudget));
        Check(vertexBudget, StackedAreaChartSource.MaximumVertices, nameof(vertexBudget));
        Check(segmentBudget, StackedAreaChartSource.MaximumSegments, nameof(segmentBudget));
        Check(strokeVertexBudget, StackedAreaChartSource.MaximumStrokeVertices, nameof(strokeVertexBudget));
        Check(workBudget, StackedAreaChartSource.MaximumWork, nameof(workBudget));
        AllSeries = visibleSeriesIds is null;
        VisibleSeriesIds = visibleSeriesIds is null ? [] :
            BarChartSource.CopyBounded(visibleSeriesIds, StackedAreaChartSource.MaximumSeries, nameof(visibleSeriesIds));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in VisibleSeriesIds)
        {
            BarChartSource.ValidateText(id, nameof(visibleSeriesIds), identity: true);
            if (!ids.Add(id)) throw new ArgumentException("Duplicate visible stacked-area series identity.", nameof(visibleSeriesIds));
        }
        Mode = mode; Interpolation = interpolation; SplineAlgorithm = splineAlgorithm; LineTension = lineTension; FixedXDomain = fixedXDomain; FixedYDomain = fixedYDomain;
        PointBudget = pointBudget; PolygonBudget = polygonBudget; VertexBudget = vertexBudget;
        SegmentBudget = segmentBudget; StrokeVertexBudget = strokeVertexBudget; WorkBudget = workBudget;
    }
    private static void Check(int value, int maximum, string name)
    { if (value < 0 || value > maximum) throw new ArgumentOutOfRangeException(name); }
    public static StackedAreaChartOptions Default { get; } = new();
    public StackedAreaChartMode Mode { get; }
    public StackedAreaChartInterpolation Interpolation { get; }
    public StackedAreaSplineAlgorithm SplineAlgorithm { get; }
    /// <summary>0 reproduces Linear exactly; positive values scale cubic control offsets. The .8
    /// default is the reference demo setting, not a claim about the proprietary library default.</summary>
    public double LineTension { get; }
    public HistoryValueDomain? FixedXDomain { get; }
    public HistoryValueDomain? FixedYDomain { get; }
    public bool AllSeries { get; }
    public ImmutableArray<string> VisibleSeriesIds { get; }
    public int PointBudget { get; }
    public int PolygonBudget { get; }
    public int VertexBudget { get; }
    public int SegmentBudget { get; }
    public int StrokeVertexBudget { get; }
    public int WorkBudget { get; }
}

public readonly record struct StackedAreaChartHit(StackedAreaChartSource Source, string SeriesId,
    long SeriesSourceVersion, AreaSample Sample)
{
    public long DatasetVersion => Source.DatasetVersion;
    public long Key => Sample.Key;
    public double? X => Sample.X;
    public double? Y => Sample.Y;
}

/// <summary>Only an actual original observation. Contribution is raw signed Y in Stacked or a
/// fraction in Normalized100; Proportion is a same-sign visible-total fraction in either mode.
/// Zero totals produce zero, never NaN. Binary64 rounding may collapse tiny positive spans.</summary>
public readonly record struct StackedAreaChartContribution(StackedAreaChartHit Hit, int SeriesIndex,
    double StackBase, double StackTop, double Contribution, double Proportion)
{
    public double Percentage => Proportion * 100;
}

/// <summary>Original vertical band interval clipped to the viewport. Screen Y increases down.</summary>
public readonly record struct StackedAreaChartPoint(StackedAreaChartHit Hit, int SeriesIndex,
    double X, double Top, double Bottom, double StackBase, double StackTop, double Contribution, double Proportion)
{
    public double Percentage => Proportion * 100;
}
public readonly record struct StackedAreaChartPolygon(int SeriesIndex, StackedAreaChartHit StartHit,
    StackedAreaChartHit EndHit, ImmutableArray<HistoryChartPoint> Vertices);
public readonly record struct StackedAreaChartSegment(int SeriesIndex, StackedAreaChartHit StartHit,
    StackedAreaChartHit EndHit, bool UpperBoundary, HistoryChartPoint Start, HistoryChartPoint End);

public sealed class StackedAreaChartSnapshot
{
    internal StackedAreaChartSnapshot(StackedAreaChartSource source, StackedAreaChartOptions options,
        HistoryValueDomain? xd, HistoryValueDomain? yd, ImmutableArray<StackedAreaChartContribution> contributions,
        ImmutableArray<StackedAreaChartPoint> points, ImmutableArray<StackedAreaChartPolygon> polygons,
        ImmutableArray<StackedAreaChartSegment> segments, StackedAreaChartStatus status, bool countsKnown,
        int requiredPoints, int requiredPolygons, int requiredVertices, int requiredSegments)
    {
        Source = source; Options = options; XDomain = xd; YDomain = yd;
        Contributions = contributions; Points = points; Polygons = polygons; Segments = segments;
        Status = status; GeometryCountsKnown = countsKnown; RequiredPoints = requiredPoints;
        RequiredPolygons = requiredPolygons; RequiredVertices = requiredVertices; RequiredSegments = requiredSegments;
        XTicks = DualAxisChartGeometry.CreateTicks(xd); YTicks = DualAxisChartGeometry.CreateTicks(yd);
    }
    public StackedAreaChartSource Source { get; }
    public long DatasetVersion => Source.DatasetVersion;
    public StackedAreaChartOptions Options { get; }
    public HistoryValueDomain? XDomain { get; }
    public HistoryValueDomain? YDomain { get; }
    public ImmutableArray<HistoryAxisTick> XTicks { get; }
    public ImmutableArray<HistoryAxisTick> YTicks { get; }
    public ImmutableArray<StackedAreaChartContribution> Contributions { get; }
    public ImmutableArray<StackedAreaChartPoint> Points { get; }
    public ImmutableArray<StackedAreaChartPolygon> Polygons { get; }
    public ImmutableArray<StackedAreaChartSegment> Segments { get; }
    /// <summary>Empty under the finite source contract; explicit gaps are rejected, not zero-filled.</summary>
    public ImmutableArray<StackedAreaChartHit> Gaps => [];
    public StackedAreaChartStatus Status { get; }
    public bool GeometryCountsKnown { get; }
    public int RequiredPoints { get; }
    public int RequiredPolygons { get; }
    public int RequiredVertices { get; }
    public int RequiredSegments { get; }
    public int RequiredStrokeVertices => GeometryCountsKnown ? checked(2 * RequiredSegments) : -1;
    public int RequiredWork => Source.RequiredWorkFor(Options);
}

public static class StackedAreaChartGeometry
{
    /// <summary>Closest clipped ORIGINAL vertical interval in DIPs, in stable source order on ties.
    /// No polygon-interior or interpolated/clipped vertex is a selectable observation.</summary>
    public static StackedAreaChartHit? HitTest(StackedAreaChartSnapshot snapshot, double x, double y,
        double viewportWidth, double viewportHeight, double tolerance = 6)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0 || !double.IsFinite(viewportHeight) || viewportHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        if (!double.IsFinite(tolerance) || tolerance < 0 || tolerance > 6) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (snapshot.Status != StackedAreaChartStatus.Ready || x < 0 || x > 1 || y < 0 || y > 1) return null;
        StackedAreaChartHit? best = null; var bestDistance = double.PositiveInfinity;
        foreach (var point in snapshot.Points)
        {
            var dx = Math.Abs(x - point.X) * viewportWidth;
            var dy = (y < point.Top ? point.Top - y : y > point.Bottom ? y - point.Bottom : 0) * viewportHeight;
            if (dx > tolerance || dy > tolerance) continue;
            var largest = Math.Max(dx, dy); var smallest = Math.Min(dx, dy);
            var ratio = largest == 0 ? 0 : smallest / largest;
            var distance = largest * Math.Sqrt(1 + ratio * ratio);
            if (distance <= tolerance && distance < bestDistance) { bestDistance = distance; best = point.Hit; }
        }
        return best;
    }
}
