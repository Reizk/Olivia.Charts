namespace Olivia.Core.Charts;

/// <summary>Single-owner full-history UTC aggregation cache. A certified same-minute tail
/// update recomputes only its final segment, including exact volume summation. Rollover,
/// interval changes, arbitrary sources and gap transitions use the complete calculation.</summary>
public sealed class PriceAggregationCache
{
    private PriceTimeAggregationSnapshot? _snapshot;

    public PriceTimeAggregationSnapshot Aggregate(PriceChartSource source, PriceTimeInterval interval)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_snapshot is { } same && same.Interval == interval && ReferenceEquals(same.OriginalSource, source))
            return same;
        if (_snapshot is not { } old || old.Interval != interval || old.Segments.IsEmpty ||
            !source.HasSameHistory(old.OriginalSource) || source.SourceVersion <= old.OriginalSource.SourceVersion ||
            source.Bars[^1].IsGap != old.OriginalSource.Bars[^1].IsGap)
            return _snapshot = PriceTimeAggregator.Aggregate(source, interval);

        var start = old.Segments[^1].OriginalStartIndex;
        var tailSource = new PriceChartSource(source.SeriesId, source.Label, source.Bars.Skip(start),
            source.DatasetVersion, source.SourceVersion);
        var tail = PriceTimeAggregator.Aggregate(tailSource, interval);
        // The certified prefix, unchanged tail timestamp and unchanged gap state guarantee
        // exactly one replacement segment. Keep the conservative fallback explicit.
        if (tail.Segments.Length != 1)
            return _snapshot = PriceTimeAggregator.Aggregate(source, interval);
        var segment = tail.Segments[0] with { OriginalStartIndex = start };
        return _snapshot = new(source, old.Source.ReplaceTail(segment.Bar, source.SourceVersion), interval,
            old.Segments.SetItem(old.Segments.Length - 1, segment));
    }

    public void Clear() => _snapshot = null;
}
