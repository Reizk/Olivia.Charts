using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceFinancialSideMarginTests
{
    [Fact]
    public void Dense_candle_exact_ink_wins_over_a_later_neighbors_expanded_hit_target()
    {
        var time = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var source = new PriceChartSource("dense", "dense", Enumerable.Range(0, 500)
            .Select(i => new PriceBar(i, time.AddMinutes(i), 10, 12, 8, 11)));
        var snapshot = source.Query();
        var candle = snapshot.Candles[5];
        var body = candle.Body!.Value;
        Assert.Equal(candle.Hit, PriceChartGeometry.HitTest(snapshot, candle.CenterX,
            body.Top + body.Height / 2, .005, .005));
    }

    [Fact]
    public void Single_query_merges_price_overlay_before_padding_and_keeps_fixed_budget_and_empty_semantics()
    {
        var time = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var source = new PriceChartSource("q", "query", [new(1, time, 10, 12, 8, 11),
            new(2, time.AddMinutes(1), 11, 13, 9, 12)]);
        var options = new PriceChartOptions(geometryBudget: 1);
        var result = source.Query(options, new(7, 16), .5);
        Assert.Equal(new HistoryValueDomain(6.5, 16.5), result.Domain);
        Assert.Same(options, result.Options);
        Assert.Equal(2, result.RequiredCandleCount);
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, result.BudgetStatus);
        Assert.Empty(result.Candles);
        var admitted = source.Query(new(), new(7, 16), .5);
        var explicitDomain = source.Query(new(fixedDomain: new(6.5, 16.5)));
        Assert.Equal(explicitDomain.Candles.Select(c => (c.Hit, c.BarIndex, c.CenterX, c.Body,
            c.Wick!.Value.Start.X, c.Wick.Value.Start.Y, c.Wick.Value.End.X, c.Wick.Value.End.Y)),
            admitted.Candles.Select(c => (c.Hit, c.BarIndex, c.CenterX, c.Body,
                c.Wick!.Value.Start.X, c.Wick.Value.Start.Y, c.Wick.Value.End.X, c.Wick.Value.End.Y)));
        Assert.Equal(new HistoryValueDomain(9, 11), source.Query(new(fixedDomain: new(9, 11)), new(-10, 30), 4).Domain);
        Assert.Null(source.Query(new(barCount: 0), new(7, 16), .5).Domain);
        Assert.Null(new PriceChartSource("g", "gap", [new(1, time, null, null, null, null)])
            .Query(new(), new(7, 16), .5).Domain);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Query(new(), null, double.NaN));
    }

    [Theory]
    [InlineData(PriceTimeInterval.Minute1, .03, 500)]
    [InlineData(PriceTimeInterval.Minute5, .15, 2500)]
    [InlineData(PriceTimeInterval.Minute15, .45, 7500)]
    [InlineData(PriceTimeInterval.Minute30, .9, 15000)]
    [InlineData(PriceTimeInterval.Hour1, 1.8, 30000)]
    [InlineData(PriceTimeInterval.Hour4, 1.8, 30000)]
    public void Interval_profile_uses_absolute_reference_units(PriceTimeInterval interval, double price, double volume)
    {
        var margins = PriceFinancialSideMargins.ForInterval(interval);
        Assert.Equal(price, margins.Price, 12); Assert.Equal(volume, margins.Volume);
        var actual = margins.ExpandPrice(new(8, 12));
        Assert.Equal(8 - price, actual.Minimum, 12); Assert.Equal(12 + price, actual.Maximum, 12);
    }

    [Fact]
    public void Raw_flat_and_extreme_domains_remain_finite_without_changing_source_values()
    {
        var raw = PriceFinancialSideMargins.ForInterval(null);
        Assert.Equal(new HistoryValueDomain(10, 10), raw.ExpandPrice(new(10, 10)));
        var hour = PriceFinancialSideMargins.ForInterval(PriceTimeInterval.Hour4);
        Assert.Equal(new HistoryValueDomain(-1.8, 1.8), hour.ExpandPrice(new(0, 0)));
        Assert.Equal(new HistoryValueDomain(-double.MaxValue, double.MaxValue),
            hour.ExpandPrice(new(-double.MaxValue, double.MaxValue)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceFinancialSideMargins.ForInterval((PriceTimeInterval)99));
    }
}
