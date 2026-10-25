namespace Olivia.Core.Charts;

/// <summary>Absolute Y-axis margins from the Financial example's interval settings.
/// Raw ordinal charts retain zero margins. Hour multiples use the same constants.</summary>
public readonly record struct PriceFinancialSideMargins
{
    private PriceFinancialSideMargins(double price, double volume) { Price = price; Volume = volume; }
    public double Price { get; }
    public double Volume { get; }

    public static PriceFinancialSideMargins ForInterval(PriceTimeInterval? interval) => interval switch
    {
        null => default,
        PriceTimeInterval.Minute1 => new(.03, 500),
        PriceTimeInterval.Minute5 => new(.03 * 5, 500 * 5),
        PriceTimeInterval.Minute15 => new(.03 * 15, 500 * 15),
        PriceTimeInterval.Minute30 => new(.03 * 30, 500 * 30),
        PriceTimeInterval.Hour1 or PriceTimeInterval.Hour4 => new(1.8, 30000),
        _ => throw new ArgumentOutOfRangeException(nameof(interval))
    };

    /// <summary>Expand the auto-domain after all price overlays. Saturation only limits
    /// unrepresentable display bounds; original prices and explicit fixed domains stay intact.</summary>
    public HistoryValueDomain ExpandPrice(HistoryValueDomain domain) =>
        new(Math.Max(-double.MaxValue, domain.Minimum - Price),
            Math.Min(double.MaxValue, domain.Maximum + Price));
}
