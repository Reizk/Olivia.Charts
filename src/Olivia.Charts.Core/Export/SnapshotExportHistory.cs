using System.Collections.Immutable;
using Olivia.Core.Charts;
using Olivia.Core.Display;

namespace Olivia.Core.Export;

/// <summary>Native single-axis capture and encoding, independent of the dual-axis export schema.</summary>
public static partial class HistorySnapshotExport
{
    /// <summary>
    /// The caller supplies the exact committed source/snapshot/geometry in one synchronous UI turn.
    /// Version numbers alone cannot establish source identity; the UI adapter must check that identity.
    /// Counts and validates before allocating copied arrays; never queries or retains a source graph.
    /// </summary>
    public static HistoryExportSnapshot Capture(HistorySeries source, HistoryChartSnapshot displayed,
        HistoryPlotGeometry geometry, ValueDisplayFormat format, HistoryExportPresentation presentation,
        SnapshotExportLimits? limits = null, CancellationToken cancellationToken = default) =>
        CaptureCore(source, displayed, geometry, format, presentation, limits, cancellationToken, null);

    // The private checkpoint is solely a deterministic cancellation-contract test seam. Public capture
    // always passes null; the production source remains sealed, immutable and directly indexed.
    private static HistoryExportSnapshot CaptureCore(HistorySeries source, HistoryChartSnapshot displayed,
        HistoryPlotGeometry geometry, ValueDisplayFormat format, HistoryExportPresentation presentation,
        SnapshotExportLimits? limits, CancellationToken cancellationToken, Action<int>? copyCheckpoint)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(displayed);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(presentation);
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= SnapshotExportLimits.Default;
        var start = displayed.RawStartIndex;
        var end = displayed.RawEndIndexExclusive;
        if (source.SourceVersion != displayed.SourceVersion || geometry.SourceVersion != displayed.SourceVersion ||
            geometry.Window != displayed.Window)
            throw new ArgumentException("Source, displayed snapshot and geometry do not describe the same frame.");
        if (start < 0 || end < start || end > source.Count)
            throw new ArgumentException("The displayed raw range is invalid.", nameof(displayed));
        if (end - start > limits.MaxRows)
            throw new SnapshotExportLimitException(SnapshotExportLimit.Rows, limits.MaxRows);
        // O(log source.Count), no ReadWindow allocation and no scan of off-screen source samples.
        if (start != LowerBound(source.Samples, displayed.Window.StartTicks) ||
            end != LowerBound(source.Samples, displayed.Window.EndTicks))
            throw new ArgumentException("The displayed raw range does not cover its complete half-open window.", nameof(displayed));
        for (var index = start; index < end; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!displayed.Window.Contains(source.Samples[index].UtcTicks))
                throw new ArgumentException("A displayed raw sample is outside its window.", nameof(displayed));
        }
        ValidateFrame(source, displayed, geometry, cancellationToken);
        ValidatePresentation(source, geometry, presentation, limits, cancellationToken);
        ValidateText(format.TimeZoneId, limits);
        ValidateText(format.CultureName, limits);
        ValidateText(format.TimeFormat, limits);
        ValidateText(format.NumberFormat, limits);

        var samples = ImmutableArray.CreateBuilder<HistorySample>(end - start);
        for (var index = start; index < end; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.Add(source.Samples[index]);
            copyCheckpoint?.Invoke(samples.Count);
        }
        var ticks = ImmutableArray.CreateBuilder<HistoryExportTick>(presentation.YTicks.Length);
        foreach (var tick in presentation.YTicks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ticks.Add(tick);
        }
        var copy = new HistoryExportPresentation(presentation.AxesDrawn, presentation.XStartLabel,
            presentation.XEndLabel, ticks.MoveToImmutable(), presentation.Status, presentation.Selection,
            presentation.SelectedDisplayText, presentation.DetailText);
        cancellationToken.ThrowIfCancellationRequested();
        return new HistoryExportSnapshot(displayed.SourceVersion, displayed.Window, start, end,
            displayed.PointBudget, displayed.RequiredPointBudget, displayed.PointCount, displayed.BudgetStatus,
            new SnapshotExportFormat(format.TimeZoneId, format.CultureName, format.TimeFormat, format.NumberFormat),
            limits, geometry.RawDomain, geometry.DisplayDomain, copy, samples.MoveToImmutable());
    }

    private static void ValidateFrame(HistorySeries source, HistoryChartSnapshot displayed,
        HistoryPlotGeometry geometry, CancellationToken token)
    {
        if (geometry.Nodes.Length != displayed.PointCount || geometry.Segments.Length != displayed.Segments.Length ||
            geometry.Markers.Length != displayed.Segments.Length || !geometry.Gaps.SequenceEqual(displayed.Gaps))
            throw new ArgumentException("The geometry does not match the displayed representatives and gaps.", nameof(geometry));
        var nodeIndex = 0;
        var edgeIndex = 0;
        for (var segmentIndex = 0; segmentIndex < displayed.Segments.Length; segmentIndex++)
        {
            token.ThrowIfCancellationRequested();
            var segment = displayed.Segments[segmentIndex];
            if (segment.RawStartIndex < displayed.RawStartIndex || segment.RawEndIndexExclusive > displayed.RawEndIndexExclusive ||
                segment.RawEndIndexExclusive <= segment.RawStartIndex || segment.Points.IsEmpty)
                throw new ArgumentException("A displayed segment has invalid raw bounds.", nameof(displayed));
            var shape = geometry.Segments[segmentIndex];
            if (shape != new HistoryPlotSegmentGeometry(segment.RawStartIndex, segment.RawEndIndexExclusive,
                nodeIndex, segment.Points.Length, edgeIndex, segment.Points.Length - 1))
                throw new ArgumentException("The geometry segment does not match the displayed segment.", nameof(geometry));
            var previousRawIndex = -1;
            foreach (var point in segment.Points)
            {
                token.ThrowIfCancellationRequested();
                if (point.RawIndex < segment.RawStartIndex || point.RawIndex >= segment.RawEndIndexExclusive ||
                    point.RawIndex <= previousRawIndex || point.Sample.IsGap ||
                    !SameSample(point.Sample, source.Samples[point.RawIndex]))
                    throw new ArgumentException("A displayed representative does not match its original source index.", nameof(displayed));
                var node = geometry.Nodes[nodeIndex];
                if (node.RawIndex != point.RawIndex || !SameSample(node.Sample, point.Sample) ||
                    !double.IsFinite(node.Position.X) || !double.IsFinite(node.Position.Y))
                    throw new ArgumentException("A geometry node does not match its displayed representative.", nameof(geometry));
                if (previousRawIndex == -1)
                {
                    if (geometry.Markers[segmentIndex] != node)
                        throw new ArgumentException("A geometry marker does not match its first node.", nameof(geometry));
                }
                else
                {
                    if (edgeIndex >= geometry.DataEdges.Length ||
                        geometry.DataEdges[edgeIndex++] != new HistoryPlotEdge(geometry.Nodes[nodeIndex - 1], node))
                        throw new ArgumentException("A geometry edge does not match its contiguous nodes.", nameof(geometry));
                }
                previousRawIndex = point.RawIndex;
                nodeIndex++;
            }
        }
        if (edgeIndex != geometry.DataEdges.Length)
            throw new ArgumentException("The geometry has unexpected data edges.", nameof(geometry));
        foreach (var gap in displayed.Gaps)
        {
            token.ThrowIfCancellationRequested();
            if (gap.RawStartIndex < displayed.RawStartIndex || gap.RawEndIndexExclusive > displayed.RawEndIndexExclusive ||
                gap.RawEndIndexExclusive <= gap.RawStartIndex ||
                source.Samples[gap.RawStartIndex].UtcTicks != gap.StartTicks ||
                source.Samples[gap.RawEndIndexExclusive - 1].UtcTicks != gap.LastTicks)
                throw new ArgumentException("A displayed gap does not match the original source range.", nameof(displayed));
            var reasons = HistoryGapReason.None;
            for (var index = gap.RawStartIndex; index < gap.RawEndIndexExclusive; index++)
            {
                token.ThrowIfCancellationRequested();
                var sample = source.Samples[index];
                if (!sample.IsGap) throw new ArgumentException("A displayed gap contains a finite original sample.", nameof(displayed));
                reasons |= sample.Value is not { } value ? HistoryGapReason.Missing
                    : double.IsNaN(value) ? HistoryGapReason.NotANumber : HistoryGapReason.Infinite;
            }
            if (reasons != gap.Reasons)
                throw new ArgumentException("The displayed gap reasons differ from the original samples.", nameof(displayed));
        }
    }

    private static void ValidatePresentation(HistorySeries source, HistoryPlotGeometry geometry,
        HistoryExportPresentation presentation, SnapshotExportLimits limits, CancellationToken token)
    {
        ValidateText(presentation.XStartLabel, limits);
        ValidateText(presentation.XEndLabel, limits);
        ValidateText(presentation.Status, limits);
        ValidateText(presentation.SelectedDisplayText, limits);
        ValidateText(presentation.DetailText, limits);
        if (presentation.YTicks.IsDefault || presentation.AxesDrawn != !geometry.Nodes.IsEmpty ||
            presentation.YTicks.Length != geometry.Ticks.Length)
            throw new ArgumentException("The captured axes and ticks do not match the rendered geometry.", nameof(presentation));
        if (!presentation.AxesDrawn && (presentation.XStartLabel.Length != 0 ||
            presentation.XEndLabel.Length != 0 || presentation.DetailText.Length != 0))
            throw new ArgumentException("A frame without data axes cannot have drawn axis or detail labels.", nameof(presentation));
        for (var index = 0; index < presentation.YTicks.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var tick = presentation.YTicks[index];
            var original = geometry.Ticks[index];
            if (!SameDouble(tick.Value, original.Value) || !SameDouble(tick.Position, original.Position) ||
                !double.IsFinite(tick.Position))
                throw new ArgumentException("A captured tick differs from the rendered geometry.", nameof(presentation));
            ValidateText(tick.Text, limits);
        }
        if (presentation.Selection is not { } selection)
        {
            if (presentation.SelectedDisplayText.Length != 0)
                throw new ArgumentException("Selection text requires an actual selected sample.", nameof(presentation));
            return;
        }
        // Selection can survive a viewport change. Search the exact source in O(log N) by its real
        // timestamp/key ordering; do not scan the entire source or require selection in exported rows.
        var low = 0;
        var high = source.Count;
        while (low < high)
        {
            token.ThrowIfCancellationRequested();
            var middle = low + (high - low) / 2;
            var candidate = source.Samples[middle];
            if (candidate.UtcTicks < selection.UtcTicks ||
                (candidate.UtcTicks == selection.UtcTicks && candidate.Key < selection.Key)) low = middle + 1;
            else high = middle;
        }
        if (low == source.Count || !SameSample(source.Samples[low], selection))
            throw new ArgumentException("The captured selection does not belong to the displayed source.", nameof(presentation));
    }

    private static int LowerBound(ImmutableArray<HistorySample> samples, long ticks)
    {
        var low = 0;
        var high = samples.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (samples[middle].UtcTicks < ticks) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static bool SameSample(HistorySample left, HistorySample right) =>
        left.Key == right.Key && left.UtcTicks == right.UtcTicks &&
        (left.Value.HasValue == right.Value.HasValue) &&
        (!left.Value.HasValue || SameDouble(left.Value.Value, right.Value!.Value));

    private static bool SameDouble(double left, double right) =>
        BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);

    private static void ValidateText(string text, SnapshotExportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > limits.MaxTextLength)
            throw new SnapshotExportLimitException(SnapshotExportLimit.TextLength, limits.MaxTextLength);
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index])) continue;
            if (!char.IsHighSurrogate(text[index]) || index + 1 == text.Length || !char.IsLowSurrogate(text[++index]))
                throw new ArgumentException("Export presentation must contain well-formed UTF-16 text.");
        }
    }

    private static string ValueState(double? value) => !value.HasValue ? "Null"
        : double.IsNaN(value.Value) ? "NaN"
        : double.IsPositiveInfinity(value.Value) ? "PositiveInfinity"
        : double.IsNegativeInfinity(value.Value) ? "NegativeInfinity" : "Finite";
}
