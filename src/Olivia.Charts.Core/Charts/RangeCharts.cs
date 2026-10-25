using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>An original ordered interval. Missing or nonfinite endpoints make the whole observation
/// a gap. X remains finite and ordered even for gaps; Lower/Upper are never silently exchanged.</summary>
public readonly record struct RangeSample(long Key, double X, double? Lower, double? Upper)
{
    public bool IsGap => !Lower.HasValue || !Upper.HasValue ||
        !double.IsFinite(Lower.Value) || !double.IsFinite(Upper.Value);
}

public sealed class RangeSeries
{
    public RangeSeries(string id, string label, IEnumerable<RangeSample> samples, long sourceVersion = 0)
    {
        BarChartSource.ValidateText(id, nameof(id), identity: true);
        BarChartSource.ValidateText(label, nameof(label));
        if (sourceVersion < 0) throw new ArgumentOutOfRangeException(nameof(sourceVersion));
        Samples = BarChartSource.CopyBounded(samples, RangeChartSource.MaximumRawEntries, nameof(samples));
        var keys = new HashSet<long>();
        double? previous = null;
        foreach (var sample in Samples)
        {
            if (!keys.Add(sample.Key)) throw new ArgumentException("Duplicate interval key within series.", nameof(samples));
            if (!double.IsFinite(sample.X) || (previous.HasValue && sample.X <= previous.Value))
                throw new ArgumentException("Interval X must be finite and strictly increasing, including gaps.", nameof(samples));
            if (!sample.IsGap && sample.Lower!.Value > sample.Upper!.Value)
                throw new ArgumentException("Finite interval Lower must not exceed Upper.", nameof(samples));
            previous = sample.X;
        }
        Id = id; Label = label; SourceVersion = sourceVersion;
    }
    public string Id { get; }
    public string Label { get; }
    public long SourceVersion { get; }
    public ImmutableArray<RangeSample> Samples { get; }
}

public enum RangeChartMode { GroupedBars, OverlapBars, LinearArea }
public enum RangeChartStatus { Ready, WorkBudgetExceeded, GeometryBudgetExceeded }

/// <summary>Copied immutable input. Full-source categories and series ordinals do not change when
/// a query hides a series. Numeric X windows select whole categorical slots for bars, while area
/// uses continuous numeric clipping. Source reference is part of every original hit identity.</summary>
public sealed partial class RangeChartSource
{
    public const int MaximumSeries = 8;
    public const int MaximumCategories = 256;
    public const int MaximumRawEntries = 2048;
    public const int MaximumPolygons = 2048;
    public const int MaximumVertices = 16384;
    public const int MaximumSegments = 4096;
    public const int MaximumStrokeVertices = 8192;
    public const int MaximumWork = 524288;

    public RangeChartSource(IEnumerable<RangeSeries> series, long datasetVersion = 0)
    {
        if (datasetVersion < 0) throw new ArgumentOutOfRangeException(nameof(datasetVersion));
        var copy = BarChartSource.CopyBounded(series, MaximumSeries, nameof(series));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var coordinates = new SortedSet<double>();
        var count = 0;
        foreach (var item in copy)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!ids.Add(item.Id)) throw new ArgumentException("Duplicate range series identity.", nameof(series));
            count = checked(count + item.Samples.Length);
            if (count > MaximumRawEntries) throw new ArgumentException("Range sample limit exceeded.", nameof(series));
            foreach (var sample in item.Samples)
            {
                coordinates.Add(sample.X);
                if (coordinates.Count > MaximumCategories)
                    throw new ArgumentException("Range distinct X limit exceeded.", nameof(series));
            }
        }
        Series = copy; Coordinates = coordinates.ToImmutableArray(); RawEntryCount = count;
        DatasetVersion = datasetVersion;
        RequiredWork = checked(160 * count + 16 * Coordinates.Length + 8 * Series.Length);
    }
    public ImmutableArray<RangeSeries> Series { get; }
    public ImmutableArray<double> Coordinates { get; }
    public int RawEntryCount { get; }
    public long DatasetVersion { get; }
    /// <summary>Conservative whole-query score, not measured machine instructions.</summary>
    public int RequiredWork { get; }
}

