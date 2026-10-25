using System.Numerics;

namespace Olivia.Core.Charts;

/// <summary>A UTC tick interval [StartTicks, EndTicks). Empty windows are valid.</summary>
public readonly record struct TimeWindow
{
    // One tick beyond DateTime.MaxValue permits an inclusive last representable sample.
    public const long MaximumExclusiveTicks = 3155378976000000000L;

    public TimeWindow(long startTicks, long endTicks)
    {
        if (startTicks < 0 || startTicks > MaximumExclusiveTicks)
            throw new ArgumentOutOfRangeException(nameof(startTicks));
        if (endTicks < startTicks || endTicks > MaximumExclusiveTicks)
            throw new ArgumentOutOfRangeException(nameof(endTicks));
        StartTicks = startTicks;
        EndTicks = endTicks;
    }

    public long StartTicks { get; }
    public long EndTicks { get; }
    public long DurationTicks => EndTicks - StartTicks;
    public bool IsEmpty => StartTicks == EndTicks;
    public bool Contains(long utcTicks) => utcTicks >= StartTicks && utcTicks < EndTicks;

    /// <summary>Preserves duration where possible, translating into the source extent.</summary>
    public TimeWindow ClampTo(TimeWindow extent)
    {
        if (DurationTicks >= extent.DurationTicks)
            return extent;
        var start = Math.Clamp(StartTicks, extent.StartTicks, extent.EndTicks - DurationTicks);
        return new TimeWindow(start, start + DurationTicks);
    }

    public TimeWindow Pan(long deltaTicks, TimeWindow extent)
    {
        var current = ClampTo(extent);
        // Decimal avoids overflowing even for long.MinValue and long.MaxValue deltas.
        var start = (long)Math.Clamp((decimal)current.StartTicks + deltaTicks,
            extent.StartTicks, extent.EndTicks - current.DurationTicks);
        return new TimeWindow(start, start + current.DurationTicks);
    }

    /// <summary>
    /// A scale greater than one zooms in. The anchor stays at its relative position until bounded.
    /// Tick calculations use the exact supplied double ratio and round to nearest, ties to even.
    /// Scale one preserves the clamped window, including an empty window, exactly.
    /// </summary>
    public TimeWindow Zoom(double scale, long anchorTicks, TimeWindow extent)
    {
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale));
        var current = ClampTo(extent);
        if (extent.IsEmpty || scale == 1d)
            return current;
        var duration = Math.Max(1, current.DurationTicks);
        var newDuration = ZoomDuration(duration, scale, extent.DurationTicks);
        var anchor = Math.Clamp(anchorTicks, current.StartTicks, current.EndTicks);
        var anchorOffset = current.IsEmpty ? RoundRatio(newDuration, 2)
            : RoundRatio((BigInteger)(anchor - current.StartTicks) * newDuration, current.DurationTicks);
        var proposed = anchor - (long)anchorOffset;
        var start = (long)Math.Clamp(proposed, extent.StartTicks, extent.EndTicks - newDuration);
        return new TimeWindow(start, start + newDuration);
    }

    private static long ZoomDuration(long duration, double scale, long maximumDuration)
    {
        // Decode the positive finite IEEE-754 value instead of converting integer ticks to double.
        // The largest intermediate has about 1,136 bits, even for the smallest subnormal scale.
        var bits = BitConverter.DoubleToInt64Bits(scale);
        var exponentBits = (int)((bits >> 52) & 0x7ff);
        var significand = bits & 0x000fffffffffffffL;
        var exponent = -1074;
        if (exponentBits != 0)
        {
            significand |= 1L << 52;
            exponent = exponentBits - 1023 - 52;
        }
        var numerator = new BigInteger(duration);
        var denominator = new BigInteger(significand);
        if (exponent < 0) numerator <<= -exponent;
        else denominator <<= exponent;
        var rounded = RoundRatio(numerator, denominator);
        if (rounded < 1) return 1;
        return rounded >= maximumDuration ? maximumDuration : (long)rounded;
    }

    private static BigInteger RoundRatio(BigInteger numerator, BigInteger denominator)
    {
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        var comparison = (remainder << 1).CompareTo(denominator);
        return comparison > 0 || (comparison == 0 && !quotient.IsEven) ? quotient + 1 : quotient;
    }
}
