using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Olivia.Core.Charts;

/// <summary>Supported finite static UTC bucket durations. These are not live-update settings.</summary>
public enum PriceTimeInterval { Minute1, Minute5, Minute15, Minute30, Hour1, Hour4 }

/// <summary>Missing takes precedence over overflow of the known subtotal. Gap denotes an
/// explicit original OHLC gap, not an empty calendar interval.</summary>
public enum PriceAggregateVolumeStatus { Complete, Missing, Overflow, Gap }

/// <summary>A contiguous original-source range represented by one derived bar. Bucket bounds
/// are UTC and half-open; the bar retains its first original key and timestamp, including offset.
/// An explicit gap has OriginalCount = 1, MissingVolumeCount = 1 and the unchanged original bar.
/// Separate runs in one bucket remain separate when an explicit gap intervenes.</summary>
public readonly record struct PriceTimeAggregateSegment(int OriginalStartIndex, int OriginalCount,
    DateTimeOffset BucketStartUtc, long BucketEndUtcTicksExclusive, PriceBar Bar,
    PriceAggregateVolumeStatus VolumeStatus, int MissingVolumeCount)
{
    /// <summary>Null only when the exclusive end is beyond DateTimeOffset.MaxValue. The tick
    /// bound remains exact, including the conceptual midnight after 9999-12-31.</summary>
    public DateTimeOffset? BucketEndUtc => BucketEndUtcTicksExclusive <= DateTimeOffset.MaxValue.UtcTicks
        ? new DateTimeOffset(BucketEndUtcTicksExclusive, TimeSpan.Zero) : null;
}

/// <summary>Full-source aggregation, independent of any later ordinal viewport. OriginalSource
/// retains every immutable original row and its identity; each segment maps exactly back to it.
/// Source has a distinct bounded derived identity and preserves both original version numbers.
/// To change intervals, aggregate OriginalSource again, not the already aggregated Source.</summary>
public sealed class PriceTimeAggregationSnapshot
{
    internal PriceTimeAggregationSnapshot(PriceChartSource originalSource, PriceChartSource source,
        PriceTimeInterval interval, ImmutableArray<PriceTimeAggregateSegment> segments)
    {
        OriginalSource = originalSource;
        Source = source;
        Interval = interval;
        Segments = segments;
    }

    public PriceChartSource OriginalSource { get; }
    public PriceChartSource Source { get; }
    public PriceTimeInterval Interval { get; }
    public ImmutableArray<PriceTimeAggregateSegment> Segments { get; }
}

/// <summary>Bounded static Financial OHLC (first Open, maximum High, minimum Low, last Close)
/// and Sum volume aggregation. UTC tick-floor buckets divide each UTC day exactly. No sorting,
/// empty-bucket insertion, gap collapse, clamping, imputation, annotations or live semantics.
/// Output contains at most as many bars as the original source (at most 18,000).</summary>
public static class PriceTimeAggregator
{
    private const ulong FractionMask = 0x000f_ffff_ffff_ffffUL;
    private const ulong MaximumExactInteger = 1UL << 53;
    private static readonly BigInteger MaximumVolumeUnits = ToBinaryUnits(double.MaxValue);

    public static TimeSpan GetDuration(PriceTimeInterval interval) => interval switch
    {
        PriceTimeInterval.Minute1 => TimeSpan.FromMinutes(1),
        PriceTimeInterval.Minute5 => TimeSpan.FromMinutes(5),
        PriceTimeInterval.Minute15 => TimeSpan.FromMinutes(15),
        PriceTimeInterval.Minute30 => TimeSpan.FromMinutes(30),
        PriceTimeInterval.Hour1 => TimeSpan.FromHours(1),
        PriceTimeInterval.Hour4 => TimeSpan.FromHours(4),
        _ => throw new ArgumentOutOfRangeException(nameof(interval))
    };

    public static string GetLabel(PriceTimeInterval interval) => interval switch
    {
        PriceTimeInterval.Minute1 => "1m",
        PriceTimeInterval.Minute5 => "5m",
        PriceTimeInterval.Minute15 => "15m",
        PriceTimeInterval.Minute30 => "30m",
        PriceTimeInterval.Hour1 => "1h",
        PriceTimeInterval.Hour4 => "4h",
        _ => throw new ArgumentOutOfRangeException(nameof(interval))
    };