public sealed class RangeChartOptions
{
    public RangeChartOptions(RangeChartMode mode = RangeChartMode.GroupedBars,
        HistoryValueDomain? fixedXDomain = null, HistoryValueDomain? fixedYDomain = null,
        IEnumerable<string>? visibleSeriesIds = null, int intervalBudget = RangeChartSource.MaximumRawEntries,
        int barBudget = RangeChartSource.MaximumRawEntries, int polygonBudget = RangeChartSource.MaximumPolygons,
        int vertexBudget = RangeChartSource.MaximumVertices, int segmentBudget = RangeChartSource.MaximumSegments,
        int strokeVertexBudget = RangeChartSource.MaximumStrokeVertices, int workBudget = RangeChartSource.MaximumWork)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Check(intervalBudget, RangeChartSource.MaximumRawEntries, nameof(intervalBudget));
        Check(barBudget, RangeChartSource.MaximumRawEntries, nameof(barBudget));
        Check(polygonBudget, RangeChartSource.MaximumPolygons, nameof(polygonBudget));
        Check(vertexBudget, RangeChartSource.MaximumVertices, nameof(vertexBudget));
        Check(segmentBudget, RangeChartSource.MaximumSegments, nameof(segmentBudget));
        Check(strokeVertexBudget, RangeChartSource.MaximumStrokeVertices, nameof(strokeVertexBudget));
        Check(workBudget, RangeChartSource.MaximumWork, nameof(workBudget));
        AllSeries = visibleSeriesIds is null;
        VisibleSeriesIds = visibleSeriesIds is null ? [] :
            BarChartSource.CopyBounded(visibleSeriesIds, RangeChartSource.MaximumSeries, nameof(visibleSeriesIds));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in VisibleSeriesIds)
        {
            BarChartSource.ValidateText(id, nameof(visibleSeriesIds), identity: true);
            if (!ids.Add(id)) throw new ArgumentException("Duplicate visible range series identity.", nameof(visibleSeriesIds));
        }
        Mode = mode; FixedXDomain = fixedXDomain; FixedYDomain = fixedYDomain;
        IntervalBudget = intervalBudget; BarBudget = barBudget; PolygonBudget = polygonBudget;
        VertexBudget = vertexBudget; SegmentBudget = segmentBudget; StrokeVertexBudget = strokeVertexBudget;
        WorkBudget = workBudget;
    }
    private static void Check(int value, int maximum, string name)
    { if (value < 0 || value > maximum) throw new ArgumentOutOfRangeException(name); }
    public static RangeChartOptions Default { get; } = new();
    public RangeChartMode Mode { get; }
    public HistoryValueDomain? FixedXDomain { get; }
    public HistoryValueDomain? FixedYDomain { get; }
    public bool AllSeries { get; }
    public ImmutableArray<string> VisibleSeriesIds { get; }
    public int IntervalBudget { get; }
    public int BarBudget { get; }
    public int PolygonBudget { get; }
    public int VertexBudget { get; }
    public int SegmentBudget { get; }
    public int StrokeVertexBudget { get; }
    public int WorkBudget { get; }
}

/// <summary>Original pair identity. Synthetic edge intersections are never RangeChartHits.</summary>
public readonly record struct RangeChartHit(RangeChartSource Source, string SeriesId,
    long SeriesSourceVersion, RangeSample Sample)
{
    public long DatasetVersion => Source.DatasetVersion;
    public long Key => Sample.Key;
    public double X => Sample.X;
    public double? Lower => Sample.Lower;
    public double? Upper => Sample.Upper;
}

/// <summary>Clipped original vertical interval; normalized screen coordinates increase down.</summary>
public readonly record struct RangeChartInterval(RangeChartHit Hit, int SeriesIndex, double X, double Top, double Bottom);
public readonly record struct RangeChartBar(RangeChartHit Hit, int SeriesIndex, int CategoryIndex,
    double Left, double Top, double Width, double Height);
public readonly record struct RangeChartPolygon(int SeriesIndex, RangeChartHit StartHit, RangeChartHit EndHit,
    ImmutableArray<HistoryChartPoint> Vertices);
public readonly record struct RangeChartSegment(int SeriesIndex, RangeChartHit StartHit, RangeChartHit EndHit,
    bool UpperBoundary, HistoryChartPoint Start, HistoryChartPoint End);

public sealed class RangeChartSnapshot
{
    internal RangeChartSnapshot(RangeChartSource source, RangeChartOptions options, HistoryValueDomain? xDomain,
        HistoryValueDomain? yDomain, ImmutableArray<double> categories, ImmutableArray<RangeChartInterval> intervals,
        ImmutableArray<RangeChartBar> bars, ImmutableArray<RangeChartPolygon> polygons,
        ImmutableArray<RangeChartSegment> segments, ImmutableArray<RangeChartHit> gaps,
        RangeChartStatus status, bool countsKnown, int requiredIntervals, int requiredBars,
        int requiredPolygons, int requiredVertices, int requiredSegments)
    {
        Source = source; Options = options; XDomain = xDomain; YDomain = yDomain; Categories = categories;
        Intervals = intervals; Bars = bars; Polygons = polygons; Segments = segments; Gaps = gaps;
        Status = status; GeometryCountsKnown = countsKnown; RequiredIntervals = requiredIntervals;
        RequiredBars = requiredBars; RequiredPolygons = requiredPolygons; RequiredVertices = requiredVertices;
        RequiredSegments = requiredSegments;
    }
    public RangeChartSource Source { get; }
    public RangeChartOptions Options { get; }
    public HistoryValueDomain? XDomain { get; }
    public HistoryValueDomain? YDomain { get; }
    public ImmutableArray<double> Categories { get; }
    public ImmutableArray<RangeChartInterval> Intervals { get; }
    public ImmutableArray<RangeChartBar> Bars { get; }
    public ImmutableArray<RangeChartPolygon> Polygons { get; }
    public ImmutableArray<RangeChartSegment> Segments { get; }
    public ImmutableArray<RangeChartHit> Gaps { get; }
    public RangeChartStatus Status { get; }
    public bool GeometryCountsKnown { get; }
    public int RequiredIntervals { get; }
    public int RequiredBars { get; }
    public int RequiredPolygons { get; }
    public int RequiredVertices { get; }
    public int RequiredSegments { get; }
    public int RequiredStrokeVertices => GeometryCountsKnown ? checked(RequiredSegments * 2) : -1;
    public int RequiredWork => Source.RequiredWork;
}
