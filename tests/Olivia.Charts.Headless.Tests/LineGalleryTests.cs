using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Olivia.Avalonia;
using Olivia.Charts.Demo;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Headless.Tests;

public sealed class LineGalleryTests
{
    [AvaloniaTheory]
    [InlineData(LineInterpolation.Smooth)]
    [InlineData(LineInterpolation.Straight)]
    [InlineData(LineInterpolation.Step)]
    public async Task Line_modes_render_original_data_and_support_pointer_selection_and_zoom(LineInterpolation mode)
    {
        var window = new GalleryWindow(); window.Show();
        try
        {
            await window.SelectExampleAsync("line");
            var chart = Assert.IsType<LinePlot>(window.CurrentScene!.View);
            chart.SetInterpolation(mode); Dispatcher.UIThread.RunJobs();
            Assert.Equal(ChartBudgetStatus.Satisfied, chart.Snapshot!.BudgetStatus);
            Assert.Equal(28, chart.Snapshot.Points.Length); Assert.NotEmpty(chart.Snapshot.Segments);
            var point = chart.Snapshot.Points[4];
            var position = chart.TranslatePoint(new Point(chart.PlotArea.Left + point.X * chart.PlotArea.Width,
                chart.PlotArea.Top + point.Y * chart.PlotArea.Height), window)!.Value;
            TestUi.Click(window, position);
            Assert.Equal(point.Hit, chart.SelectedHit);
            var before = chart.Snapshot.XDomain!.Value;
            window.MouseWheel(position, new Vector(0, 1)); Dispatcher.UIThread.RunJobs();
            Assert.True(chart.Snapshot.XDomain!.Value.Maximum - chart.Snapshot.XDomain.Value.Minimum < before.Maximum - before.Minimum);
            Assert.All(chart.Snapshot.Points, p => Assert.Contains(chart.Source!.Series.SelectMany(s => s.Samples), s => s == p.Hit.Sample));
            TestUi.Press(window, Key.Home); Assert.Equal(before, chart.Snapshot.XDomain);
            TestUi.Capture(window, "line-" + mode + "-interaction.png");
            await window.StopAsync(); Assert.Null(chart.Source); Assert.Null(chart.Snapshot);
        }
        finally { await window.StopAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Compact_window_gives_the_chart_space_and_can_reopen_the_settings()
    {
        var window = new GalleryWindow { Width = 1000, Height = 760 }; window.Show();
        try
        {
            await window.SelectExampleAsync("sensors"); Dispatcher.UIThread.RunJobs();
            var chart = Assert.IsType<RealtimeSensorPlot>(window.CurrentScene!.View);
            var settings = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SettingsPanel");
            Assert.False(settings.IsVisible);
            Assert.True(chart.IsPlotViewportAvailable);
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ToggleSettings");
            TestUi.Click(window, button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value);
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.IsVisible);
            Assert.True(chart.IsPlotViewportAvailable);
            TestUi.Capture(window, "sensors-compact.png");
        }
        finally { await window.StopAsync(); window.Close(); }
    }
}
