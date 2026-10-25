using System.Collections.Immutable;
using Olivia.Core.Charts;

namespace Olivia.Core.Export;

/// <summary>Explicit rejection categories. Exports never silently truncate data or text.</summary>
public enum SnapshotExportLimit { Rows, TextLength, EncodedBytes }

public sealed class SnapshotExportLimitException : InvalidOperationException
{
    internal SnapshotExportLimitException(SnapshotExportLimit limit, int maximum)
        : base($"Snapshot export exceeds the {limit} limit ({maximum}).")
    {
        Limit = limit;
        Maximum = maximum;
    }

    public SnapshotExportLimit Limit { get; }
    public int Maximum { get; }
}

/// <summary>Immutable logical copy and output limits, independent of display point budgets.</summary>
public sealed class SnapshotExportLimits
{
    public const int AbsoluteMaximumRows = 100_000;
    public const int AbsoluteMaximumTextLength = 4_096;
    public const int AbsoluteMaximumEncodedBytes = 16 * 1_024 * 1_024;

    public static SnapshotExportLimits Default { get; } = new();

    public SnapshotExportLimits(int maxRows = 4_096, int maxTextLength = 1_024,
        int maxEncodedBytes = 4 * 1_024 * 1_024)
    {
        if (maxRows is < 0 or > AbsoluteMaximumRows)
            throw new ArgumentOutOfRangeException(nameof(maxRows));
        if (maxTextLength is < 1 or > AbsoluteMaximumTextLength)
            throw new ArgumentOutOfRangeException(nameof(maxTextLength));
        if (maxEncodedBytes is < 1 or > AbsoluteMaximumEncodedBytes)
            throw new ArgumentOutOfRangeException(nameof(maxEncodedBytes));
        MaxRows = maxRows;
        MaxTextLength = maxTextLength;
        MaxEncodedBytes = maxEncodedBytes;
    }

    /// <summary>Maximum aggregate original samples across effectively visible series.</summary>
    public int MaxRows { get; }
    /// <summary>Maximum UTF-16 code units per arbitrary metadata string.</summary>
    public int MaxTextLength { get; }
    /// <summary>Maximum UTF-8 bytes for each completed serialized output.</summary>
    public int MaxEncodedBytes { get; }
}

public sealed record SnapshotExportFormat(string TimeZoneId, string CultureName,
    string TimeFormat, string NumberFormat);

public sealed record SnapshotExportAxis(string Id, string Label, string Unit, HistoryAxisSide Side,
    bool IsVisible, HistoryValueDomain? Domain, HistoryValueDomain? FixedDomain);

/// <summary>Copied series metadata and a bounded independent array of original source samples.</summary>
public sealed class SnapshotExportSeries
{
    internal SnapshotExportSeries(string id, string label, string axisId, long sourceVersion,
        bool isVisible, bool isRequestedVisible, int rawStartIndex, int rawEndIndexExclusive,
        int allocatedPointBudget, int requiredPointBudget, int pointCount, ImmutableArray<HistorySample> samples)
    {
        Id = id;
        Label = label;
        AxisId = axisId;
        SourceVersion = sourceVersion;
        IsVisible = isVisible;
        IsRequestedVisible = isRequestedVisible;
        RawStartIndex = rawStartIndex;
        RawEndIndexExclusive = rawEndIndexExclusive;
        AllocatedPointBudget = allocatedPointBudget;
        RequiredPointBudget = requiredPointBudget;
        PointCount = pointCount;
        Samples = samples;
    }

    public string Id { get; }
    public string Label { get; }
    public string AxisId { get; }
    public long SourceVersion { get; }
    public bool IsVisible { get; }
    public bool IsRequestedVisible { get; }
    public int RawStartIndex { get; }
    public int RawEndIndexExclusive { get; }
    public int AllocatedPointBudget { get; }
    public int RequiredPointBudget { get; }
    public int PointCount { get; }
    public ImmutableArray<HistorySample> Samples { get; }
}

/// <summary>
/// An immutable bounded copy of one displayed frame. It retains no chart, dataset, raw series,
/// formatter, lease, task or enumerable. Shared immutable strings cannot retain those source graphs.
/// </summary>
public sealed class SnapshotExportSnapshot
{
    internal SnapshotExportSnapshot(long datasetVersion, TimeWindow window, int pointBudget,
        long requiredPointBudget, int pointCount, ChartBudgetStatus budgetStatus, int sampleCount,
        SnapshotExportFormat format, SnapshotExportLimits limits,
        ImmutableArray<SnapshotExportAxis> axes, ImmutableArray<SnapshotExportSeries> series)
    {
        DatasetVersion = datasetVersion;
        Window = window;
        PointBudget = pointBudget;
        RequiredPointBudget = requiredPointBudget;
        PointCount = pointCount;
        BudgetStatus = budgetStatus;
        SampleCount = sampleCount;
        Format = format;
        Limits = limits;
        Axes = axes;
        Series = series;
    }

    public int SchemaVersion => 1;
    public string Scope => "visible-window-raw";
    public string Description => "Original raw samples of effectively visible series inside the displayed half-open UTC window, including gaps and values outside fixed Y domains; not the entire dataset.";
    public long DatasetVersion { get; }
    public TimeWindow Window { get; }
    public int PointBudget { get; }
    public long RequiredPointBudget { get; }
    public int PointCount { get; }
    public ChartBudgetStatus BudgetStatus { get; }
    public int SampleCount { get; }
    public SnapshotExportFormat Format { get; }
    public SnapshotExportLimits Limits { get; }
    public ImmutableArray<SnapshotExportAxis> Axes { get; }
    public ImmutableArray<SnapshotExportSeries> Series { get; }
}
