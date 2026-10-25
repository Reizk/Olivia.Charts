using System.Collections.Immutable;

namespace Olivia.Core.Charts;

public readonly record struct PriceTimeAxisTick(double Position, DateTimeOffset Timestamp);

/// <summary>One immutable source-bound compressed time projection shared by all price panes.
/// Range is independent of segment count; empty side margins do not create synthetic data.</summary>
public sealed class PriceTimeViewport
{
    public PriceTimeViewport(PriceTimeAxis axis, PriceTimeAxisRange range)
    {
        ArgumentNullException.ThrowIfNull(axis);
        ArgumentNullException.ThrowIfNull(range);
        if (!double.IsFinite(1 / range.Length)) throw new ArgumentOutOfRangeException(nameof(range));
        Axis = axis;
        Range = range;
    }

    public PriceTimeAxis Axis { get; }
    public PriceTimeAxisRange Range { get; }

    public ImmutableArray<PriceTimeAxisTick> CreateTicks(int maximum = 6)
    {
        if (maximum < 2 || maximum > 32) throw new ArgumentOutOfRangeException(nameof(maximum));
        var ticks = ImmutableArray.CreateBuilder<PriceTimeAxisTick>();
        DateTimeOffset? previous = null;
        for (var i = 0; i < maximum; i++)
        {
            var position = i / (double)(maximum - 1);
            var timestamp = Axis.ToTimestamp(Range.Denormalize(position));
            if (timestamp is not { } date || date == previous || !Axis.IsTimestampVisible(date)) continue;
            ticks.Add(new(position, date));
            previous = date;
        }
        return ticks.ToImmutable();
    }

    public (int Start, int Count) SelectSegments()
    {
        var start = Bound(Range.Minimum, upper: false);
        return (start, Bound(Range.Maximum, upper: true) - start);
    }

    /// <summary>Nearest occupied bucket, with midpoint ties and same-bucket segment ties
    /// selecting the later segment. Padding beyond half a bucket has no hover target.</summary>
    public int? FindSegment(double normalizedX)
    {
        if (!double.IsFinite(normalizedX) || normalizedX < 0 || normalizedX > 1) return null;
        var bucket = Math.Floor(Range.Denormalize(normalizedX) + .5);
        if (bucket < 0 || bucket >= Axis.BucketCount) return null;
        var index = Bound(bucket, upper: true) - 1;
        if (index < 0) return null;
        var coordinate = Axis.GetSegmentCoordinate(index);
        return coordinate >= Range.Minimum && coordinate <= Range.Maximum ? index : null;
    }

    /// <summary>Preserves the numeric span and fractional position across append/eviction.
    /// A common occupied bucket is the origin anchor, so a removed weekend is not counted
    /// as thousands of removed slots. Another dataset or interval exits this viewport mode.</summary>
    public PriceTimeViewport? Rebase(PriceTimeAggregationSnapshot aggregation)
    {
        ArgumentNullException.ThrowIfNull(aggregation);
        if (ReferenceEquals(Axis.Aggregation, aggregation)) return this;
        var previous = Axis.Aggregation;
        if (aggregation.Interval != previous.Interval || aggregation.Source.SeriesId != previous.Source.SeriesId ||
            aggregation.Source.DatasetVersion != previous.Source.DatasetVersion) return null;
        var next = Axis.Rebase(aggregation);
        if (Axis.IsEmpty || next.IsEmpty) return new(next, Range);
        var anchor = Axis.GetBucketStart(Axis.BucketCount - 1);
        var shift = next.ToCoordinate(anchor)!.Value - (Axis.BucketCount - 1);
        return new(next, shift == 0 ? Range : Range.Pan(shift));
    }

    /// <summary>Bounds a pan/zoom without changing its requested span. An initial range
    /// larger than the whole range can continue to contain the complete data and margins.</summary>
    public PriceTimeAxisRange Constrain(PriceTimeAxisRange candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (Axis.FinancialWholeRange is not { } whole) return candidate;
        var low = Math.Min(whole.Minimum, whole.Maximum - candidate.Length);
        var high = Math.Max(whole.Minimum, whole.Maximum - candidate.Length);
        var minimum = Math.Clamp(candidate.Minimum, low, high);
        return new(minimum, minimum + candidate.Length);
    }

    private int Bound(double coordinate, bool upper)
    {
        var low = 0;
        var high = Axis.Aggregation.Segments.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var current = Axis.GetSegmentCoordinate(middle);
            if (current < coordinate || upper && current == coordinate) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
