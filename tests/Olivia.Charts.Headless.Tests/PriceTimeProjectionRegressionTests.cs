using Avalonia;
using Avalonia.Headless.XUnit;
using Olivia.Avalonia;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Headless.Tests;

public sealed class PriceTimeProjectionRegressionTests
{
    [AvaloniaFact]
    public void Time_projection_keeps_shared_hover_padding_zoom_gate_and_indicator_toggle_coherent()
    {
        var original = Source();
        var aggregate = PriceTimeAggregator.Aggregate(original, PriceTimeInterval.Minute5);
        var plot = new PricePlot();
        var window = TestUi.Show(plot, 1000, 800);
        try
        {
            plot.SetBollingerEnabled(true); plot.SetCciEnabled(true);
            plot.SetWilliamsREnabled(true); plot.SetVolumeEnabled(true);
            plot.SetAggregatedSource(aggregate, new(timeViewport: new(PriceTimeAxis.Create(aggregate), new(60, 120))));
            window.UpdateLayout();
            var options = plot.Options;
            var sourceCopy = original.Bars.ToArray();
            var x = plot.PlotArea.Left + plot.PlotArea.Width / 2;
            Assert.True(plot.UpdateCrosshairAt(new Point(x, plot.CciPlotArea!.Value.Center.Y), plot.FrameRevision));
            Assert.Equal(90, plot.Crosshair!.BarIndex);
            Assert.Equal(.5, plot.Crosshair.ScreenX);
            Assert.Equal(original.Bars[90], plot.Crosshair.Hit.Bar);
            Assert.False(plot.UpdateCrosshairAt(new Point(plot.PlotArea.Right, plot.PlotArea.Center.Y), plot.FrameRevision));
            Assert.Null(plot.Crosshair); // Coordinate 120 is beyond the final occupied bucket, 119.
            plot.SetCciEnabled(false); plot.SetCciEnabled(true);
            Assert.Same(options, plot.Options);
            plot.Zoom(.5); // Proposed 120 > 90: reject without changing frame or options.
            Assert.Same(options, plot.Options);
            plot.Zoom(2);
            Assert.Equal(new PriceTimeAxisRange(75, 105), plot.Options.TimeViewport!.Range);
            Assert.Equal(31, plot.Snapshot!.BarCount);
            plot.Zoom(100);
            Assert.Equal(new PriceTimeAxisRange(89.5, 90.5), plot.Options.TimeViewport.Range);
            Assert.Equal(1, plot.Snapshot.BarCount);
            Assert.True(plot.VisibleBollingerSegmentCount > 0);
            Assert.True(plot.VisibleWilliamsRSegmentCount > 0);
            plot.Zoom(1d / 30);
            plot.Pan(10000);
            Assert.Equal(new PriceTimeAxisRange(91, 121), plot.Options.TimeViewport.Range);
            plot.Zoom(100);
            plot.Pan(10000);
            Assert.Equal(0, plot.Snapshot.BarCount); // Entirely within the trailing empty margin.
            plot.Zoom(.5);
            Assert.Equal(2, plot.Options.TimeViewport.Range.Length); // Empty views can zoom back out.
            plot.ResetView();
            Assert.Equal(new PriceTimeAxisRange(-2, 121), plot.Options.TimeViewport.Range);
            Assert.Equal(120, plot.Snapshot.BarCount);
            Assert.Equal(sourceCopy, original.Bars);
            plot.SetSource(original);
            Assert.Null(plot.Options.TimeViewport);
            Assert.Null(plot.AggregationSnapshot);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Aggregated_publication_rebases_time_range_atomically_and_old_frame_stays_immutable()
    {
        var source = Source();
        var aggregate = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var plot = new PricePlot();
        var window = TestUi.Show(plot, 1000, 800);
        try
        {
            plot.SetAggregatedSource(aggregate, new(timeViewport: new(PriceTimeAxis.Create(aggregate), new(60.25, 120.25))));
            var previous = plot.Snapshot!;
            var positions = previous.Candles.Select(c => c.CenterX).ToArray();
            var next = new PriceChartSource(source.SeriesId, source.Label, source.Bars.Skip(1).Append(
                new PriceBar(120, source.Bars[^1].Timestamp.AddMinutes(5), 9, 12, 8, 11, 100)), 1, 2);
            var nextAggregate = PriceTimeAggregator.Aggregate(next, PriceTimeInterval.Minute5);
            plot.SetAggregatedSource(nextAggregate);
            Assert.Same(nextAggregate.Source, plot.Options.TimeViewport!.Axis.Aggregation.Source);
            Assert.Equal(new PriceTimeAxisRange(59.25, 119.25), plot.Options.TimeViewport.Range);
            Assert.Equal(previous.GetNormalizedX(90), plot.Snapshot!.GetNormalizedX(89), 12);
            Assert.Equal(positions, previous.Candles.Select(c => c.CenterX));
            Assert.Equal(new PriceTimeAxisRange(60.25, 120.25), previous.Options.TimeViewport!.Range);
        }
        finally { window.Close(); }
    }

    private static PriceChartSource Source() => new("shared-time", "Shared time", Enumerable.Range(0, 120)
        .Select(i => new PriceBar(i, new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero).AddMinutes(i * 5),
            9, 12, 8, 11, 100)), 1);
}
