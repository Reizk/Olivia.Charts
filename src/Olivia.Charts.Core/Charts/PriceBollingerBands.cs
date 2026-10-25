using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>One complete trailing Close window, identified by its final original bar.</summary>
public readonly record struct PriceBollingerPoint(int BarIndex, long Key, DateTimeOffset Timestamp,
    double Middle, double Upper, double Lower, double StandardDeviation);

/// <summary>Viewport-independent bands over the original bounded source. OverflowCount counts
/// complete windows whose band results cannot be represented as finite doubles; warm-up and gaps
/// are not overflows. A rejected window does not discard history for the next window.</summary>
public sealed record PriceBollingerSnapshot(PriceChartSource Source,
    ImmutableArray<PriceBollingerPoint> Points, int Period, double StandardDeviationMultiplier,
    int OverflowCount);

/// <summary>Bollinger bands using an SMA and population standard deviation (divisor n).
/// Close input, full-period warm-up and reset at explicit gaps are compatibility choices,
/// not a claim of exact DevExpress defaults. No values are filled, clamped or resampled.</summary>
public static class PriceBollingerBands
{
    public const int MinimumPeriod = 2;
    public const int MaximumPeriod = PriceChartSource.MaximumBars;
    public const double MaximumStandardDeviationMultiplier = 10;

    public static PriceBollingerSnapshot Calculate(PriceChartSource source, int period = 20,
        double standardDeviationMultiplier = 2)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (period < MinimumPeriod || period > MaximumPeriod)
            throw new ArgumentOutOfRangeException(nameof(period));
        if (!double.IsFinite(standardDeviationMultiplier) || standardDeviationMultiplier < 0 ||
            standardDeviationMultiplier > MaximumStandardDeviationMultiplier)
            throw new ArgumentOutOfRangeException(nameof(standardDeviationMultiplier));

        var points = ImmutableArray.CreateBuilder<PriceBollingerPoint>();
        var contiguous = 0;
        var overflowCount = 0;
        for (var i = 0; i < source.Bars.Length; i++)
        {
            var bar = source.Bars[i];
            if (bar.IsGap) { contiguous = 0; continue; }
            contiguous++;
            if (contiguous < period) continue;
            if (TryCalculateWindow(source.Bars, i - period + 1, period, standardDeviationMultiplier,
                out var middle, out var upper, out var lower, out var standardDeviation))
                points.Add(new(i, bar.Key, bar.Timestamp, middle, upper, lower, standardDeviation));
            else
                overflowCount++;
        }
        return new(source, points.ToImmutable(), period, standardDeviationMultiplier, overflowCount);
    }

    private static bool TryCalculateWindow(ImmutableArray<PriceBar> bars, int start, int period,
        double multiplier, out double middle, out double upper, out double lower,
        out double standardDeviation)
    {
        var first = bars[start].Close!.Value;
        var constant = true;
        var maximumMagnitude = Math.Abs(first);
        for (var j = start + 1; j < start + period; j++)
        {
            var value = bars[j].Close!.Value;
            constant &= value == first;
            maximumMagnitude = Math.Max(maximumMagnitude, Math.Abs(value));
        }
        // In particular, +/-MaxValue constants must not overflow during summation, and
        // constants must not acquire a fictitious deviation through mean rounding.
        if (constant)
        {
            middle = upper = lower = first;
            standardDeviation = 0;
            return true;
        }

        // A power-of-two scale retains significand bits, unlike division by an arbitrary
        // maximum. Scaled magnitudes are <= 2 even at either finite double extreme.
        var exponent = Math.ILogB(maximumMagnitude);
        var sum = new CompensatedSum();
        for (var j = start; j < start + period; j++)
            sum.Add(Math.ScaleB(bars[j].Close!.Value, -exponent));
        var mean = sum.Value / period;

        var squaredDeviations = new CompensatedSum();
        var deviations = new CompensatedSum();
        for (var j = start; j < start + period; j++)
        {
            var deviation = Math.ScaleB(bars[j].Close!.Value, -exponent) - mean;
            squaredDeviations.Add(deviation * deviation);
            deviations.Add(deviation);
        }
        // Correct the second pass for rounding in the first-pass mean. This matters for
        // closely spaced large values, whose exact mean may lie between adjacent doubles.
        var meanDeviation = deviations.Value / period;
        var variance = Math.FusedMultiplyAdd(-meanDeviation, meanDeviation,
            squaredDeviations.Value / period);
        var deviationScale = Math.Sqrt(Math.Max(0, variance));
        middle = Math.ScaleB(mean, exponent);
        standardDeviation = Math.ScaleB(deviationScale, exponent);
        // Combine in scaled space before restoring magnitude. Multiplying the raw sigma
        // first could overflow even when addition/cancellation would yield a finite band.
        upper = Math.ScaleB(Math.FusedMultiplyAdd(multiplier, deviationScale, mean), exponent);
        lower = Math.ScaleB(Math.FusedMultiplyAdd(-multiplier, deviationScale, mean), exponent);
        return double.IsFinite(middle) && double.IsFinite(standardDeviation) &&
            double.IsFinite(upper) && double.IsFinite(lower);
    }

    private struct CompensatedSum
    {
        private double sum;
        private double correction;
        public readonly double Value => sum + correction;

        public void Add(double value)
        {
            var next = sum + value;
            correction += Math.Abs(sum) >= Math.Abs(value)
                ? (sum - next) + value
                : (value - next) + sum;
            sum = next;
        }
    }
}
