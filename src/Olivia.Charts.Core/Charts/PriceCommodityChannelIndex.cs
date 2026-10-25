using System.Collections.Immutable;
using System.Numerics;

namespace Olivia.Core.Charts;

/// <summary>One complete trailing typical-price window, identified by its final original bar.</summary>
public readonly record struct PriceCciPoint(int BarIndex, long Key, DateTimeOffset Timestamp,
    double Value);

/// <summary>Viewport-independent CCI over the original bounded source. FlatWindowCount counts
/// complete windows with zero mean absolute deviation in typical price. Undefined values are
/// omitted without discarding history; warm-up and explicit gaps do not count as flat windows.</summary>
public sealed record PriceCciSnapshot(PriceChartSource Source, ImmutableArray<PriceCciPoint> Points,
    int Period, int FlatWindowCount);

/// <summary>CCI = (final typical price - SMA) / (0.015 * mean absolute deviation), with
/// typical price = (High + Low + Close) / 3. Full-period warm-up, reset at explicit gaps and
/// omission of flat windows are compatibility choices, not verified DevExpress defaults.
/// Values are not clamped to display bounds or guides. Original OHLC bars are never sorted,
/// filled, rewritten, resampled or cropped to a viewport.</summary>
public static class PriceCommodityChannelIndex
{
    public const int MinimumPeriod = 1;
    public const int MaximumPeriod = PriceChartSource.MaximumBars;

    public static PriceCciSnapshot Calculate(PriceChartSource source, int period = 14)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (period < MinimumPeriod || period > MaximumPeriod)
            throw new ArgumentOutOfRangeException(nameof(period));

