using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>One volume column at a bar's shared ordinal or time X position. The hit retains
/// the complete original bar and source identity, including missing versus known-zero volume.</summary>
public readonly record struct PriceVolumeColumn(int BarIndex, PriceChartHit Hit, PriceChartBody Body);

/// <summary>Independent zero-based linear volume projection for the price viewport. MissingVolumeCount
/// counts visible null volumes, including explicit OHLC gaps. RequiredColumnCount counts visible
/// known volumes, including zeros, before the price frame's all-or-nothing budget decision. It is
/// informative only: the existing price geometry budget still measures candles, not combined primitives.
/// MissingVolumeCount + RequiredColumnCount equals Prices.BarCount; each is bounded by MaximumBars.</summary>
public sealed record PriceVolumeSnapshot(PriceChartSnapshot Prices, HistoryValueDomain? Domain,
    ImmutableArray<PriceVolumeColumn> Columns, int RequiredColumnCount, int MissingVolumeCount)
{
    public PriceChartSource Source => Prices.Source;
}

/// <summary>Finite, bounded volume bars with width .2 of one X unit. Price Y
/// clipping does not filter volumes or determine their domain. Optional absolute headroom
/// retains the nonnegative zero baseline. No minimum height,
/// imputation, resampling, aggregation or live-update semantics are introduced.</summary>
public static class PriceVolumeGeometry
{
    public static PriceVolumeSnapshot Create(PriceChartSnapshot prices) => Create(prices, 0);

    public static PriceVolumeSnapshot Create(PriceChartSnapshot prices, double sideMargin)
    {
        ArgumentNullException.ThrowIfNull(prices);
        if (!double.IsFinite(sideMargin) || sideMargin < 0)
            throw new ArgumentOutOfRangeException(nameof(sideMargin));
        var required = 0;
        var missing = 0;
        var maximum = 0d;
        var end = prices.BarStart + prices.BarCount;
        for (var i = prices.BarStart; i < end; i++)
        {
            var bar = prices.Source.Bars[i];
            if (bar.Volume is not { } volume) { missing++; continue; }
            // PriceBar requires explicit OHLC gaps to carry null volume.
            required++;
            maximum = Math.Max(maximum, volume);
        }

        // The official value-axis correction restores the zero end after applying margins.
        // Only the upper end expands for nonnegative volume, including all-zero data.
        var displayMaximum = Math.Min(double.MaxValue, maximum + sideMargin);
        HistoryValueDomain? domain = required == 0 ? null : new HistoryValueDomain(0, displayMaximum);
        var columns = ImmutableArray.CreateBuilder<PriceVolumeColumn>();
        if (prices.BudgetStatus == ChartBudgetStatus.Satisfied && required > 0)
        {
            for (var i = prices.BarStart; i < end; i++)
            {
                var bar = prices.Source.Bars[i];
                if (bar.IsGap || bar.Volume is not { } volume) continue;
                // Direct ratios avoid overflow for MaxValue and preserve ratios of subnormals.
                // Zero values remain actual zero-height columns even with positive headroom.
                var height = displayMaximum > 0 ? volume / displayMaximum : 0;
                // Subtract before division for Top: 1 - height loses near-top precision.
                var top = displayMaximum > 0 ? (displayMaximum - volume) / displayMaximum : 1;
                var center = prices.GetNormalizedX(i);
                var left = Math.Max(0, center - .1 * prices.XUnitWidth);
                var right = Math.Min(1, center + .1 * prices.XUnitWidth);
                var body = prices.Options.TimeViewport is null
                    ? new PriceChartBody((i - prices.BarStart + .4) / prices.BarCount, top, .2 / prices.BarCount, height)
                    : new PriceChartBody(left, top, Math.Max(0, right - left), height);
                var hit = new PriceChartHit(prices.Source.DatasetVersion, prices.Source.SeriesId,
                    prices.Source.SourceVersion, bar);
                columns.Add(new(i, hit, body));
            }
        }
        return new(prices, domain, columns.ToImmutable(), required, missing);
    }

    /// <summary>Tests only the actual positive-height column rectangle, without tolerance.
    /// Zero columns, missing volumes, gaps and refused frames cannot hit. Coordinates outside
    /// the viewport never hit; last drawn wins if rectangles overlap.</summary>
    public static PriceChartHit? HitTest(PriceVolumeSnapshot snapshot, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (x < 0 || x > 1 || y < 0 || y > 1) return null;
        for (var i = snapshot.Columns.Length - 1; i >= 0; i--)
        {
            var column = snapshot.Columns[i];
            var body = column.Body;
            if (body.Height <= 0 || body.Width <= 0) continue;
            if (x >= body.Left && x <= body.Left + body.Width &&
                y >= body.Top && y <= body.Top + body.Height)
                return column.Hit;
        }
        return null;
    }
}