    public static PriceTimeAggregationSnapshot Aggregate(PriceChartSource source, PriceTimeInterval interval)
    {
        ArgumentNullException.ThrowIfNull(source);
        var durationTicks = GetDuration(interval).Ticks;
        var label = GetLabel(interval);
        var segments = ImmutableArray.CreateBuilder<PriceTimeAggregateSegment>();
        var derivedBars = ImmutableArray.CreateBuilder<PriceBar>();
        var index = 0;
        while (index < source.Bars.Length)
        {
            var first = source.Bars[index];
            var bucketStartTicks = first.Timestamp.UtcTicks / durationTicks * durationTicks;
            // Even at DateTimeOffset.MaxValue this is within Int64; do not clip the
            // exclusive bound or construct an out-of-range DateTimeOffset.
            var bucketEndTicks = bucketStartTicks + durationTicks;
            var bucketStart = new DateTimeOffset(bucketStartTicks, TimeSpan.Zero);
            if (first.IsGap)
            {
                segments.Add(new(index, 1, bucketStart, bucketEndTicks, first,
                    PriceAggregateVolumeStatus.Gap, 1));
                derivedBars.Add(first);
                index++;
                continue;
            }

            var start = index;
            var high = first.High!.Value;
            var low = first.Low!.Value;
            var close = first.Close;
            var volumeUnits = BigInteger.Zero;
            ulong integerVolume = 0;
            var binaryVolume = false;
            var missingVolumeCount = 0;
            while (index < source.Bars.Length)
            {
                var bar = source.Bars[index];
                if (bar.IsGap || bar.Timestamp.UtcTicks >= bucketEndTicks) break;
                // Strict comparisons retain the first selected value's bits on ties,
                // notably +0 versus -0. Open and Close are copied, not calculated.
                if (bar.High!.Value > high) high = bar.High.Value;
                if (bar.Low!.Value < low) low = bar.Low.Value;
                close = bar.Close;
                if (bar.Volume is { } volume)
                {
                    // The official history and live generator use small integral volumes.
                    // Accumulate these exactly without allocating a 2^-1074 BigInteger per row.
                    // A fraction, large value or excessive total promotes once to the original
                    // exact binary accumulator, preserving ties, overflow and subnormal semantics.
                    if (!binaryVolume && volume <= MaximumExactInteger && volume == Math.Truncate(volume) &&
                        integerVolume <= MaximumExactInteger - (ulong)volume)
                        integerVolume += (ulong)volume;
                    else
                    {
                        if (!binaryVolume) { volumeUnits = ToBinaryUnits(integerVolume); binaryVolume = true; }
                        volumeUnits += ToBinaryUnits(volume);
                    }
                }
                else missingVolumeCount++;
                index++;
            }

            double? aggregateVolume = null;
            PriceAggregateVolumeStatus volumeStatus;
            if (missingVolumeCount > 0)
                volumeStatus = PriceAggregateVolumeStatus.Missing;
            else if (binaryVolume && volumeUnits > MaximumVolumeUnits)
                volumeStatus = PriceAggregateVolumeStatus.Overflow;
            else
            {
                volumeStatus = PriceAggregateVolumeStatus.Complete;
                aggregateVolume = binaryVolume ? FromBinaryUnits(volumeUnits) : integerVolume;
            }
            var aggregate = new PriceBar(first.Key, first.Timestamp, first.Open, high, low,
                close, aggregateVolume);
            segments.Add(new(start, index - start, bucketStart, bucketEndTicks, aggregate,
                volumeStatus, missingVolumeCount));
            derivedBars.Add(aggregate);
        }

        // Neither the original identity nor label has room reserved for a suffix. Hash
        // UTF-8 identity instead and keep the label short; provenance stays in the snapshot.
        var identity = $"agg:{label}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.SeriesId)))}";
        if (identity == source.SeriesId) identity = $"derived:{identity}";
        var derived = new PriceChartSource(identity, $"Aggregated {label}", derivedBars.ToImmutable(),
            source.DatasetVersion, source.SourceVersion);
        return new(source, derived, interval, segments.ToImmutable());
    }

    private static BigInteger ToBinaryUnits(double value)
    {
        // Every admitted volume is a nonnegative finite integer multiple of 2^-1074.
        // A maximum of 18,000 inputs bounds the exact accumulator to 2113 bits. The sign
        // of raw zero is intentionally ignored; the original source preserves its bits.
        var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        var exponent = (int)((bits >> 52) & 0x7ff);
        var significand = bits & FractionMask;
        if (exponent != 0) significand |= 1UL << 52;
        return new BigInteger(significand) << Math.Max(0, exponent - 1);
    }

    private static double FromBinaryUnits(BigInteger units)
    {
        // The caller has already rejected exact mathematical sums above MaxValue,
        // including MaxValue + Epsilon, which ordinary floating addition would hide.
        if (units.IsZero) return 0d;
        if (units.GetBitLength() <= 52)
            return BitConverter.Int64BitsToDouble((long)units);

        var shift = (int)units.GetBitLength() - 53;
        var significand = (ulong)(units >> shift);
        if (shift > 0)
        {
            var remainder = units - (new BigInteger(significand) << shift);
            var half = BigInteger.One << (shift - 1);
            if (remainder > half || (remainder == half && (significand & 1) != 0)) significand++;
            if (significand == 1UL << 53)
            {
                significand >>= 1;
                shift++;
            }
        }
        var bits = ((ulong)(shift + 1) << 52) | (significand & FractionMask);
        return BitConverter.Int64BitsToDouble((long)bits);
    }
}
