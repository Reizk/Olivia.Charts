using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Olivia.Avalonia;
using Olivia.Charts.Demo;
using Xunit;

namespace Olivia.Headless.Tests;

public sealed class GalleryTests
{
    public static IEnumerable<object[]> Examples => ExampleCatalog.All.Select(e => new object[] { e.Id });

    [AvaloniaTheory]
    [MemberData(nameof(Examples))]
    public async Task Every_example_renders_both_themes_and_releases_its_sources(string id)
    {
        var window = new GalleryWindow(); window.Show();
        try
        {
            await window.SelectExampleAsync(id);
            var scene = window.CurrentScene!; scene.SetPaused(true);
            await Settle(scene); Assert.Null(window.LastError);
            Assert.True(scene.View.Bounds.Width >= 400); Assert.True(scene.View.Bounds.Height >= 300);
            Assert.DoesNotContain("No source", scene.ReadStatus(), StringComparison.OrdinalIgnoreCase);
            TestUi.Capture(window, id + "-light.png");
            window.SetDarkTheme(true); await Settle(scene);
            TestUi.Capture(window, id + "-dark.png");
            // Exercise actual parameter choices instead of merely constructing the controls.
            foreach (var option in scene.Options.SelectMany(c => c.GetVisualDescendants().Prepend(c)).OfType<ComboBox>())
            {
                for (var i = 0; i < option.ItemCount; i++) option.SelectedIndex = i;
                await Settle(scene);
            }
            foreach (var option in scene.Options.OfType<CheckBox>())
            { option.IsChecked = !option.IsChecked; await Settle(scene); option.IsChecked = !option.IsChecked; }
            scene.Reset?.Invoke(); await Settle(scene);
            await window.StopAsync(); Assert.True(scene.IsClosed);
            var ticks = scene.TickCount; scene.Advance(); Assert.Equal(ticks, scene.TickCount);
        }
        finally { await window.StopAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Pause_theme_navigation_and_close_preserve_the_active_lifecycle()
    {
        var window = new GalleryWindow(); window.Show();
        try
        {
            await window.SelectExampleAsync("sensors");
            var scene = window.CurrentScene!;
            scene.Advance(); var before = scene.TickCount;
            scene.SetPaused(true); scene.Advance(); Assert.Equal(before, scene.TickCount);
            window.SetDarkTheme(true); Assert.Same(scene, window.CurrentScene); Assert.True(scene.IsPaused);
            scene.SetPaused(false); scene.Advance(); Assert.Equal(before + 1, scene.TickCount);
            var first = window.SelectExampleAsync("large-data");
            var last = window.SelectExampleAsync("price");
            await Task.WhenAll(first, last);
            Assert.True(scene.IsClosed); Assert.Equal("price", window.CurrentExample!.Id);
            await window.StopAsync(); Assert.Null(window.CurrentScene);
        }
        finally { await window.StopAsync(); window.Close(); }
    }

    [Fact]
    public void Catalog_search_has_stable_ids_and_no_unrelated_modules()
    {
        Assert.Equal(24, ExampleCatalog.All.Count);
        Assert.Equal(24, ExampleCatalog.All.Select(e => e.Id).Distinct().Count());
        Assert.Contains(ExampleCatalog.All, e => e.Id == "line" && e.Group == "基础图表");
        Assert.DoesNotContain(ExampleCatalog.All, e => e.Id is "point3d" or "surface" or "binding" or "workspace");
        Assert.Empty(ExampleCatalog.Search("", "数据与配置"));
        Assert.Single(ExampleCatalog.Search("金融图表"));
        Assert.Equal("heatmap", Assert.Single(ExampleCatalog.Search("heatmap")).Id);
        Assert.Empty(ExampleCatalog.Search("不存在的图表"));
        Assert.Empty(ExampleCatalog.Search("", "三维图表"));
        Assert.Empty(ExampleCatalog.Search("三维"));
        Assert.Empty(ExampleCatalog.Search("surface"));
    }

    private static async Task Settle(ChartScene scene)
    {
        Dispatcher.UIThread.RunJobs();
        var plots = scene.View.GetVisualDescendants().Prepend(scene.View).ToArray();
        foreach (var plot in plots)
        {
            if (plot is HistoryPlot history) await TestUi.SettleAsync(history);
            if (plot is DualAxisHistoryPlot dual) await TestUi.EventuallyAsync(() => dual.WhenIdle.IsCompleted, "Dual-axis worker did not settle.");
            if (plot is LargeDataPlot large) await TestUi.EventuallyAsync(() => large.WhenIdle.IsCompleted, "Large-data worker did not settle.");
            if (plot is RealtimeSensorPlot sensors)
            {
                await TestUi.EventuallyAsync(() => sensors.Frame is not null, "Sensor page must publish its initial frame even when paused immediately.");
                Assert.Equal(4, sensors.Frame!.Panes.Length);
                Assert.All(sensors.Frame.Panes, pane => Assert.Equal(2, pane.Series.Length));
                Assert.True(sensors.RenderStatistics.AcceptedFrames > 0);
            }
        }
        Dispatcher.UIThread.RunJobs();
    }
}
