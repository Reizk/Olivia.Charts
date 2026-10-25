using System.Collections.Immutable;
using Olivia.Core.Charts;

namespace Olivia.Core.Workspaces;

/// <summary>One immutable acquisition, containing all eight channels under one original key.</summary>
public sealed record RealtimeSensorRow
{
    public RealtimeSensorRow(long key, long utcTicks, ImmutableArray<double?> values)
    {
        if (key < 1) throw new ArgumentOutOfRangeException(nameof(key));
        if (utcTicks < 0 || utcTicks >= TimeWindow.MaximumExclusiveTicks)
            throw new ArgumentOutOfRangeException(nameof(utcTicks));
        if (values.IsDefault || values.Length != 8)
            throw new ArgumentException("Exactly eight sensor values are required.", nameof(values));
        Key = key; UtcTicks = utcTicks; Values = values;
    }

    public long Key { get; }
    public long UtcTicks { get; }
    public ImmutableArray<double?> Values { get; }
    public double? this[int seriesIndex] => Values[seriesIndex];
    public HistorySample Sample(int seriesIndex) => new(Key, UtcTicks, this[seriesIndex]);
}

public sealed record RealtimeSensorOptions
{
    public RealtimeSensorOptions(int capacity = 10000, int initialSampleCount = 10000, int pointBudget = 1200,
        TimeSpan? productionInterval = null, TimeSpan? publicationInterval = null,
        TimeSpan? initialWindow = null, TimeSpan? maximumWindow = null, int randomSeed = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        if (initialSampleCount < 0 || initialSampleCount > capacity)
            throw new ArgumentOutOfRangeException(nameof(initialSampleCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(pointBudget, 1);
        Capacity = capacity; InitialSampleCount = initialSampleCount; PointBudget = pointBudget;
        ProductionInterval = productionInterval ?? TimeSpan.FromMilliseconds(15);
        PublicationInterval = publicationInterval ?? TimeSpan.FromMilliseconds(15);
        InitialWindow = initialWindow ?? TimeSpan.FromSeconds(14);
        MaximumWindow = maximumWindow ?? TimeSpan.FromSeconds(50);
        if (ProductionInterval < TimeSpan.FromMilliseconds(1) || ProductionInterval > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(productionInterval));
        if (PublicationInterval < TimeSpan.FromMilliseconds(1) || PublicationInterval > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(publicationInterval));
        if (InitialWindow <= TimeSpan.Zero || InitialWindow > MaximumWindow)
            throw new ArgumentOutOfRangeException(nameof(initialWindow));
        if (MaximumWindow > TimeSpan.FromSeconds(50) || MaximumWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumWindow));
        RandomSeed = randomSeed;
    }

    public int Capacity { get; }
    public int InitialSampleCount { get; }
    public int PointBudget { get; }
    public TimeSpan ProductionInterval { get; }
    public TimeSpan PublicationInterval { get; }
    public TimeSpan InitialWindow { get; }
    public TimeSpan MaximumWindow { get; }
    public int RandomSeed { get; }
}

/// <summary>Eviction is raw retention policy; coalescing replaces only pending display frames.</summary>
public sealed record RealtimeSensorStatistics(long ProducedRows, long PublishedFrames, long DeliveredFrames,
    long DiscardedFrames, int RetainedRows, long EvictedRows, long ReleasedRows,
    int PendingFrames, long CoalescedFrames, bool IsStarted, bool IsStopped, bool IsInitialized,
    bool IsPublicationPaused, bool IsDisplayPaused, bool IsProductionPaused, bool IsFollowingLatest,
    Exception? Error);

public sealed class RealtimeSensorSeriesFrame
{
    private readonly Lazy<ImmutableArray<HistoryPlotEdge>> _dataEdges;

