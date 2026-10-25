using System.Collections.Immutable;
using System.Globalization;

namespace Olivia.Core.Charts;

public enum PriceCrosshairPane { Price, Cci, WilliamsR }

/// <summary>One exact, in-domain value at the selected X coordinate. Y increases downwards.
/// Volume shares the price pane but uses its independent zero-based volume domain.</summary>
public readonly record struct PriceCrosshairHighlight(PriceCrosshairPane Pane, string Series, double ScreenY);

/// <summary>Immutable transient hover readout, independent of original-row selection and the
/// final-original-close annotation. Nullable indicator points mean unavailable, not zero.
/// The hit and optional aggregate segment retain raw values and original timestamp offsets.</summary>
public sealed class PriceCrosshairSnapshot
{
    internal PriceCrosshairSnapshot(PriceChartSnapshot prices, PriceTimeAggregationSnapshot? aggregation,
        int barIndex, double screenX, PriceChartHit hit, PriceTimeAggregateSegment? aggregateSegment,
        PriceBollingerPoint? bollingerPoint, PriceCciPoint? cciPoint, PriceWilliamsRPoint? williamsRPoint,
        bool volumeEnabled, bool bollingerEnabled, bool cciEnabled, bool williamsREnabled,
        string headerText, string labelText, string accessibleSummary,
        ImmutableArray<PriceCrosshairHighlight> highlights)
    {
        Prices = prices; Aggregation = aggregation; BarIndex = barIndex; ScreenX = screenX; Hit = hit;
        AggregateSegment = aggregateSegment; BollingerPoint = bollingerPoint; CciPoint = cciPoint;
        WilliamsRPoint = williamsRPoint; VolumeEnabled = volumeEnabled; BollingerEnabled = bollingerEnabled;
        CciEnabled = cciEnabled; WilliamsREnabled = williamsREnabled; HeaderText = headerText;
        LabelText = labelText; AccessibleSummary = accessibleSummary; Highlights = highlights;
    }

    public PriceChartSnapshot Prices { get; }
    public PriceTimeAggregationSnapshot? Aggregation { get; }
    public int BarIndex { get; }
    public double ScreenX { get; }
    public PriceChartHit Hit { get; }
    public PriceTimeAggregateSegment? AggregateSegment { get; }
    public PriceBollingerPoint? BollingerPoint { get; }
    public PriceCciPoint? CciPoint { get; }
    public PriceWilliamsRPoint? WilliamsRPoint { get; }
    public bool VolumeEnabled { get; }
    public bool BollingerEnabled { get; }
    public bool CciEnabled { get; }
    public bool WilliamsREnabled { get; }
    public string HeaderText { get; }
    /// <summary>Complete multiline display text, including HeaderText. OHLC uses custom 0.000,
    /// volume uses custom 0., and indicator F4 is an explicit Olivia compatibility choice.</summary>
    public string LabelText { get; }
    public string AccessibleSummary { get; }
    public ImmutableArray<PriceCrosshairHighlight> Highlights { get; }
}

