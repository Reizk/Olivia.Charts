using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>Original keyed XY pair; either absent or nonfinite coordinate makes the whole pair a gap.</summary>
public readonly record struct ScatterSample(long Key, double? X, double? Y)
{
    public bool IsGap => !X.HasValue || !Y.HasValue || !double.IsFinite(X.Value) || !double.IsFinite(Y.Value);
}

public sealed class ScatterSeries
{
    public ScatterSeries(string id, string label, IEnumerable<ScatterSample> samples, long sourceVersion = 0)
    {
        BarChartSource.ValidateText(id, nameof(id), identity: true);
        BarChartSource.ValidateText(label, nameof(label));
        if (sourceVersion < 0) throw new ArgumentOutOfRangeException(nameof(sourceVersion));
        Samples = BarChartSource.CopyBounded(samples, ScatterChartSource.MaximumRawEntries, nameof(samples));
        var keys = new HashSet<long>();
        foreach (var sample in Samples)
            if (!keys.Add(sample.Key)) throw new ArgumentException("Duplicate sample key within series.", nameof(samples));
        Id = id; Label = label; SourceVersion = sourceVersion;
    }
    public string Id { get; }
    public string Label { get; }
    public long SourceVersion { get; }
    public ImmutableArray<ScatterSample> Samples { get; }
}

/// <summary>Inclusive fixed domains clip whole points; auto domains use complete finite XY pairs only.</summary>
public sealed class ScatterChartOptions
{
    public ScatterChartOptions(int geometryBudget = ScatterChartSource.MaximumRawEntries,
        HistoryValueDomain? fixedXDomain = null, HistoryValueDomain? fixedYDomain = null)
    {
        if (geometryBudget < 0 || geometryBudget > ScatterChartSource.MaximumRawEntries)
            throw new ArgumentOutOfRangeException(nameof(geometryBudget));
        GeometryBudget = geometryBudget; FixedXDomain = fixedXDomain; FixedYDomain = fixedYDomain;
    }
    public static ScatterChartOptions Default { get; } = new();
    public int GeometryBudget { get; }
    public HistoryValueDomain? FixedXDomain { get; }
    public HistoryValueDomain? FixedYDomain { get; }
}

public readonly record struct ScatterChartHit(long DatasetVersion, string SeriesId, long SeriesSourceVersion,
    ScatterSample Sample)
{
    public long Key => Sample.Key;
    public double? X => Sample.X;
    public double? Y => Sample.Y;
}

/// <summary>Finite normalized screen coordinates; X increases rightwards, Y downwards.</summary>
public readonly record struct ScatterChartPoint(ScatterChartHit Hit, int SeriesIndex, double X, double Y);

public sealed class ScatterChartSnapshot
{
    internal ScatterChartSnapshot(ScatterChartSource source, ScatterChartOptions options,
        HistoryValueDomain? xDomain, HistoryValueDomain? yDomain, ImmutableArray<ScatterChartPoint> points,
        ImmutableArray<ScatterChartHit> gaps, int requiredPointCount)
    {
        Source = source; Options = options; XDomain = xDomain; YDomain = yDomain; Points = points;
        Gaps = gaps; RequiredPointCount = requiredPointCount;
        BudgetStatus = requiredPointCount <= options.GeometryBudget ? ChartBudgetStatus.Satisfied : ChartBudgetStatus.InsufficientBudget;
        XTicks = DualAxisChartGeometry.CreateTicks(xDomain); YTicks = DualAxisChartGeometry.CreateTicks(yDomain);
    }
    public ScatterChartSource Source { get; }
    public long DatasetVersion => Source.DatasetVersion;
    public ScatterChartOptions Options { get; }
    public HistoryValueDomain? XDomain { get; }
    public HistoryValueDomain? YDomain { get; }
    /// <summary>Ascending data positions, not inverted screen Y positions.</summary>
    public ImmutableArray<HistoryAxisTick> XTicks { get; }
    public ImmutableArray<HistoryAxisTick> YTicks { get; }
    public ImmutableArray<ScatterChartPoint> Points { get; }
    public ImmutableArray<ScatterChartHit> Gaps { get; }
    public int RequiredPointCount { get; }
    public ChartBudgetStatus BudgetStatus { get; }
}

/// <summary>Immutable bounded raw XY data. Stable source order is series order then sample order;
/// coincident points remain separate. No aggregation, decimation or connecting lines are invented.</summary>
public sealed class ScatterChartSource
{
    public const int MaximumTextLength = 256;
    public const int MaximumSeries = 8;
    public const int MaximumRawEntries = 2048;
    public ScatterChartSource(IEnumerable<ScatterSeries> series, long datasetVersion = 0)
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
    public ImmutableArray<ScatterSeries> Series { get; }
    public long DatasetVersion { get; }
    public int RawEntryCount { get; }

