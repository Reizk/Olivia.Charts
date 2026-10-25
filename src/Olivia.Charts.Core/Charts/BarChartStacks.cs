using System.Collections.Immutable;
using System.Numerics;

namespace Olivia.Core.Charts;

public enum BarChartStackStatus { Ready, WorkBudgetExceeded, GeometryBudgetExceeded, ArithmeticOverflow }

/// <summary>One original contribution. Endpoints are cumulative display values; Share is signed
/// percentage only for FullStacked. Raw value and signed zero remain in Hit, never aggregated away.</summary>
public readonly record struct BarChartContribution(BarChartHit Hit, int CategoryIndex, int SeriesIndex,
    double Start, double End, double? Share);

public sealed partial class BarChartSource
{
    /// <summary>Abstract bounded admission score, not CPU instructions or measured BigInteger work.</summary>
    public const int MaximumStackWork = 262144;
    private static readonly BigInteger StackUnitDenominator = BigInteger.One << 1074;
    private static readonly BigInteger StackMaximumFinite = StackMagnitude(double.MaxValue);

    private BarChartSnapshot QueryStack(BarChartOptions options)
    {
        var start = Math.Min(options.CategoryStart, Categories.Length);
        var count = Math.Min(options.CategoryCount, Categories.Length - start);
        BarChartSnapshot Refused(BarChartStackStatus status, int? work, int bars = 0, HistoryValueDomain? domain = null)
            => new(this, options, start, count, domain, [], [], bars, status, [], work);

        // The first pass is charged before reading raw entries. No exact arithmetic/geometry yet.
        var baseWork = checked(RawEntryCount + count * 16 + Series.Length * 8);
        if (baseWork > options.StackWorkBudget)
            return Refused(BarChartStackStatus.WorkBudgetExceeded, null);
        var buckets = new List<(BarChartHit Hit, int Series)>?[count];
        var gaps = ImmutableArray.CreateBuilder<BarChartHit>();
        var finite = 0;
        for (var s = 0; s < Series.Length; s++)
            foreach (var sample in Series[s].Samples)
            {
                var category = _categoryIndices[sample.CategoryKey];
                if (category < start || category >= start + count) continue;
                var hit = new BarChartHit(DatasetVersion, Series[s].Id, Series[s].SourceVersion, sample, Categories[category]);
                if (sample.IsGap) { gaps.Add(hit); continue; }
                (buckets[category - start] ??= []).Add((hit, s));
                finite++;
            }
        var work = checked(baseWork + finite * 96);
        if (work > options.StackWorkBudget) return Refused(BarChartStackStatus.WorkBudgetExceeded, work);

        // All following passes, exact operations and copies are included in the fixed preflight.
        // Source caps prove sums <=2109 bits; percent numerator <=2116 and shifted dividend <=3190.
        var positive = new BigInteger[count];
        var negative = new BigInteger[count];
        for (var c = 0; c < count; c++)
            if (buckets[c] is { } bucket)
                foreach (var entry in bucket)
                {
                    var value = entry.Hit.Value!.Value;
                    if (value > 0) positive[c] += StackMagnitude(value);
                    else if (value < 0) negative[c] += StackMagnitude(value);
                }
        var full = options.Layout == BarChartLayout.FullStacked;
        var minimum = 0d; var maximum = 0d;
        for (var c = 0; c < count; c++)
        {
            if (!full && (positive[c] > StackMaximumFinite || negative[c] > StackMaximumFinite))
                return Refused(BarChartStackStatus.ArithmeticOverflow, work);
            maximum = Math.Max(maximum, full ? positive[c].IsZero ? 0 : 100 : StackRound(positive[c], StackUnitDenominator));
            minimum = Math.Min(minimum, full ? negative[c].IsZero ? 0 : -100 : -StackRound(negative[c], StackUnitDenominator));
        }
        HistoryValueDomain? domain = count == 0 ? null : options.FixedDomain ??
            (finite == 0 ? null : new HistoryValueDomain(minimum, maximum));
        var contributions = ImmutableArray.CreateBuilder<BarChartContribution>(finite);
        var bars = ImmutableArray.CreateBuilder<BarChartBar>();
        for (var c = 0; c < count; c++)
        {
            var p = BigInteger.Zero; var n = BigInteger.Zero;
            var previousPositive = 0d; var previousNegative = 0d;
            if (buckets[c] is not { } bucket) continue;
            foreach (var entry in bucket)
            {
                var value = entry.Hit.Value!.Value;
                var magnitude = StackMagnitude(value);
                double from; double to; double? share = full ? 0d : null;
                if (value > 0)
                {
                    from = previousPositive; p += magnitude;
                    to = full ? StackRound(100 * p, positive[c]) : StackRound(p, StackUnitDenominator);
                    previousPositive = to;
                    if (full) share = StackRound(100 * magnitude, positive[c]);
                }
                else if (value < 0)
                {
                    from = previousNegative; n += magnitude;
                    to = full ? -StackRound(100 * n, negative[c]) : -StackRound(n, StackUnitDenominator);
                    previousNegative = to;
                    if (full) share = -StackRound(100 * magnitude, negative[c]);
                }
                else { from = 0; to = 0; }
                contributions.Add(new(entry.Hit, c + start, entry.Series, from, to, share));
                if (domain is not { } y) continue;
                var low = Math.Max(Math.Min(from, to), y.Minimum);
                var high = Math.Min(Math.Max(from, to), y.Maximum);
                if (low > high) continue;
                var top = 1 - DualAxisChartGeometry.NormalizeY(high, y);
                var bottom = 1 - DualAxisChartGeometry.NormalizeY(low, y);
                bars.Add(new(entry.Hit, c + start, entry.Series, (c + 0.1) / count, top,
                    0.8 / count, Math.Max(0, bottom - top)));
            }
        }
        if (bars.Count > options.GeometryBudget)
            return Refused(BarChartStackStatus.GeometryBudgetExceeded, work, bars.Count, domain);
        return new(this, options, start, count, domain, bars.ToImmutable(), gaps.ToImmutable(), bars.Count,
            BarChartStackStatus.Ready, contributions.ToImmutable(), work);
    }

    // Exact nonnegative magnitude in units of 2^-1074; inputs are already finite admitted samples.
    private static BigInteger StackMagnitude(double value)
    {
        var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value)) & 0x7fff_ffff_ffff_ffffUL;
        var exponent = (int)(bits >> 52);
        var significand = bits & 0x000f_ffff_ffff_ffffUL;
        if (exponent == 0) return new BigInteger(significand);
        return new BigInteger(significand | (1UL << 52)) << (exponent - 1);
    }

    // Single nearest-even rounding; notably 100*n/d is rounded directly, never round(n/d)*100.
    private static double StackRound(BigInteger numerator, BigInteger denominator)
    {
        if (numerator.IsZero) return 0;
        var exponent = checked((int)(numerator.GetBitLength() - denominator.GetBitLength()));
        if (exponent >= 0 ? numerator < (denominator << exponent) : (numerator << -exponent) < denominator) exponent--;
        var shift = Math.Max(-1074, exponent - 52);
        var scaledNumerator = shift < 0 ? numerator << -shift : numerator;
        var scaledDenominator = shift > 0 ? denominator << shift : denominator;
        var quotient = BigInteger.DivRem(scaledNumerator, scaledDenominator, out var remainder);
        var comparison = (remainder << 1).CompareTo(scaledDenominator);
        if (comparison > 0 || (comparison == 0 && !quotient.IsEven)) quotient++;
        return Math.ScaleB((double)quotient, shift);
    }
}
