using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>An original sample. Null, NaN and either infinity remain explicit gaps.</summary>
public readonly record struct HistorySample
{
    public HistorySample(long key, long utcTicks, double? value)
    {
        if (utcTicks < 0 || utcTicks >= TimeWindow.MaximumExclusiveTicks)
            throw new ArgumentOutOfRangeException(nameof(utcTicks));
        Key = key;
        UtcTicks = utcTicks;
        Value = value;
    }

    public long Key { get; }
    public long UtcTicks { get; }
    public double? Value { get; }
    public bool IsGap => !Value.HasValue || !double.IsFinite(Value.Value);
}

[Flags]
public enum HistoryGapReason { None = 0, Missing = 1, NotANumber = 2, Infinite = 4 }

public enum ChartBudgetStatus { Satisfied, InsufficientBudget }

/// <summary>Indices refer to the immutable, timestamp/key-sorted source, and end is exclusive.</summary>
public readonly record struct HistoryGapRange(int RawStartIndex, int RawEndIndexExclusive,
    long StartTicks, long LastTicks, HistoryGapReason Reasons)
{
    public int SampleCount => RawEndIndexExclusive - RawStartIndex;
}

/// <summary>A display-only representative, retaining its exact original key, value and source index.</summary>
public readonly record struct HistoryRepresentativePoint(HistorySample Sample, int RawIndex)
{
    public long Key => Sample.Key;
    public long UtcTicks => Sample.UtcTicks;
    public double Value => Sample.Value!.Value;
}

/// <summary>Only points in the same segment may be connected. Never connect separate segments.</summary>
public sealed class HistoryLineSegment
{
    internal HistoryLineSegment(int start, int end, ImmutableArray<HistoryRepresentativePoint> points)
    {
        RawStartIndex = start;
        RawEndIndexExclusive = end;
        Points = points;
    }

    public int RawStartIndex { get; }
    public int RawEndIndexExclusive { get; }
    public ImmutableArray<HistoryRepresentativePoint> Points { get; }
}

/// <summary>A deeply immutable view. Empty segments with InsufficientBudget are deliberately not a partial line.</summary>
public sealed class HistoryChartSnapshot
{
    internal HistoryChartSnapshot(long sourceVersion, TimeWindow window, int budget, int requiredBudget,
        int rawStart, int rawEnd, ChartBudgetStatus status, ImmutableArray<HistoryLineSegment> segments,
        ImmutableArray<HistoryGapRange> gaps)
    {
        SourceVersion = sourceVersion;
        Window = window;
        PointBudget = budget;
        RequiredPointBudget = requiredBudget;
        RawStartIndex = rawStart;
        RawEndIndexExclusive = rawEnd;
        BudgetStatus = status;
        Segments = segments;
        Gaps = gaps;
        PointCount = segments.Sum(segment => segment.Points.Length);
    }

    public long SourceVersion { get; }
    public TimeWindow Window { get; }
    public int PointBudget { get; }
    public int RequiredPointBudget { get; }
    public int PointCount { get; }
    public int RawStartIndex { get; }
    public int RawEndIndexExclusive { get; }
    public int RawSampleCount => RawEndIndexExclusive - RawStartIndex;
    public ChartBudgetStatus BudgetStatus { get; }
    public ImmutableArray<HistoryLineSegment> Segments { get; }
    public ImmutableArray<HistoryGapRange> Gaps { get; }
}

public sealed class RawHistoryWindow
{
    internal RawHistoryWindow(long sourceVersion, TimeWindow window, int rawStartIndex,
        ImmutableArray<HistorySample> samples)
    {
        SourceVersion = sourceVersion;
        Window = window;
        RawStartIndex = rawStartIndex;
        Samples = samples;
    }

    public long SourceVersion { get; }
    public TimeWindow Window { get; }
    public int RawStartIndex { get; }
    public ImmutableArray<HistorySample> Samples { get; }
}