    public ScatterChartSnapshot Query(ScatterChartOptions? options = null)
    {
        options ??= ScatterChartOptions.Default;
        var entries = new List<(ScatterChartHit Hit, int Series)>();
        var gaps = ImmutableArray.CreateBuilder<ScatterChartHit>();
        var minX = double.PositiveInfinity; var maxX = double.NegativeInfinity;
        var minY = double.PositiveInfinity; var maxY = double.NegativeInfinity;
        for (var s = 0; s < Series.Length; s++)
            foreach (var sample in Series[s].Samples)
            {
                var hit = new ScatterChartHit(DatasetVersion, Series[s].Id, Series[s].SourceVersion, sample);
                if (sample.IsGap) { gaps.Add(hit); continue; }
                minX = Math.Min(minX, sample.X!.Value); maxX = Math.Max(maxX, sample.X.Value);
                minY = Math.Min(minY, sample.Y!.Value); maxY = Math.Max(maxY, sample.Y.Value);
                entries.Add((hit, s));
            }
        HistoryValueDomain? xDomain = options.FixedXDomain ?? (entries.Count == 0 ? null : new HistoryValueDomain(minX, maxX));
        HistoryValueDomain? yDomain = options.FixedYDomain ?? (entries.Count == 0 ? null : new HistoryValueDomain(minY, maxY));
        var points = ImmutableArray.CreateBuilder<ScatterChartPoint>();
        if (xDomain.HasValue && yDomain.HasValue)
            foreach (var entry in entries)
            {
                var x = entry.Hit.X!.Value; var y = entry.Hit.Y!.Value;
                if (x < xDomain.Value.Minimum || x > xDomain.Value.Maximum ||
                    y < yDomain.Value.Minimum || y > yDomain.Value.Maximum) continue;
                points.Add(new(entry.Hit, entry.Series, DualAxisChartGeometry.NormalizeY(x, xDomain.Value),
                    1 - DualAxisChartGeometry.NormalizeY(y, yDomain.Value)));
            }
        var required = points.Count;
        return new(this, options, xDomain, yDomain,
            required <= options.GeometryBudget ? points.ToImmutable() : [], gaps.ToImmutable(), required);
    }
}

public static class ScatterChartGeometry
{
    public static ScatterChartHit? HitTest(ScatterChartSnapshot snapshot, double x, double y, double tolerance = 0)
        => HitTest(snapshot, x, y, tolerance, tolerance);

    /// <summary>Nearest elliptical marker in normalized coordinates; equal distance selects the last
    /// drawn point. Zero radii require exact equality on their axis. Outside-viewport queries miss.</summary>
    public static ScatterChartHit? HitTest(ScatterChartSnapshot snapshot, double x, double y,
        double xTolerance, double yTolerance)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (!double.IsFinite(xTolerance) || xTolerance < 0 || xTolerance > 1) throw new ArgumentOutOfRangeException(nameof(xTolerance));
        if (!double.IsFinite(yTolerance) || yTolerance < 0 || yTolerance > 1) throw new ArgumentOutOfRangeException(nameof(yTolerance));
        if (x < 0 || x > 1 || y < 0 || y > 1) return null;
        ScatterChartHit? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var point in snapshot.Points)
        {
            var dx = Math.Abs(point.X - x); var dy = Math.Abs(point.Y - y);
            if (dx > xTolerance || dy > yTolerance) continue;
            var nx = xTolerance == 0 ? 0 : dx / xTolerance;
            var ny = yTolerance == 0 ? 0 : dy / yTolerance;
            // A raw sum of squares underflows for tiny but distinct separations, allowing a
            // later noncoincident point to tie an exact hit. Scale before squaring instead.
            var largest = Math.Max(nx, ny);
            var smallest = Math.Min(nx, ny);
            var ratio = largest == 0 ? 0 : smallest / largest;
            var distance = largest * Math.Sqrt(1 + ratio * ratio);
            if (distance <= 1 && distance <= bestDistance) { bestDistance = distance; best = point.Hit; }
        }
        return best;
    }

    /// <summary>Stable visible point enumeration includes coincident/occluded points; gaps and clipped
    /// points have no geometry. Out-of-range indices return null.</summary>
    public static ScatterChartHit? GetHit(ScatterChartSnapshot snapshot, int index)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return index >= 0 && index < snapshot.Points.Length ? snapshot.Points[index].Hit : null;
    }
}
