using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Olivia.Avalonia;
using Olivia.Charts.Demo;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Headless.Tests;

public sealed class BarScatterNavigationTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bar_wheel_keeps_the_edge_category_visible_down_to_one_category(bool right)
    {
        var window = new GalleryWindow(); window.Show();
        try
        {
            await window.SelectExampleAsync("bars"); Dispatcher.UIThread.RunJobs();
            var chart = Assert.IsType<BarPlot>(window.CurrentScene!.View);
            var key = right ? "m12" : "m1";
            var position = At(window, chart, chart.PlotArea, right ? .995 : .005, .5);
            for (var i = 0; i < 8; i++)
            {
                window.MouseWheel(position, new Vector(0, 1)); Dispatcher.UIThread.RunJobs();
                Assert.Contains(chart.Snapshot!.Bars, b => b.Hit.CategoryKey == key);
            }
            Assert.Equal(1, chart.Snapshot!.CategoryCount);
            chart.Focus(); TestUi.Press(window, Key.Home);
            Assert.Equal(12, chart.Snapshot.CategoryCount);
        }
        finally { await window.StopAsync(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(BarChartLayout.Grouped)]
    [InlineData(BarChartLayout.Overlap)]
    [InlineData(BarChartLayout.Stacked)]
    [InlineData(BarChartLayout.FullStacked)]
    public async Task Bar_drag_after_center_zoom_can_reach_and_select_both_ends(BarChartLayout layout)
    {
        var window = new GalleryWindow(); window.Show();
        try
        {
            await window.SelectExampleAsync("bars"); Dispatcher.UIThread.RunJobs();
            var chart = Assert.IsType<BarPlot>(window.CurrentScene!.View);
            chart.SetOptions(new(layout)); chart.Zoom(3);
            var left = At(window, chart, chart.PlotArea, .05, .5);
            var right = At(window, chart, chart.PlotArea, .95, .5);
            Drag(window, left, right);
            Assert.Equal(0, chart.Snapshot!.CategoryStart);
            Assert.Contains(chart.Snapshot.Bars, b => b.Hit.CategoryKey == "m1");
            Drag(window, right, left); Drag(window, right, left);
            Assert.Equal(12, chart.Snapshot.CategoryStart + chart.Snapshot.CategoryCount);
            var bar = chart.Snapshot.Bars.Last(b => b.Hit.CategoryKey == "m12");
            TestUi.Click(window, At(window, chart, chart.PlotArea, bar.Left + bar.Width / 2, bar.Top + bar.Height / 2));
            Assert.Equal(bar.Hit, chart.SelectedHit);
            Assert.Equal(layout, chart.Options.Layout);
            TestUi.Press(window, Key.Home); Assert.Equal(12, chart.Snapshot.CategoryCount);
        }
        finally { await window.StopAsync(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(10, 10)]
    [InlineData(10, 90)]
    [InlineData(90, 10)]
    [InlineData(90, 90)]
    public async Task Scatter_wheel_keeps_the_point_under_the_pointer_visible(double x, double y)
    {
        var window = new GalleryWindow(); window.Show();
        try
        {
            await window.SelectExampleAsync("scatter"); Dispatcher.UIThread.RunJobs();
            var chart = Assert.IsType<ScatterPlot>(window.CurrentScene!.View); chart.SetSource(GridSource());
            var target = chart.Snapshot!.Points.Single(p => p.Hit.X == x && p.Hit.Y == y);
            var position = At(window, chart, chart.PlotArea, target.X, target.Y);
            window.MouseWheel(position, new Vector(0, 1)); Dispatcher.UIThread.RunJobs();
            var visible = Assert.Single(chart.Snapshot.Points, p => p.Hit == target.Hit);
            Assert.Equal(target.X, visible.X, 10); Assert.Equal(target.Y, visible.Y, 10);
            Assert.True(chart.Snapshot.XDomain!.Value.Maximum - chart.Snapshot.XDomain.Value.Minimum < 100);
            TestUi.Click(window, position); Assert.Equal(target.Hit, chart.SelectedHit);
            window.MouseWheel(position, new Vector(0, -1)); Dispatcher.UIThread.RunJobs();
            Assert.Equal(100, chart.Snapshot.XDomain.Value.Maximum - chart.Snapshot.XDomain.Value.Minimum, 9);
            TestUi.Press(window, Key.Home); Assert.Equal(25, chart.Snapshot.Points.Length);
        }
        finally { await window.StopAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Scatter_drag_recovers_corner_points_on_both_axes_then_resets()
    {
        var window = new GalleryWindow(); window.Show();
        try
        {
            await window.SelectExampleAsync("scatter"); Dispatcher.UIThread.RunJobs();
            var chart = Assert.IsType<ScatterPlot>(window.CurrentScene!.View); chart.SetSource(GridSource()); chart.Zoom(2);
            Assert.DoesNotContain(chart.Snapshot!.Points, p => p.Hit.X == 10 && p.Hit.Y == 10);
            Drag(window, At(window, chart, chart.PlotArea, .2, .8), At(window, chart, chart.PlotArea, .7, .3));
            var corner = Assert.Single(chart.Snapshot.Points, p => p.Hit.X == 10 && p.Hit.Y == 10);
            TestUi.Click(window, At(window, chart, chart.PlotArea, corner.X, corner.Y));
            Assert.Equal(corner.Hit, chart.SelectedHit);
            Drag(window, At(window, chart, chart.PlotArea, .95, .05), At(window, chart, chart.PlotArea, .05, .95));
            Assert.Contains(chart.Snapshot.Points, p => p.Hit.X == 90 && p.Hit.Y == 90);
            TestUi.Press(window, Key.Home); Assert.Equal(25, chart.Snapshot.Points.Length);
            Assert.Null(chart.Options.FixedXDomain); Assert.Null(chart.Options.FixedYDomain);
        }
        finally { await window.StopAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Live_scatter_can_continue_dragging_across_a_data_update()
    {
        var window = new GalleryWindow(); window.Show();
        try
        {
            await window.SelectExampleAsync("scatter-live"); Dispatcher.UIThread.RunJobs();
            var scene = window.CurrentScene!; scene.SetPaused(true);
            var chart = Assert.IsType<ScatterPlot>(scene.View); chart.Zoom(2);
            var start = At(window, chart, chart.PlotArea, .5, .5);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(40, 30), RawInputModifiers.LeftMouseButton);
            var first = chart.Snapshot!;
            scene.SetPaused(false); scene.Advance(); scene.SetPaused(true); Dispatcher.UIThread.RunJobs();
            Assert.True(chart.Snapshot!.DatasetVersion > first.DatasetVersion);
            Assert.Equal(first.XDomain, chart.Snapshot.XDomain);
            window.MouseMove(start + new Vector(80, 60), RawInputModifiers.LeftMouseButton); window.MouseUp(start + new Vector(80, 60), MouseButton.Left);
            Assert.True(chart.Snapshot.XDomain!.Value.Minimum < first.XDomain!.Value.Minimum);
            Assert.True(chart.Snapshot.YDomain!.Value.Minimum > first.YDomain!.Value.Minimum);
        }
        finally { await window.StopAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Disabled_scatter_navigation_and_released_sources_ignore_dragging()
    {
        var window = new GalleryWindow(); window.Show();
        try
        {
            await window.SelectExampleAsync("scatter"); Dispatcher.UIThread.RunJobs();
            var chart = Assert.IsType<ScatterPlot>(window.CurrentScene!.View); chart.Zoom(2);
            var original = chart.Snapshot; chart.AllowNavigation = false;
            var point = At(window, chart, chart.PlotArea, .5, .5);
            window.MouseWheel(point, new Vector(0, 1)); Drag(window, point, point + new Vector(50, 50));
            Assert.Same(original, chart.Snapshot);
            chart.AllowNavigation = true;
            window.MouseDown(point, MouseButton.Left); chart.ReleaseSource();
            window.MouseMove(point + new Vector(50, 50), RawInputModifiers.LeftMouseButton); window.MouseUp(point + new Vector(50, 50), MouseButton.Left);
            Assert.Null(chart.Snapshot);
        }
        finally { await window.StopAsync(); window.Close(); }
    }

    private static ScatterChartSource GridSource() => new([new ScatterSeries("grid", "Grid",
        new[] { 0, 10, 50, 90, 100 }.SelectMany(x => new[] { 0, 10, 50, 90, 100 }.Select(y => new ScatterSample(x * 1000 + y, x, y))))], 1);
    private static Point At(Window window, Control chart, Rect area, double x, double y) =>
        chart.TranslatePoint(new Point(area.Left + x * area.Width, area.Top + y * area.Height), window)!.Value;
    private static void Drag(Window window, Point start, Point end)
    {
        window.MouseDown(start, MouseButton.Left); window.MouseMove(end, RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
