using System.Collections.Immutable;

namespace Olivia.Core.Charts;

public sealed class DualAxisAxisSnapshot
{
    internal DualAxisAxisSnapshot(HistoryAxisDefinition definition, bool visible, HistoryValueDomain? domain)
    {
        Definition = definition;
        IsVisible = visible;
        Domain = domain;
        Ticks = visible ? DualAxisChartGeometry.CreateTicks(domain) : [];
    }

    public HistoryAxisDefinition Definition { get; }
    public bool IsVisible { get; }
    public HistoryValueDomain? Domain { get; }
    public ImmutableArray<HistoryAxisTick> Ticks { get; }
}

public sealed class DualAxisSeriesSnapshot
{
    internal DualAxisSeriesSnapshot(HistorySeriesDefinition definition, bool visible,
        int allocatedPointBudget, HistoryChartSnapshot? snapshot)
    {
        Definition = definition;
        IsVisible = visible;
        AllocatedPointBudget = allocatedPointBudget;
        RequiredPointBudget = snapshot?.RequiredPointBudget ?? 0;
        PointCount = snapshot?.PointCount ?? 0;
        RawStartIndex = snapshot?.RawStartIndex ?? 0;
        RawEndIndexExclusive = snapshot?.RawEndIndexExclusive ?? 0;
        Segments = snapshot?.Segments ?? [];
        Gaps = snapshot?.Gaps ?? [];
    }

    public HistorySeriesDefinition Definition { get; }
    /// <summary>Effective visibility, including suppression by a hidden mapped axis.</summary>
    public bool IsVisible { get; }
    public long SourceVersion => Definition.Source.SourceVersion;
    public int AllocatedPointBudget { get; }
    public int RequiredPointBudget { get; }
    public int PointCount { get; }
    /// <summary>Hidden series are not queried and report an empty raw range.</summary>
    public int RawStartIndex { get; }
    public int RawEndIndexExclusive { get; }
    public int RawSampleCount => RawEndIndexExclusive - RawStartIndex;
    public ImmutableArray<HistoryLineSegment> Segments { get; }
    public ImmutableArray<HistoryGapRange> Gaps { get; }
}

/// <summary>One immutable shared-X, all-or-nothing finite-point-budget view of a specific source object.</summary>
public sealed class DualAxisChartSnapshot
{
    internal DualAxisChartSnapshot(DualAxisHistorySource source, TimeWindow window, int budget,
        long requiredBudget, ChartBudgetStatus status, DualAxisChartOptions options,
        ImmutableArray<DualAxisAxisSnapshot> axes, ImmutableArray<DualAxisSeriesSnapshot> series)
    {
        Source = source;
        Window = window;
        PointBudget = budget;
        RequiredPointBudget = requiredBudget;
        BudgetStatus = status;
        Options = options;
        Axes = axes;
        Series = series;
        PointCount = series.Sum(item => item.PointCount);
    }

    public DualAxisHistorySource Source { get; }
    public long DatasetVersion => Source.DatasetVersion;
    public long SourceVersion => DatasetVersion;
    public TimeWindow Window { get; }
    public int PointBudget { get; }
    public long RequiredPointBudget { get; }
    public int PointCount { get; }
    public ChartBudgetStatus BudgetStatus { get; }
    public DualAxisChartOptions Options { get; }
    public ImmutableArray<DualAxisAxisSnapshot> Axes { get; }
    public ImmutableArray<DualAxisSeriesSnapshot> Series { get; }
}

/// <summary>An exact original source hit. Neither display sampling nor Y scaling changes its identity.</summary>
public readonly record struct DualAxisHistoryHit(long DatasetVersion, string SeriesId,
    long SeriesSourceVersion, HistorySample Sample)
{
    public long SourceVersion => DatasetVersion;
    public long Key => Sample.Key;
    public long UtcTicks => Sample.UtcTicks;
    public double? Value => Sample.Value;
}
