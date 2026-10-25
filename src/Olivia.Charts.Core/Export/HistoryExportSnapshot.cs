using System.Collections.Immutable;
using Olivia.Core.Charts;

namespace Olivia.Core.Export;

/// <summary>An actual drawn Y label: original binary64 value, upward normalized position and text.</summary>
public readonly record struct HistoryExportTick(double Value, double Position, string Text);

/// <summary>
/// Actual captured presentation. AxesDrawn describes the data axes/tick labels, not background grids.
/// A selection can remain outside the viewport; preserving it does not claim its marker was drawn.
/// </summary>
public sealed record HistoryExportPresentation(bool AxesDrawn, string XStartLabel, string XEndLabel,
    ImmutableArray<HistoryExportTick> YTicks, string Status, HistorySample? Selection,
    string SelectedDisplayText, string DetailText);

/// <summary>
/// A bounded source-free copy of the displayed native single-axis history frame. No business series,
/// axis identity or unit is invented. Domains preserve the geometry's nonfinite label bounds.
/// </summary>
public sealed class HistoryExportSnapshot
{
    internal HistoryExportSnapshot(long sourceVersion, TimeWindow window, int rawStartIndex,
        int rawEndIndexExclusive, int pointBudget, int requiredPointBudget, int pointCount,
        ChartBudgetStatus budgetStatus, SnapshotExportFormat format, SnapshotExportLimits limits,
        HistoryPlotValueDomain? rawDomain, HistoryPlotValueDomain? displayDomain,
        HistoryExportPresentation presentation, ImmutableArray<HistorySample> samples)
    {
        SourceVersion = sourceVersion;
        Window = window;
        RawStartIndex = rawStartIndex;
        RawEndIndexExclusive = rawEndIndexExclusive;
        PointBudget = pointBudget;
        RequiredPointBudget = requiredPointBudget;
        PointCount = pointCount;
        BudgetStatus = budgetStatus;
        Format = format;
        Limits = limits;
        RawDomain = rawDomain;
        DisplayDomain = displayDomain;
        Presentation = presentation;
        Samples = samples;
    }

    public int SchemaVersion => 1;
    public string Kind => "single-axis-history";
    public string Scope => "visible-window-raw";
    public string Description => "Original raw samples inside the displayed half-open UTC window, including explicit gaps; not the entire dataset. Presentation labels are captured from the displayed single-axis chart.";
    public long SourceVersion { get; }
    public TimeWindow Window { get; }
    public int RawStartIndex { get; }
    public int RawEndIndexExclusive { get; }
    public int PointBudget { get; }
    public int RequiredPointBudget { get; }
    public int PointCount { get; }
    public ChartBudgetStatus BudgetStatus { get; }
    public int SampleCount => Samples.Length;
    public SnapshotExportFormat Format { get; }
    public SnapshotExportLimits Limits { get; }
    public HistoryPlotValueDomain? RawDomain { get; }
    public HistoryPlotValueDomain? DisplayDomain { get; }
    public HistoryExportPresentation Presentation { get; }
    public ImmutableArray<HistorySample> Samples { get; }
}
