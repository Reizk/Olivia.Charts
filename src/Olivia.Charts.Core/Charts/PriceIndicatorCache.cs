using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>Single-owner indicator preparation. Reuses immutable results only when the source
/// certifies an unchanged history prefix. A new dataset, retention rollover or arbitrary source
/// rebuild takes the full calculation path. Does not retain a source/version chain.</summary>
public sealed class PriceIndicatorCache
{
    private PriceBollingerSnapshot? _bands;
    private PriceWilliamsRSnapshot? _williams;
    private PriceCciSnapshot? _cci;

    public PriceBollingerSnapshot Bollinger(PriceChartSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(_bands?.Source, source)) return _bands;
        if (_bands is not { } old || !source.HasSameHistory(old.Source))
            return _bands = PriceBollingerBands.Calculate(source);
        var tail = PriceBollingerBands.Calculate(TailWindow(source, old.Period), old.Period,
            old.StandardDeviationMultiplier);
        var index = source.Bars.Length - 1;
        return _bands = new(source, ReplaceTail(old.Points, index, p => p.BarIndex,
            tail.Points.Select(p => p with { BarIndex = index })), old.Period, old.StandardDeviationMultiplier,
            old.OverflowCount - TailIssue(old.Source, old.Period, old.Points.IsEmpty ? -1 : old.Points[^1].BarIndex)
                + tail.OverflowCount);
    }

    public PriceWilliamsRSnapshot WilliamsR(PriceChartSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(_williams?.Source, source)) return _williams;
        if (_williams is not { } old || !source.HasSameHistory(old.Source))
            return _williams = PriceWilliamsR.Calculate(source);
        var tail = PriceWilliamsR.Calculate(TailWindow(source, old.Period), old.Period);
        var index = source.Bars.Length - 1;
        return _williams = new(source, ReplaceTail(old.Points, index, p => p.BarIndex,
            tail.Points.Select(p => p with { BarIndex = index })), old.Period,
            old.FlatWindowCount - TailIssue(old.Source, old.Period, old.Points.IsEmpty ? -1 : old.Points[^1].BarIndex)
                + tail.FlatWindowCount);
    }

    public PriceCciSnapshot Cci(PriceChartSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(_cci?.Source, source)) return _cci;
        if (_cci is not { } old || !source.HasSameHistory(old.Source))
            return _cci = PriceCommodityChannelIndex.Calculate(source);
        var tail = PriceCommodityChannelIndex.Calculate(TailWindow(source, old.Period), old.Period);
        var index = source.Bars.Length - 1;
        return _cci = new(source, ReplaceTail(old.Points, index, p => p.BarIndex,
            tail.Points.Select(p => p with { BarIndex = index })), old.Period,
            old.FlatWindowCount - TailIssue(old.Source, old.Period, old.Points.IsEmpty ? -1 : old.Points[^1].BarIndex)
                + tail.FlatWindowCount);
    }

    public void Clear() { _bands = null; _williams = null; _cci = null; }

    private static PriceChartSource TailWindow(PriceChartSource source, int period) => new(
        source.SeriesId, source.Label, source.Bars.Skip(Math.Max(0, source.Bars.Length - period)),
        source.DatasetVersion, source.SourceVersion);

    private static int TailIssue(PriceChartSource source, int period, int lastPointIndex)
    {
        if (source.Bars.Length < period || lastPointIndex == source.Bars.Length - 1) return 0;
        for (var i = source.Bars.Length - period; i < source.Bars.Length; i++)
            if (source.Bars[i].IsGap) return 0;
        return 1;
    }

    private static ImmutableArray<T> ReplaceTail<T>(ImmutableArray<T> previous, int index,
        Func<T, int> getIndex, IEnumerable<T> tail)
    {
        var count = previous.Length;
        if (count > 0 && getIndex(previous[^1]) == index) count--;
        var replacement = tail.ToArray(); // At most one complete trailing window.
        var result = ImmutableArray.CreateBuilder<T>(count + replacement.Length);
        result.AddRange(previous.AsSpan(0, count));
        result.AddRange(replacement);
        return result.MoveToImmutable();
    }
}