/// <summary>Finite, bounded shared-X hover. Every supplied provider must belong to this exact
/// price source; volumes must also belong to this exact viewport snapshot. Cached sorted indicator
/// points are searched by BarIndex, never recalculated, copied, sorted, filled or searched backwards.
/// Gaps return no hover; time-coordinate ties choose the last segment. Missing sessions and
/// time padding do not invent values. Out-of-Y-domain
/// raw values remain readable, with an explicit clipping note but no clamped highlight.
/// Indicator providers are the factory-produced sorted, finite immutable caches; this query
/// checks source identity, not arbitrary user-constructed provider array validity.</summary>
public static class PriceCrosshair
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly HistoryValueDomain CciDomain = new(-150, 150);
    private static readonly HistoryValueDomain WilliamsRDomain = new(-100, 0);

    public static PriceCrosshairSnapshot? Query(PriceChartSnapshot prices, double normalizedX,
        PriceVolumeSnapshot? volumes = null, PriceBollingerSnapshot? bands = null,
        PriceCciSnapshot? cci = null, PriceWilliamsRSnapshot? williamsR = null,
        PriceTimeAggregationSnapshot? aggregation = null)
    {
        ArgumentNullException.ThrowIfNull(prices);
        // Validate even when the pointer or frame cannot produce a hover.
        if (volumes is not null && !ReferenceEquals(volumes.Prices, prices))
            throw new ArgumentException("Volumes must belong to the queried price snapshot.", nameof(volumes));
        ValidateSource(prices, bands?.Source, nameof(bands));
        ValidateSource(prices, cci?.Source, nameof(cci));
        ValidateSource(prices, williamsR?.Source, nameof(williamsR));
        ValidateSource(prices, aggregation?.Source, nameof(aggregation));
        if (!double.IsFinite(normalizedX) || normalizedX < 0 || normalizedX > 1 ||
            prices.BarCount == 0 || prices.Domain is not { } priceDomain ||
            prices.BudgetStatus != ChartBudgetStatus.Satisfied)
            return null;

        // Adjacent centers' midpoint is the slot boundary; an exact tie selects the later slot.
        if (prices.FindBarAt(normalizedX) is not { } index) return null;
        var bar = prices.Source.Bars[index];
        if (bar.IsGap) return null;
        var hit = new PriceChartHit(prices.DatasetVersion, prices.Source.SeriesId, prices.Source.SourceVersion, bar);
        var segment = aggregation is null ? (PriceTimeAggregateSegment?)null : aggregation.Segments[index];
        var bb = bands is null ? (PriceBollingerPoint?)null : Find(bands.Points, index, static point => point.BarIndex);
        var cp = cci is null ? (PriceCciPoint?)null : Find(cci.Points, index, static point => point.BarIndex);
        var wr = williamsR is null ? (PriceWilliamsRPoint?)null : Find(williamsR.Points, index, static point => point.BarIndex);
        var highlights = ImmutableArray.CreateBuilder<PriceCrosshairHighlight>(7);
        var clipping = new List<string>(7);
        AddHighlight(PriceCrosshairPane.Price, "Close", bar.Close!.Value, priceDomain);
        if (volumes?.Domain is { } volumeDomain && bar.Volume is { } volume)
            AddHighlight(PriceCrosshairPane.Price, "Volume", volume, volumeDomain, zeroBaseline: true);
        if (bb is { } band)
        {
            AddHighlight(PriceCrosshairPane.Price, "Bollinger Upper", band.Upper, priceDomain);
            AddHighlight(PriceCrosshairPane.Price, "Bollinger Middle", band.Middle, priceDomain);
            AddHighlight(PriceCrosshairPane.Price, "Bollinger Lower", band.Lower, priceDomain);
        }
        if (cp is { } cciPoint) AddHighlight(PriceCrosshairPane.Cci, "CCI", cciPoint.Value, CciDomain);
        if (wr is { } wrPoint) AddHighlight(PriceCrosshairPane.WilliamsR, "Williams %R", wrPoint.Value, WilliamsRDomain);

        var header = (aggregation is null ? "" : $"Aggregated {PriceTimeAggregator.GetLabel(aggregation.Interval)} · ") +
            bar.Timestamp.ToString("hh:mm", Invariant) + (aggregation is null ? "" : " (first constituent)");
        var lines = new List<string>(14)
        {
            header, $"Open: {Format(bar.Open, "0.000")}", $"High: {Format(bar.High, "0.000")}",
            $"Low: {Format(bar.Low, "0.000")}", $"Close: {Format(bar.Close, "0.000")}"
        };
        if (volumes is not null)
            lines.Add($"Volume: {Format(bar.Volume, "0.")}" +
                (segment is { VolumeStatus: PriceAggregateVolumeStatus.Missing or PriceAggregateVolumeStatus.Overflow } s
                    ? $" ({s.VolumeStatus})" : ""));
        if (bands is not null)
        {
            lines.Add($"Bollinger Upper: {Format(bb?.Upper, "F4")}");
            lines.Add($"Bollinger Middle: {Format(bb?.Middle, "F4")}");
            lines.Add($"Bollinger Lower: {Format(bb?.Lower, "F4")}");
        }
        if (cci is not null) lines.Add($"CCI: {Format(cp?.Value, "F4")}");
        if (williamsR is not null) lines.Add($"Williams %R: {Format(wr?.Value, "F4")}");
        if (bands is not null || cci is not null || williamsR is not null)
            lines.Add("Indicator F4 formatting: Olivia compatibility.");
        foreach (var series in clipping)
            lines.Add($"No marker: {series} (outside Y)");
        var label = string.Join("\n", lines);
        var summary = CreateSummary(label, hit, index, aggregation, segment, volumes is not null, bands is not null,
            cci is not null, williamsR is not null, bb, cp, wr);
        return new(prices, aggregation, index, prices.GetNormalizedX(index), hit, segment, bb, cp, wr,
            volumes is not null, bands is not null, cci is not null, williamsR is not null,
            header, label, summary, highlights.ToImmutable());

        void AddHighlight(PriceCrosshairPane pane, string series, double value, HistoryValueDomain domain,
            bool zeroBaseline = false)
        {
            if (!double.IsFinite(value) || value < domain.Minimum || value > domain.Maximum)
            {
                clipping.Add(series);
                return;
            }
            // Match the raw reversed-domain price projection, retaining tiny near-top values.
            var y = zeroBaseline && domain.Minimum == 0 && domain.Maximum == 0 ? 1 :
                DualAxisChartGeometry.NormalizeY(-value, new(-domain.Maximum, -domain.Minimum));
            highlights.Add(new(pane, series, y));
        }
    }

    private static void ValidateSource(PriceChartSnapshot prices, PriceChartSource? source, string parameter)
    {
        if (source is not null && !ReferenceEquals(source, prices.Source))
            throw new ArgumentException("Provider must belong to the queried price source.", parameter);
    }

    private static T? Find<T>(ImmutableArray<T> points, int index, Func<T, int> barIndex) where T : struct
    {
        var first = 0;
        var last = points.Length - 1;
        while (first <= last)
        {
            var middle = first + (last - first) / 2;
            var candidate = barIndex(points[middle]);
            if (candidate == index) return points[middle];
            if (candidate < index) first = middle + 1;
            else last = middle - 1;
        }
        return null;
    }

    private static string Format(double? value, string format) => value?.ToString(format, Invariant) ?? "unavailable";

    private static string CreateSummary(string label, PriceChartHit hit, int index,
        PriceTimeAggregationSnapshot? aggregation, PriceTimeAggregateSegment? segment,
        bool volumeEnabled, bool bandsEnabled, bool cciEnabled, bool williamsREnabled,
        PriceBollingerPoint? bb, PriceCciPoint? cci, PriceWilliamsRPoint? wr)
    {
        var bar = hit.Bar;
        var lines = new List<string>
        {
            label,
            FormattableString.Invariant($"{(aggregation is null ? "Original" : "Derived")} source: {hit.SeriesId}; dataset {hit.DatasetVersion}; source {hit.SourceVersion}; bar index {index}; key {hit.Key}; timestamp {bar.Timestamp:O}."),
            $"Raw OHLC: Open {Format(bar.Open, "R")}; High {Format(bar.High, "R")}; Low {Format(bar.Low, "R")}; Close {Format(bar.Close, "R")}."
        };
        if (volumeEnabled) lines.Add($"Raw Volume: {Format(bar.Volume, "R")}.");
        if (bandsEnabled)
            lines.Add($"Raw Bollinger: Upper {Format(bb?.Upper, "R")}; Middle {Format(bb?.Middle, "R")}; Lower {Format(bb?.Lower, "R")}; Standard deviation {Format(bb?.StandardDeviation, "R")}.");
        if (cciEnabled) lines.Add($"Raw CCI: {Format(cci?.Value, "R")}.");
        if (williamsREnabled) lines.Add($"Raw Williams %R: {Format(wr?.Value, "R")}.");
        if (aggregation is not null && segment is { } aggregate)
        {
            var original = aggregation.OriginalSource;
            var endIndex = aggregate.OriginalStartIndex + aggregate.OriginalCount - 1;
            var first = original.Bars[aggregate.OriginalStartIndex];
            var last = original.Bars[endIndex];
            lines.Add(FormattableString.Invariant($"Original source: {original.SeriesId}; dataset {original.DatasetVersion}; source {original.SourceVersion}."));
            var end = aggregate.BucketEndUtc is { } bucketEnd ? bucketEnd.ToString("O", Invariant) :
                $"UTC tick {aggregate.BucketEndUtcTicksExclusive.ToString(Invariant)} (beyond DateTimeOffset.MaxValue)";
            lines.Add(FormattableString.Invariant($"Aggregation: {PriceTimeAggregator.GetLabel(aggregation.Interval)} ({aggregation.Interval}); UTC bucket [{aggregate.BucketStartUtc:O}, {end}); end ticks exclusive {aggregate.BucketEndUtcTicksExclusive}."));
            lines.Add(FormattableString.Invariant($"Constituents: {aggregate.OriginalCount}; first index {aggregate.OriginalStartIndex}, key {first.Key}, timestamp {first.Timestamp:O}; last index {endIndex}, key {last.Key}, timestamp {last.Timestamp:O}."));
            lines.Add(FormattableString.Invariant($"Aggregate volume status: {aggregate.VolumeStatus}; missing constituents {aggregate.MissingVolumeCount}."));
        }
        return string.Join("\n", lines);
    }
}
