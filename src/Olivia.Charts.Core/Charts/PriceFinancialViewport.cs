namespace Olivia.Core.Charts;

/// <summary>Immutable initial-time selection over an unchanged, full-source aggregation.
/// Requested UTC bounds map into compressed bucket coordinates. Options activates this
/// projection and its empty margins, while retaining legacy ordinal selection metadata.</summary>
public sealed class PriceInitialTimeView
{
    internal PriceInitialTimeView(PriceTimeAggregationSnapshot aggregation, TimeSpan effectiveUnit,
        DateTimeOffset? requestedMinimumUtc, DateTimeOffset? requestedMaximumUtc, PriceChartOptions options)
    {
        Aggregation = aggregation;
        EffectiveUnit = effectiveUnit;
        RequestedMinimumUtc = requestedMinimumUtc;
        RequestedMaximumUtc = requestedMaximumUtc;
        TimeAxis = PriceTimeAxis.Create(aggregation);
        if (requestedMinimumUtc.HasValue && requestedMaximumUtc.HasValue)
            RequestedAxisRange = new(TimeAxis.ToCoordinate(requestedMinimumUtc.Value)!.Value,
                TimeAxis.ToCoordinate(requestedMaximumUtc.Value)!.Value);
        Options = RequestedAxisRange is { } range ? options.WithTimeViewport(new(TimeAxis, range)) : options;
    }

    public PriceTimeAggregationSnapshot Aggregation { get; }
    public PriceChartSource Source => Aggregation.Source;
    public TimeSpan EffectiveUnit { get; }
    public DateTimeOffset? RequestedMinimumUtc { get; }
    public DateTimeOffset? RequestedMaximumUtc { get; }
    public PriceChartOptions Options { get; }
    /// <summary>Compressed UTC coordinate model shared by Options and all panes.</summary>
    public PriceTimeAxis TimeAxis { get; }
    /// <summary>Requested date bounds mapped into the compressed axis. Not clamped to
    /// FinancialWholeRange and not automatically rejected by the interaction-only zoom gate.</summary>
    public PriceTimeAxisRange? RequestedAxisRange { get; }
}

/// <summary>Finite static initial Financial time-window compatibility. The last ORIGINAL row
/// anchors maximum = last + 2 units and minimum = maximum - 60 units. Minute intervals use at
/// least five minutes per unit; hour intervals retain their duration. No reaggregation, source
/// slicing or synthetic bars are applied. The initial range is independent of the zoom gate.</summary>
public static class PriceFinancialViewport
{
    /// <summary>Preserves geometry budget, fixed domain and body width, replacing only the
    /// supplied X range. Empty sources have null requested bounds and an empty range.
    /// Throws ArgumentOutOfRangeException rather than clipping unrepresentable UTC bounds.</summary>
    public static PriceInitialTimeView CreateInitial(PriceTimeAggregationSnapshot aggregation,
        PriceChartOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(aggregation);
        options ??= PriceChartOptions.Default;
        var duration = PriceTimeAggregator.GetDuration(aggregation.Interval);
        var effectiveUnit = duration < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : duration;
        DateTimeOffset? minimum = null;
        DateTimeOffset? maximum = null;
        var start = 0;
        var count = 0;
        if (!aggregation.OriginalSource.Bars.IsEmpty)
        {
            // Convert first: a valid UTC bound may be outside the original offset's local
            // date-time range. AddTicks rejects either UTC overflow without clamping.
            maximum = aggregation.OriginalSource.Bars[^1].Timestamp.ToUniversalTime()
                .AddTicks(2 * effectiveUnit.Ticks);
            minimum = maximum.Value.AddTicks(-60 * effectiveUnit.Ticks);

            // UTC-sorted, half-open buckets intersect [minimum, maximum] iff
            // bucket.Start <= maximum && bucket.EndExclusive > minimum. All segments,
            // including explicit gaps within one bucket, therefore stay together.
            while (start < aggregation.Segments.Length &&
                aggregation.Segments[start].BucketEndUtcTicksExclusive <= minimum.Value.UtcTicks)
                start++;
            while (start + count < aggregation.Segments.Length &&
                aggregation.Segments[start + count].BucketStartUtc.UtcTicks <= maximum.Value.UtcTicks)
                count++;
        }

        var selectedOptions = new PriceChartOptions(start, count, options.GeometryBudget,
            options.FixedDomain, options.BodyWidthFraction);
        return new(aggregation, effectiveUnit, minimum, maximum, selectedOptions);
    }
}
