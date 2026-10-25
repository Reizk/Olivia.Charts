using System.Collections.Immutable;

namespace Olivia.Core.Charts;

public readonly record struct HistoryChartPoint(double X, double Y);
public readonly record struct HistoryChartLine(HistoryChartPoint Start, HistoryChartPoint End);

/// <summary>Position is ascending normalized Y: zero is the minimum, one is the maximum.</summary>
public readonly record struct HistoryAxisTick(double Value, double Position);

/// <summary>Pure finite, clamped chart geometry, independent of any UI or label culture.</summary>
public static class DualAxisChartGeometry
{
    public static double NormalizeX(long utcTicks, TimeWindow window)
    {
        if (utcTicks < 0 || utcTicks > TimeWindow.MaximumExclusiveTicks)
            throw new ArgumentOutOfRangeException(nameof(utcTicks));
        if (window.IsEmpty) return 0.5;
        if (utcTicks <= window.StartTicks) return 0;
        if (utcTicks >= window.EndTicks) return 1;
        return (double)(utcTicks - window.StartTicks) / window.DurationTicks;
    }

    public static double NormalizeY(double value, HistoryValueDomain domain)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (domain.IsConstant) return 0.5;
        if (value <= domain.Minimum) return 0;
        if (value >= domain.Maximum) return 1;
        var range = domain.Maximum - domain.Minimum;
        // Only a cross-zero range can overflow. Halving before subtracting keeps its ratio finite.
        var position = double.IsFinite(range)
            ? (value - domain.Minimum) / range
            : (value / 2 - domain.Minimum / 2) / (domain.Maximum / 2 - domain.Minimum / 2);
        return Math.Clamp(position, 0, 1);
    }

    /// <summary>Projects into a finite rectangle; Y increases downwards. Out-of-domain values clamp.</summary>
    public static HistoryChartPoint Project(long utcTicks, double value, TimeWindow window,
        HistoryValueDomain domain, double left, double top, double width, double height)
    {
        ValidateRectangle(left, top, width, height);
        return new HistoryChartPoint(left + NormalizeX(utcTicks, window) * width,
            top + (1 - NormalizeY(value, domain)) * height);
    }

    /// <summary>
    /// Clips an original continuous edge in Y before projecting. Both endpoints must be inside the
    /// shared half-open UTC window; outside-X neighbors are deliberately rejected, matching Query.
    /// This avoids turning
    /// off-scale vertices into misleading boundary-hugging curves. Gaps and wholly outside edges have
    /// no geometry. Exact Y-boundary overrides preserve steep ±MaxValue crossings even when their two
    /// intersection fractions round to the same double. Separate raw runs must never be joined.
    /// </summary>
    public static HistoryChartLine? ClipSegment(HistorySample first, HistorySample second,
        TimeWindow window, HistoryValueDomain domain, double left, double top, double width, double height)
    {
        ValidateRectangle(left, top, width, height);
        if (!window.Contains(first.UtcTicks) || !window.Contains(second.UtcTicks) || first.IsGap || second.IsGap)
            return null;
        var start = 0d;
        var end = 1d;
        var firstY = first.Value!.Value;
        var secondY = second.Value!.Value;
        double? startY = null;
        double? endY = null;
        if (firstY == secondY)
        {
            if (firstY < domain.Minimum || firstY > domain.Maximum) return null;
        }
        else
        {
            var forward = secondY > firstY;
            var rawBounds = new HistoryValueDomain(Math.Min(firstY, secondY), Math.Max(firstY, secondY));
            if (rawBounds.Maximum < domain.Minimum || rawBounds.Minimum > domain.Maximum) return null;
            var entry = forward ? domain.Minimum : domain.Maximum;
            var exit = forward ? domain.Maximum : domain.Minimum;
            if (entry >= rawBounds.Minimum && entry <= rawBounds.Maximum)
            {
                var fraction = forward ? NormalizeY(entry, rawBounds) : 1 - NormalizeY(entry, rawBounds);
                if (fraction >= start)
                {
                    start = fraction;
                    startY = domain.IsConstant ? 0.5 : forward ? 0 : 1;
                }
            }
            if (exit >= rawBounds.Minimum && exit <= rawBounds.Maximum)
            {
                var fraction = forward ? NormalizeY(exit, rawBounds) : 1 - NormalizeY(exit, rawBounds);
                if (fraction <= end)
                {
                    end = fraction;
                    endY = domain.IsConstant ? 0.5 : forward ? 1 : 0;
                }
            }
            if (start > end) return null;
        }
        var firstX = NormalizeX(first.UtcTicks, window);
        var secondX = NormalizeX(second.UtcTicks, window);
        HistoryChartPoint At(double fraction, double? yOverride)
        {
            var x = Math.Clamp(firstX + fraction * (secondX - firstX), 0, 1);
            var y = yOverride ?? NormalizeY(Interpolate(firstY, secondY, fraction), domain);
            return new HistoryChartPoint(left + x * width, top + (1 - y) * height);
        }
        return new HistoryChartLine(At(start, startY), At(end, endY));
    }

    /// <summary>
    /// Produces at most tickCount distinct finite values, including exact endpoints. Empty domains
    /// have no ticks; constant domains have one centered tick. Adjacent doubles never create duplicates.
    /// </summary>
    public static ImmutableArray<HistoryAxisTick> CreateTicks(HistoryValueDomain? domain, int tickCount = 5)
    {
        if (tickCount < 2 || tickCount > 256) throw new ArgumentOutOfRangeException(nameof(tickCount));
        if (!domain.HasValue) return [];
        var bounds = domain.Value;
        if (bounds.IsConstant) return ImmutableArray.Create(new HistoryAxisTick(bounds.Minimum, 0.5));
        var ticks = ImmutableArray.CreateBuilder<HistoryAxisTick>(tickCount);
        for (var index = 0; index < tickCount; index++)
        {
            var fraction = (double)index / (tickCount - 1);
            var value = index == 0 ? bounds.Minimum : index == tickCount - 1 ? bounds.Maximum
                : bounds.Minimum < 0 && bounds.Maximum > 0
                    ? bounds.Minimum * (1 - fraction) + bounds.Maximum * fraction
                    : bounds.Minimum + (bounds.Maximum - bounds.Minimum) * fraction;
            value = Math.Clamp(value, bounds.Minimum, bounds.Maximum);
            if (ticks.Count == 0 || value > ticks[^1].Value)
                ticks.Add(new HistoryAxisTick(value, NormalizeY(value, bounds)));
        }
        return ticks.ToImmutable();
    }

    private static double Interpolate(double first, double second, double fraction)
    {
        if (fraction == 0) return first;
        if (fraction == 1) return second;
        return Math.Sign(first) != Math.Sign(second)
            ? first * (1 - fraction) + second * fraction
            : first + (second - first) * fraction;
    }

    private static void ValidateRectangle(double left, double top, double width, double height)
    {
        if (!double.IsFinite(left)) throw new ArgumentOutOfRangeException(nameof(left));
        if (!double.IsFinite(top)) throw new ArgumentOutOfRangeException(nameof(top));
        if (!double.IsFinite(width) || width < 0 || !double.IsFinite(left + width))
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height < 0 || !double.IsFinite(top + height))
            throw new ArgumentOutOfRangeException(nameof(height));
    }
}
