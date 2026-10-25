using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>A continuous viewport in compressed time units, independent of candle count.
/// Coordinates are relative to the first occupied UTC bucket. This type does not clamp a
/// viewport to data or apply the Financial example's zoom policy automatically.</summary>
public sealed record PriceTimeAxisRange
{
    public PriceTimeAxisRange(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum)) throw new ArgumentOutOfRangeException(nameof(minimum));
        if (!double.IsFinite(maximum) || maximum <= minimum || !double.IsFinite(maximum - minimum))
            throw new ArgumentOutOfRangeException(nameof(maximum));
        Minimum = minimum;
        Maximum = maximum;
    }

    public double Minimum { get; }
    public double Maximum { get; }
    public double Length => Maximum - Minimum;

    /// <summary>No clamping: callers can clip geometry at the viewport edges.</summary>
    public double Normalize(double coordinate)
    {
        if (!double.IsFinite(coordinate)) throw new ArgumentOutOfRangeException(nameof(coordinate));
        return (coordinate - Minimum) / Length;
    }

    public double Denormalize(double normalized)
    {
        if (!double.IsFinite(normalized)) throw new ArgumentOutOfRangeException(nameof(normalized));
        return Minimum + normalized * Length;
    }

    /// <summary>Matches the example's BeforeZoom check on the proposed numeric range, not
    /// the number of segments/candles. Initial ranges may exceed this limit.</summary>
    public bool IsFinancialZoomAllowed => Length <= 90;

    /// <summary>Builds a candidate around an anchor in [0,1]. It does not silently enforce
    /// zoom policy or whole-range limits; the owner can reject the returned candidate.</summary>
    public PriceTimeAxisRange Zoom(double factor, double anchor = .5)
    {
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        if (!double.IsFinite(anchor) || anchor < 0 || anchor > 1) throw new ArgumentOutOfRangeException(nameof(anchor));
        var length = Length / factor;
        var minimum = Minimum + (Length - length) * anchor;
        return new(minimum, minimum + length);
    }

    public PriceTimeAxisRange Pan(double units)
    {
        if (!double.IsFinite(units)) throw new ArgumentOutOfRangeException(nameof(units));
        return new(Minimum + units, Maximum + units);
    }
}

/// <summary>Immutable Financial time mapping for the supported fixed UTC minute/hour buckets.
/// Each occupied bucket advances one unit; multiple explicit-gap segments in the same bucket
/// share its coordinate. No prices, timestamps, keys or gaps are altered. Dates map to their
/// floored bucket; dates in an internal missing interval map to the preceding occupied bucket.
/// Outside the data ends, elapsed bucket distance is retained for side margins.
/// PriceTimeViewport opts the renderer into this mapping; raw ordinal views remain available.
/// It does not implement a local-time/DST calendar, workdays or variable-length date units.</summary>
public sealed class PriceTimeAxis
{
    private readonly ImmutableArray<long> _bucketTicks;
    private readonly ImmutableArray<long> _lastArgumentTicks;
    private readonly ImmutableArray<int> _segmentCoordinates;

    private PriceTimeAxis(PriceTimeAggregationSnapshot aggregation, ImmutableArray<long> bucketTicks,
        ImmutableArray<long> lastArgumentTicks, ImmutableArray<int> segmentCoordinates)
    {
        Aggregation = aggregation;
        Duration = PriceTimeAggregator.GetDuration(aggregation.Interval);
        _bucketTicks = bucketTicks;
        _lastArgumentTicks = lastArgumentTicks;
        _segmentCoordinates = segmentCoordinates;
    }

    public PriceTimeAggregationSnapshot Aggregation { get; }
    public TimeSpan Duration { get; }
    public int BucketCount => _bucketTicks.Length;
    public bool IsEmpty => _bucketTicks.IsEmpty;

    /// <summary>Uses the aggregation's existing bucket boundaries. The original source is
    /// retained by reference; construction is bounded by its 18,000-row source limit.</summary>
    public static PriceTimeAxis Create(PriceTimeAggregationSnapshot aggregation)
    {
        ArgumentNullException.ThrowIfNull(aggregation);
        var ticks = ImmutableArray.CreateBuilder<long>();
        var lastArguments = ImmutableArray.CreateBuilder<long>();
        var coordinates = ImmutableArray.CreateBuilder<int>(aggregation.Segments.Length);
        foreach (var segment in aggregation.Segments)
        {
            var bucket = segment.BucketStartUtc.UtcTicks;
            var last = aggregation.OriginalSource.Bars[segment.OriginalStartIndex + segment.OriginalCount - 1]
                .Timestamp.UtcTicks;
            if (ticks.Count == 0 || bucket != ticks[^1])
            {
                ticks.Add(bucket);
                lastArguments.Add(last);
            }
            else lastArguments[^1] = last;
            coordinates.Add(ticks.Count - 1);
        }
        return new(aggregation, ticks.ToImmutable(), lastArguments.ToImmutable(), coordinates.MoveToImmutable());
    }

    public int GetSegmentCoordinate(int segmentIndex) => _segmentCoordinates[segmentIndex];