    internal RealtimeSensorSeriesFrame(int index, HistoryChartSnapshot snapshot, HistoryPlotGeometry domain)
    {
        Index = index; Snapshot = snapshot;
        var nodes = ImmutableArray.CreateBuilder<HistoryPlotNode>(snapshot.PointCount);
        var markers = ImmutableArray.CreateBuilder<HistoryPlotNode>(snapshot.Segments.Length);
        foreach (var segment in snapshot.Segments)
        {
            var first = true;
            foreach (var point in segment.Points)
            {
                var node = new HistoryPlotNode(point, domain.Normalize(point.UtcTicks, point.Value));
                nodes.Add(node);
                if (first) { markers.Add(node); first = false; }
            }
        }
        Nodes = nodes.MoveToImmutable(); Markers = markers.ToImmutable();
        // Drawing and hit testing consume Nodes/segments, not duplicated endpoint pairs.
        // Keep the existing diagnostic API, but do not allocate edges for every live frame
        // (including frames coalesced before display). Concurrent readers share one result.
        _dataEdges = new(BuildDataEdges, LazyThreadSafetyMode.ExecutionAndPublication);
    }
    public int Index { get; }
    public string Name => Index % 2 == 0 ? "Sensor Set 1" : "Sensor Set 2";
    public HistoryChartSnapshot Snapshot { get; }
    public ImmutableArray<HistoryPlotNode> Nodes { get; }
    /// <summary>Diagnostic edge view, materialized once on demand; never joins separate finite runs.</summary>
    public ImmutableArray<HistoryPlotEdge> DataEdges => _dataEdges.Value;
    public ImmutableArray<HistoryPlotNode> Markers { get; }

    private ImmutableArray<HistoryPlotEdge> BuildDataEdges()
    {
        var count = 0;
        foreach (var segment in Snapshot.Segments)
            count += Math.Max(0, segment.Points.Length - 1);
        if (count == 0) return [];
        var edges = ImmutableArray.CreateBuilder<HistoryPlotEdge>(count);
        var offset = 0;
        foreach (var segment in Snapshot.Segments)
        {
            for (var index = 1; index < segment.Points.Length; index++)
                edges.Add(new(Nodes[offset + index - 1], Nodes[offset + index]));
            offset += segment.Points.Length;
        }
        return edges.MoveToImmutable();
    }
}

public sealed class RealtimeSensorPaneFrame
{
    private static readonly string[] Names = ["Temperature", "Pressure", "Power", "Intensity"];
    internal RealtimeSensorPaneFrame(int index, HistoryChartSnapshot first, HistoryChartSnapshot second)
    {
        Index = index;
        // Separate segments are concatenated, not joined. Both series use the same padded Y domain.
        var domainSegments = DomainSegments(first).AddRange(DomainSegments(second));
        var combined = new HistoryChartSnapshot(first.SourceVersion, first.Window,
            first.PointBudget + second.PointBudget, first.RequiredPointBudget + second.RequiredPointBudget,
            first.RawStartIndex, first.RawEndIndexExclusive,
            first.BudgetStatus == ChartBudgetStatus.Satisfied && second.BudgetStatus == ChartBudgetStatus.Satisfied
                ? ChartBudgetStatus.Satisfied : ChartBudgetStatus.InsufficientBudget,
            domainSegments, first.Gaps.AddRange(second.Gaps));
        Geometry = HistoryPlotGeometry.Create(combined);
        Series = [new(index * 2, first, Geometry), new(index * 2 + 1, second, Geometry)];
    }
    // The shared normalizer needs only global extrema, not another full copy of both signals' geometry.
    private static ImmutableArray<HistoryLineSegment> DomainSegments(HistoryChartSnapshot snapshot)
    {
        if (snapshot.PointCount == 0) return [];
        HistoryRepresentativePoint? minimum = null, maximum = null;
        foreach (var segment in snapshot.Segments)
            foreach (var point in segment.Points)
            {
                if (minimum is null || point.Value < minimum.Value.Value) minimum = point;
                if (maximum is null || point.Value > maximum.Value.Value) maximum = point;
            }
        var points = minimum == maximum ? ImmutableArray.Create(minimum!.Value)
            : ImmutableArray.Create(minimum!.Value, maximum!.Value);
        // Separate singleton segments cannot fabricate an edge across a gap or between sensor sets.
        return points.Select(point => new HistoryLineSegment(point.RawIndex, point.RawIndex + 1, [point])).ToImmutableArray();
    }
    public int Index { get; }
    public string Name => Names[Index];
    /// <summary>Shared Y-domain/ticks/normalization only. Render Series' fully prepared geometry.</summary>
    public HistoryPlotGeometry Geometry { get; }
    public ImmutableArray<RealtimeSensorSeriesFrame> Series { get; }
}

/// <summary>
/// The only data object delivered to the UI. Original readback is owned by this exact displayed version,
/// including samples omitted by downsampling. No reference to the live session or mutable ring is retained.
/// Raw indices in Series index RawRows, not a newer retention version.
/// </summary>
public sealed class RealtimeSensorFrame
{
    internal RealtimeSensorFrame(long generation, long revision, long sourceVersion, TimeWindow window,
        TimeWindow extent, bool following, ImmutableArray<RealtimeSensorRow> rows, int pointBudget,
        RealtimeSensorStatistics statistics, CancellationToken cancellationToken)
    {
        Generation = generation; Revision = revision; SourceVersion = sourceVersion;
        Window = window; Extent = extent; IsFollowingLatest = following; Statistics = statistics;
        var first = LowerBound(rows, window.StartTicks);
        var end = LowerBound(rows, window.EndTicks);
        RawRows = rows.Slice(first, end - first);
        var series = ImmutableArray.CreateBuilder<HistoryChartSnapshot>(8);
        for (var channel = 0; channel < 8; channel++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            series.Add(RealtimeSensorSampler.Query(RawRows, channel, sourceVersion, window,
                pointBudget, cancellationToken));
        }
        Series = series.MoveToImmutable();
        var panes = ImmutableArray.CreateBuilder<RealtimeSensorPaneFrame>(4);
        for (var index = 0; index < 4; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            panes.Add(new(index, Series[index * 2], Series[index * 2 + 1]));
        }
        Panes = panes.MoveToImmutable();
    }

