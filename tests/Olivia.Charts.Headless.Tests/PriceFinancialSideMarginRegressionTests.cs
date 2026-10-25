using Avalonia.Headless.XUnit;
using Olivia.Avalonia;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Headless.Tests;

public sealed class PriceFinancialSideMarginRegressionTests
{
    [AvaloniaFact]
    public void Volume_interval_padding_survives_toggle_and_fixed_price_range_without_accumulation()
    {
        var plot = new PricePlot();
        plot.SetVolumeEnabled(true);
        var source = Source();
        foreach (var interval in Enum.GetValues<PriceTimeInterval>())
        {
            var aggregate = PriceTimeAggregator.Aggregate(source, interval);
            plot.SetAggregatedSource(aggregate, new(fixedDomain: new(-10, -1)));
            var volume = plot.VolumeSnapshot!;
            Assert.Equal(new HistoryValueDomain(0, 100 + PriceFinancialSideMargins.ForInterval(interval).Volume), volume.Domain);
            Assert.Empty(plot.Snapshot!.Candles);
            Assert.Equal(24, volume.Columns.Length);
            plot.SetVolumeEnabled(false); plot.SetVolumeEnabled(true);
            Assert.Equal(volume.Domain, plot.VolumeSnapshot!.Domain);
            Assert.Equal(volume.Columns, plot.VolumeSnapshot.Columns);
        }
        plot.SetSource(source, new());
        Assert.Equal(new HistoryValueDomain(0, 100), plot.VolumeSnapshot!.Domain);
        plot.ReleaseSource();
    }

    [AvaloniaFact]
    public void Interval_and_publication_change_auto_domain_but_keep_fixed_domains_and_raw_mode()
    {
        var source = Source();
        var plot = new PricePlot();
        var five = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        plot.SetAggregatedSource(five);
        var old = plot.Snapshot!;
        Assert.Equal(new HistoryValueDomain(7.85, 12.15), old.Domain);
        Assert.Same(five.Source, old.Source);
        var hour = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Hour4);
        plot.SetAggregatedSource(hour);
        Assert.Equal(new HistoryValueDomain(6.2, 13.8), plot.Snapshot!.Domain);
        var changed = new PriceChartSource(source.SeriesId, source.Label, source.Bars.Take(23).Append(
            new PriceBar(23, source.Bars[^1].Timestamp, 11, 16, 8, 12, 100)), sourceVersion: 1);
        plot.SetAggregatedSource(PriceTimeAggregator.Aggregate(changed, PriceTimeInterval.Hour4));
        Assert.Equal(new HistoryValueDomain(6.2, 17.8), plot.Snapshot!.Domain);
        Assert.Equal(new HistoryValueDomain(7.85, 12.15), old.Domain);
        Assert.Equal(12, source.Bars[^1].High);

        var fixedDomain = new HistoryValueDomain(9, 11);
        plot.SetOptions(new(fixedDomain: fixedDomain)); plot.SetBollingerEnabled(true);
        Assert.Equal(fixedDomain, plot.Snapshot!.Domain);
        plot.SetSource(source, new());
        Assert.Equal(new HistoryValueDomain(8, 12), plot.Snapshot!.Domain);
    }

    [AvaloniaFact]
    public void Price_margin_encloses_visible_bollinger_values_once_and_preserves_time_projection()
    {
        var source = Source(vary: true);
        var aggregate = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var plot = new PricePlot();
        var window = TestUi.Show(plot, 1000, 700);
        try
        {
            var options = new PriceChartOptions(timeViewport: new(PriceTimeAxis.Create(aggregate), new(18, 23)));
            plot.SetBollingerEnabled(true); plot.SetAggregatedSource(aggregate, options);
            var prices = plot.Snapshot!;
            var bars = prices.Source.Bars.Skip(prices.BarStart).Take(prices.BarCount).ToArray();
            var bands = plot.BollingerBands!.Points.Where(p => p.BarIndex >= prices.BarStart &&
                p.BarIndex < prices.BarStart + prices.BarCount).ToArray();
            var minimum = Math.Min(bars.Min(b => b.Low!.Value), bands.Min(b => b.Lower));
            var maximum = Math.Max(bars.Max(b => b.High!.Value), bands.Max(b => b.Upper));
            Assert.Equal(minimum - .15, prices.Domain!.Value.Minimum, 12);
            Assert.Equal(maximum + .15, prices.Domain.Value.Maximum, 12);
            Assert.Same(options, plot.Options); Assert.Same(options.TimeViewport, prices.Options.TimeViewport);
            Assert.True(plot.VisibleBollingerSegmentCount > 0);
            plot.SetCciEnabled(true); plot.SetWilliamsREnabled(true); plot.SetVolumeEnabled(true);
            Assert.Equal(prices.Domain, plot.Snapshot!.Domain); // No accumulated margin on toggles.
            var candle = plot.Snapshot.Candles.First(c => c.Body is { Height: > 0, Width: > 0 });
            var body = candle.Body!.Value;
            Assert.Equal(candle.Hit, PriceChartGeometry.HitTest(plot.Snapshot,
                body.Left + body.Width / 2, body.Top + body.Height / 2));
            window.UpdateLayout();
            Directory.CreateDirectory(Path.GetDirectoryName(TestUi.ArtifactPath("financial-cf1b3/price-y-margin.png"))!);
            TestUi.Capture(window, "financial-cf1b3/price-y-margin.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Empty_gaps_and_constant_price_do_not_invent_samples_or_invalid_domains()
    {
        var plot = new PricePlot();
        var time = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        foreach (var bars in new[] { Array.Empty<PriceBar>(), new[] { new PriceBar(1, time, null, null, null, null) } })
        {
            plot.SetAggregatedSource(PriceTimeAggregator.Aggregate(new("s", "empty", bars), PriceTimeInterval.Minute5));
            Assert.Null(plot.Snapshot!.Domain); Assert.Empty(plot.Snapshot.Candles);
        }
        plot.SetAggregatedSource(PriceTimeAggregator.Aggregate(new("s", "flat",
            [new PriceBar(1, time, 0, 0, 0, 0)]), PriceTimeInterval.Minute5));
        Assert.Equal(new HistoryValueDomain(-.15, .15), plot.Snapshot!.Domain);
        Assert.Single(plot.Snapshot.Candles); Assert.Equal(0, plot.Snapshot.Source.Bars[0].Close);
    }

    private static PriceChartSource Source(bool vary = false) => new("prices", "margin probe",
        Enumerable.Range(0, 24).Select(i =>
        {
            var mid = vary ? 10 + 4 * Math.Sin(i / 3d) : 10;
            return new PriceBar(i, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(i * 4),
                mid, vary ? mid + .1 : 12, vary ? mid - .1 : 8, vary ? mid + .05 : 11, 100);
        }));
}
