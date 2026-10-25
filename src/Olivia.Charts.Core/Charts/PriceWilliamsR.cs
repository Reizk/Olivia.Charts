using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>One complete trailing High/Low window, identified by its final original bar.</summary>
public readonly record struct PriceWilliamsRPoint(int BarIndex, long Key, DateTimeOffset Timestamp,
    double Value);

/// <summary>Viewport-independent Williams %R over the original bounded source. FlatWindowCount
/// counts complete windows whose highest High equals their lowest Low. Those undefined values
/// are omitted, without discarding history. Warm-up and explicit gaps do not count as flat windows.</summary>
public sealed record PriceWilliamsRSnapshot(PriceChartSource Source,
    ImmutableArray<PriceWilliamsRPoint> Points, int Period, int FlatWindowCount);

/// <summary>Williams %R = -100 * (highest High - final Close) / (highest High - lowest Low),
/// with mathematical domain [-100, 0]. Full-period warm-up, reset at explicit gaps and omission
/// of flat windows are compatibility choices, not a claim of exact DevExpress defaults.
/// Original OHLC bars are never sorted, filled, rewritten, resampled or cropped to a viewport.</summary>
public static class PriceWilliamsR
{
    public const int MinimumPeriod = 1;
    public const int MaximumPeriod = PriceChartSource.MaximumBars;

    public static PriceWilliamsRSnapshot Calculate(PriceChartSource source, int period = 14)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (period < MinimumPeriod || period > MaximumPeriod)
            throw new ArgumentOutOfRangeException(nameof(period));

        var points = ImmutableArray.CreateBuilder<PriceWilliamsRPoint>();
        var contiguous = 0;
        var flatWindowCount = 0;
        for (var i = 0; i < source.Bars.Length; i++)
        {
            var bar = source.Bars[i];
            if (bar.IsGap) { contiguous = 0; continue; }
            contiguous++;
            if (contiguous < period) continue;

            var highest = bar.High!.Value;
            var lowest = bar.Low!.Value;
            for (var j = i - period + 1; j < i; j++)
            {
                highest = Math.Max(highest, source.Bars[j].High!.Value);
                lowest = Math.Min(lowest, source.Bars[j].Low!.Value);
            }
            if (highest == lowest) { flatWindowCount++; continue; }

            points.Add(new(i, bar.Key, bar.Timestamp,
                CalculateValue(highest, lowest, bar.Close!.Value)));
        }
        return new(source, points.ToImmutable(), period, flatWindowCount);
    }

    private static double CalculateValue(double highest, double lowest, double close)
    {
        // Exact endpoints also avoid taking an exponent of an all-zero numerator.
        if (close == highest) return 0;
        if (close == lowest) return -100;

        // Normalize each subtraction independently by a power of two, retaining significand
        // bits even for adjacent extremes or subnormal inputs. Raw subtraction could overflow
        // for opposite finite extremes. A shared denominator scale could instead underflow
        // the numerator before multiplication by 100 makes its final result representable.
        var numeratorExponent = Math.ILogB(Math.Max(Math.Abs(highest), Math.Abs(close)));
        var denominatorExponent = Math.ILogB(Math.Max(Math.Abs(highest), Math.Abs(lowest)));
        var numerator = Math.ScaleB(highest, -numeratorExponent) -
            Math.ScaleB(close, -numeratorExponent);
        var denominator = Math.ScaleB(highest, -denominatorExponent) -
            Math.ScaleB(lowest, -denominatorExponent);
        var value = -Math.ScaleB(100 * (numerator / denominator),
            numeratorExponent - denominatorExponent);
        // Validated OHLC guarantees this domain; limit only any final endpoint roundoff.
        return Math.Clamp(value, -100, 0);
    }
}