    public long Generation { get; }
    public long Revision { get; }
    public long SourceVersion { get; }
    public TimeWindow Window { get; }
    public TimeWindow Extent { get; }
    public bool IsFollowingLatest { get; }
    /// <summary>Manual X stays fixed as retention rolls; no silent pan to newer data.</summary>
    public bool IsWindowOutsideRetention => Window.StartTicks < Extent.StartTicks || Window.EndTicks > Extent.EndTicks;
    public ImmutableArray<RealtimeSensorRow> RawRows { get; }
    public ImmutableArray<HistoryChartSnapshot> Series { get; }
    public ImmutableArray<RealtimeSensorPaneFrame> Panes { get; }
    public RealtimeSensorStatistics Statistics { get; }

    /// <summary>Nearest original finite sample, ties by earlier X then original key. Gaps are opt-in.</summary>
    public HistorySample? FindNearest(int seriesIndex, long utcTicks, bool includeGaps = false)
    {
        if (seriesIndex < 0 || seriesIndex >= 8) throw new ArgumentOutOfRangeException(nameof(seriesIndex));
        if (utcTicks < 0 || utcTicks >= TimeWindow.MaximumExclusiveTicks)
            throw new ArgumentOutOfRangeException(nameof(utcTicks));
        HistorySample? best = null;
        long bestDistance = long.MaxValue;
        foreach (var row in RawRows)
        {
            var candidate = row.Sample(seriesIndex);
            if (!includeGaps && candidate.IsGap) continue;
            var distance = Math.Abs(candidate.UtcTicks - utcTicks);
            if (distance < bestDistance)
            {
                best = candidate; bestDistance = distance;
            }
        }
        return best;
    }

    internal static int LowerBound(ImmutableArray<RealtimeSensorRow> rows, long ticks)
    {
        var low = 0; var high = rows.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (rows[middle].UtcTicks < ticks) low = middle + 1; else high = middle;
        }
        return low;
    }
}