    internal PriceTimeAxis Rebase(PriceTimeAggregationSnapshot aggregation)
    {
        if (ReferenceEquals(Aggregation, aggregation)) return this;
        // The private source token proves that only OHLC/volume in the tail changed.
        // Timestamps and gap segmentation must both be unchanged to reuse the mapping.
        return aggregation.Interval == Aggregation.Interval &&
            aggregation.Source.HasSameHistory(Aggregation.Source) &&
            aggregation.OriginalSource.HasSameHistory(Aggregation.OriginalSource)
            ? new(aggregation, _bucketTicks, _lastArgumentTicks, _segmentCoordinates)
            : Create(aggregation);
    }

    public DateTimeOffset GetBucketStart(int bucketIndex) => new(_bucketTicks[bucketIndex], TimeSpan.Zero);

    /// <summary>Null for empty history. Input offsets represent the same UTC instant and
    /// never modify the original offset retained in the aggregation.</summary>
    public double? ToCoordinate(DateTimeOffset timestamp)
    {
        if (IsEmpty) return null;
        var ticks = timestamp.UtcTicks / Duration.Ticks * Duration.Ticks;
        if (ticks < _bucketTicks[0]) return (ticks - _bucketTicks[0]) / Duration.Ticks;
        if (ticks > _bucketTicks[^1])
            return BucketCount - 1L + (ticks - _bucketTicks[^1]) / Duration.Ticks;
        return FindPreviousBucket(ticks);
    }

    /// <summary>Inverse of axis coordinates, not of arbitrary timestamps (the forward map
    /// floors). Within consecutive buckets fractions use elapsed time rounded to the base
    /// minute/hour (not the multiplier), with midpoints rounded forward. Across a missing
    /// interval fractions snap to the nearer occupied boundary; an exact half selects the
    /// earlier boundary, following the reference's midpoint rounding. Outside the data,
    /// fractions use the same base-unit rounding. Returns null for empty data or UTC date
    /// overflow; rejects nonfinite input. Date-boundary failure returns no date rather than
    /// reproducing the reference helper's MinValue fallback/overflow behavior.</summary>
    public DateTimeOffset? ToTimestamp(double coordinate)
    {
        if (!double.IsFinite(coordinate)) throw new ArgumentOutOfRangeException(nameof(coordinate));
        if (IsEmpty) return null;
        if (coordinate < 0) return AddUnits(_bucketTicks[0], coordinate);
        if (coordinate >= BucketCount - 1) return AddUnits(_bucketTicks[^1], coordinate - (BucketCount - 1));
        var index = (int)Math.Floor(coordinate);
        var fraction = coordinate - index;
        if (_bucketTicks[index + 1] - _bucketTicks[index] > Duration.Ticks)
            return GetBucketStart(fraction <= .5 ? index : index + 1);
        return AddUnits(_bucketTicks[index], fraction);
    }

    /// <summary>Whether an axis label's date lies in an occupied run or outside the complete
    /// data span. The end of a run is its last original timestamp, not the bucket's exclusive
    /// end. Explicit null-price rows still occupy time; they are not missing calendar ranges.</summary>
    public bool IsTimestampVisible(DateTimeOffset timestamp)
    {
        if (IsEmpty) return false;
        var ticks = timestamp.UtcTicks;
        if (ticks < _bucketTicks[0] || ticks >= _bucketTicks[^1]) return true;
        var index = FindPreviousBucket(ticks);
        return ticks <= _lastArgumentTicks[index] || _bucketTicks[index + 1] - _bucketTicks[index] == Duration.Ticks;
    }

    /// <summary>The official example's two-unit whole-range side margins. This is distinct
    /// from its requested initial range (whose minute mode uses at least five minutes).
    /// No fictitious bars are created to fill the margins.</summary>
    public PriceTimeAxisRange? FinancialWholeRange => IsEmpty ? null : new(-2, BucketCount - 1d + 2);

    private int FindPreviousBucket(long ticks)
    {
        var low = 0;
        var high = _bucketTicks.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_bucketTicks[middle] <= ticks) low = middle + 1;
            else high = middle;
        }
        return low - 1;
    }

    private DateTimeOffset? AddUnits(long anchor, double units)
    {
        // Bound before converting to an integer. Int128 then prevents intermediate tick
        // overflow at either DateTime edge without silently clamping an invalid date.
        var maximumUnits = DateTimeOffset.MaxValue.UtcTicks / (double)Duration.Ticks;
        if (units < -maximumUnits || units > maximumUnits) return null;
        var whole = (long)Math.Floor(units);
        var fractionTicks = (long)Math.Round((units - whole) * Duration.Ticks, MidpointRounding.ToEven);
        var ticks = (Int128)anchor + (Int128)whole * Duration.Ticks + fractionTicks;
        if (ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks) return null;
        var baseUnit = Duration >= TimeSpan.FromHours(1) ? TimeSpan.TicksPerHour : TimeSpan.TicksPerMinute;
        ticks = (ticks + baseUnit / 2) / baseUnit * baseUnit;
        return ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks
            ? null : new DateTimeOffset((long)ticks, TimeSpan.Zero);
    }
}
