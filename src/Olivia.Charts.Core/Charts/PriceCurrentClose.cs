using System.Globalization;

namespace Olivia.Core.Charts;

public enum PriceCurrentCloseState { Empty, Gap, NoDomain, BudgetRefused, OutsideDomain, Visible }

/// <summary>The final original source row, not the last visible candle or an aggregate row.
/// OriginalHit retains the full original bar and identity even when no marker can be drawn.
/// ScreenY is present only for Visible, with zero at the top and one at the bottom.
/// AxisText is invariant F4 of the exact original Close, or null for Empty and Gap; it is
/// neither abbreviated nor a value to use for projection. Label fitting belongs to the UI.</summary>
public sealed class PriceCurrentCloseSnapshot
{
    internal PriceCurrentCloseSnapshot(PriceChartSource originalSource, int originalIndex,
        PriceChartHit? originalHit, PriceCurrentCloseState state, double? screenY, string? axisText)
    {
        OriginalSource = originalSource;
        OriginalIndex = originalIndex;
        OriginalHit = originalHit;
        State = state;
        ScreenY = screenY;
        AxisText = axisText;
    }

    public PriceChartSource OriginalSource { get; }
    public int OriginalIndex { get; }
    public PriceChartHit? OriginalHit { get; }
    public PriceCurrentCloseState State { get; }
    public double? ScreenY { get; }
    public string? AxisText { get; }
}

/// <summary>A bounded, immutable current-close annotation using the final original bar regardless
/// of viewport or time interval. A trailing gap is not searched backwards. The existing price
/// domain and budget are respected without extending the domain or clamping an off-scale close.
/// State precedence is Empty, Gap, BudgetRefused, NoDomain, OutsideDomain, then Visible.</summary>
public static class PriceCurrentClose
{
    public static PriceCurrentCloseSnapshot Create(PriceChartSnapshot prices,
        PriceTimeAggregationSnapshot? aggregation = null)
    {
        ArgumentNullException.ThrowIfNull(prices);
        if (aggregation is not null && !ReferenceEquals(prices.Source, aggregation.Source))
            throw new ArgumentException("Aggregation must belong to the queried price source.", nameof(aggregation));

        var original = aggregation?.OriginalSource ?? prices.Source;
        var index = original.Bars.Length - 1;
        if (index < 0)
            return new(original, -1, null, PriceCurrentCloseState.Empty, null, null);

        var bar = original.Bars[index];
        var hit = new PriceChartHit(original.DatasetVersion, original.SeriesId, original.SourceVersion, bar);
        if (bar.IsGap)
            return new(original, index, hit, PriceCurrentCloseState.Gap, null, null);

        var close = bar.Close!.Value;
        var text = close.ToString("F4", CultureInfo.InvariantCulture);
        if (prices.BudgetStatus != ChartBudgetStatus.Satisfied)
            return new(original, index, hit, PriceCurrentCloseState.BudgetRefused, null, text);
        if (prices.Domain is not { } domain)
            return new(original, index, hit, PriceCurrentCloseState.NoDomain, null, text);
        if (close < domain.Minimum || close > domain.Maximum)
            return new(original, index, hit, PriceCurrentCloseState.OutsideDomain, null, text);

        // Match price candles: reversing the raw domain avoids cancellation from
        // 1 - NormalizeY(close, domain) near the upper boundary, including subnormals.
        var screenY = DualAxisChartGeometry.NormalizeY(-close,
            new HistoryValueDomain(-domain.Maximum, -domain.Minimum));
        return new(original, index, hit, PriceCurrentCloseState.Visible, screenY, text);
    }
}