        var points = ImmutableArray.CreateBuilder<PriceCciPoint>();
        // Cancel a common power of two before constructing exact integers. Ordinary prices
        // then use about 53 bits instead of carrying over a thousand trailing zero bits.
        // Mixed extreme/subnormal sources still fall back to the full exact binary unit.
        var commonShift = 2046;
        var maximumShift = 0;
        foreach (var bar in source.Bars)
        {
            if (bar.IsGap) continue;
            Include(bar.High!.Value); Include(bar.Low!.Value); Include(bar.Close!.Value);
        }
        void Include(double value)
        {
            var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value)) & 0x7fff_ffff_ffff_ffffUL;
            if (bits != 0)
            {
                var shift = Math.Max(0, (int)(bits >> 52) - 1);
                commonShift = Math.Min(commonShift, shift);
                maximumShift = Math.Max(maximumShift, shift);
            }
        }
        if (maximumShift - commonShift <= 24)
            return CalculateNarrowExponentRange(source, period, commonShift);
        var typicalSums = new BigInteger[source.Bars.Length];
        var sum = BigInteger.Zero;
        var contiguous = 0;
        var flatWindowCount = 0;
        for (var i = 0; i < source.Bars.Length; i++)
        {
            var bar = source.Bars[i];
            if (bar.IsGap) { contiguous = 0; sum = BigInteger.Zero; continue; }

            // Finite doubles are exact integer multiples of 2^-1074. Retain that integer
            // until the final ratio: even a scaled floating-point H+L+C can erase a tiny
            // Close when High and Low are opposite extremes, inventing a flat window.
            var typicalSum = ToBinaryUnits(bar.High!.Value, commonShift) + ToBinaryUnits(bar.Low!.Value, commonShift) +
                ToBinaryUnits(bar.Close!.Value, commonShift);
            typicalSums[i] = typicalSum;
            sum += typicalSum;
            contiguous++;
            if (contiguous > period) sum -= typicalSums[i - period];
            if (contiguous < period) continue;

            var absoluteDeviations = BigInteger.Zero;
            for (var j = i - period + 1; j <= i; j++)
                absoluteDeviations += BigInteger.Abs(period * typicalSums[j] - sum);
            if (absoluteDeviations.IsZero) { flatWindowCount++; continue; }

            // With T = H+L+C and S = sum(T), the factors of three in typical price and
            // the binary unit cancel. Use 0.015 = 3/200 without rounding the mean or TP:
            // CCI = 200*n*(n*Tlast-S) / (3*sum(abs(n*Ti-S))).
            var numerator = 200 * period * (period * typicalSum - sum);
            var denominator = 3 * absoluteDeviations;
            points.Add(new(i, bar.Key, bar.Timestamp, DivideToDouble(numerator, denominator)));
        }
        return new(source, points.ToImmutable(), period, flatWindowCount);
    }

    private static PriceCciSnapshot CalculateNarrowExponentRange(PriceChartSource source, int period, int commonShift)
    {
        // Each scaled input uses at most 77 magnitude bits (53 + 24). H+L+C uses <79,
        // a window sum <94, summed deviations <110, and the CCI numerator <118.
        // MaximumPeriod <= 18,000 therefore keeps every operation exact in Int128.
        // Only the final rational rounding uses the shared arbitrary-precision routine.
        var typicalSums = new Int128[source.Bars.Length];
        Int128 sum = 0;
        var contiguous = 0;
        var flat = 0;
        var points = ImmutableArray.CreateBuilder<PriceCciPoint>();
        for (var i = 0; i < source.Bars.Length; i++)
        {
            var bar = source.Bars[i];
            if (bar.IsGap) { contiguous = 0; sum = 0; continue; }
            var typical = Units(bar.High!.Value) + Units(bar.Low!.Value) + Units(bar.Close!.Value);
            typicalSums[i] = typical;
            sum += typical;
            if (++contiguous > period) sum -= typicalSums[i - period];
            if (contiguous < period) continue;
            Int128 deviations = 0;
            for (var j = i - period + 1; j <= i; j++)
                deviations += Int128.Abs(period * typicalSums[j] - sum);
            if (deviations == 0) { flat++; continue; }
            var numerator = 200 * (Int128)period * (period * typical - sum);
            points.Add(new(i, bar.Key, bar.Timestamp,
                DivideToDouble((BigInteger)numerator, (BigInteger)(3 * deviations))));
        }
        return new(source, points.ToImmutable(), period, flat);

        Int128 Units(double value)
        {
            var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
            var exponent = (int)((bits >> 52) & 0x7ff);
            var significand = bits & 0x000f_ffff_ffff_ffffUL;
            if (exponent != 0) significand |= 1UL << 52;
            if (significand == 0) return 0;
            var units = (Int128)significand << (Math.Max(0, exponent - 1) - commonShift);
            return (bits & (1UL << 63)) == 0 ? units : -units;
        }
    }

    private static BigInteger ToBinaryUnits(double value, int commonShift)
    {
        var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        var exponent = (int)((bits >> 52) & 0x7ff);
        var significand = bits & 0x000f_ffff_ffff_ffffUL;
        if (exponent != 0) significand |= 1UL << 52;
        if (significand == 0) return BigInteger.Zero;
        var units = new BigInteger(significand) << (Math.Max(0, exponent - 1) - commonShift);
        return (bits & (1UL << 63)) == 0 ? units : -units;
    }

    private static double DivideToDouble(BigInteger numerator, BigInteger denominator)
    {
        if (numerator.IsZero) return 0;
        var negative = numerator.Sign < 0;
        numerator = BigInteger.Abs(numerator);

        // Locate the ratio's leading bit without converting either potentially enormous
        // integer to double. The centered deviations sum to zero, so |CCI| <= 100*n/3;
        // every defined result is finite for this source's bounded period.
        var exponent = (int)(numerator.GetBitLength() - denominator.GetBitLength());
        var belowPower = exponent >= 0
            ? numerator < (denominator << exponent)
            : (numerator << -exponent) < denominator;
        if (belowPower) exponent--;

        // Round exactly once, to nearest/even, including the subnormal boundary. Using
        // separately rounded floating-point quotients could erase a representable tiny
        // result or double-round it. Applying the sign last also preserves negative zero
        // when a genuinely negative result is smaller than the representable range.
        var unitExponent = Math.Max(exponent - 52, -1074);
        if (unitExponent < 0) numerator <<= -unitExponent;
        else denominator <<= unitExponent;
        var significand = BigInteger.DivRem(numerator, denominator, out var remainder);
        var rounding = (2 * remainder).CompareTo(denominator);
        if (rounding > 0 || (rounding == 0 && !significand.IsEven)) significand++;
        var magnitude = Math.ScaleB((double)significand, unitExponent);
        return negative ? -magnitude : magnitude;
    }
}
