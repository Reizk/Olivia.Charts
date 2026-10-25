using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Olivia.Avalonia;
using Olivia.Charts.Demo;
using Xunit;

namespace Olivia.Headless.Tests;

public sealed class SensorGalleryInteractionTests
{
    [AvaloniaFact]
    public async Task Drag_opens_history_and_toolbar_follow_restores_live_updates()
    {
        var window = new GalleryWindow(); window.Show();
        try
        {
            await window.SelectExampleAsync("sensors"); Dispatcher.UIThread.RunJobs();
            var scene = window.CurrentScene!; scene.SetPaused(true);
            var chart = Assert.IsType<RealtimeSensorPlot>(scene.View);
            var before = chart.Frame!;
            var point = chart.TranslatePoint(chart.PanePlotBounds(0).Center, window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseMove(point + new Vector(80, 0));
            window.MouseUp(point + new Vector(80, 0), MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.False(chart.Frame!.IsFollowingLatest);
            Assert.True(chart.Frame.Window.EndTicks < before.Window.EndTicks);
            Assert.Equal(before.SourceVersion, chart.Frame.SourceVersion);
            scene.SetPaused(false); scene.Advance(); Dispatcher.UIThread.RunJobs();
            Assert.Contains("查看历史", scene.ReadViewState!());
            var follow = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "FollowLatest");
            await TestUi.EventuallyAsync(() => follow.IsEnabled, "Follow toolbar button did not become available in history mode.");
            TestUi.Click(window, follow.TranslatePoint(new Point(follow.Bounds.Width / 2, follow.Bounds.Height / 2), window)!.Value);
            Dispatcher.UIThread.RunJobs();
            Assert.True(chart.Frame.IsFollowingLatest);
            var resumed = chart.Frame; scene.Advance(); Dispatcher.UIThread.RunJobs();
            Assert.True(chart.Frame.Window.EndTicks > resumed.Window.EndTicks);
            Assert.True(chart.Frame.SourceVersion > resumed.SourceVersion);
            Assert.Contains("实时跟随", scene.ReadViewState!());
        }
        finally { await window.StopAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Wheel_zoom_keeps_latest_data_moving_and_Home_returns_from_manual_pan()
    {
        var window = new GalleryWindow(); window.Show();
        try
        {
            await window.SelectExampleAsync("sensors");
            var scene = window.CurrentScene!;
            var chart = Assert.IsType<RealtimeSensorPlot>(scene.View);
            Dispatcher.UIThread.RunJobs();
            var initial = chart.Frame!;
            var position = chart.TranslatePoint(chart.PanePlotBounds(0).Center, window)!.Value;
            window.MouseWheel(position, new Vector(0, 1));
            Dispatcher.UIThread.RunJobs();
            var zoomed = chart.Frame!;
            Assert.True(zoomed.Window.DurationTicks < initial.Window.DurationTicks);
            Assert.True(zoomed.IsFollowingLatest, "Zooming a live chart must keep it following incoming readings.");
            scene.Advance(); Dispatcher.UIThread.RunJobs();
            Assert.True(chart.Frame!.SourceVersion > zoomed.SourceVersion);
            Assert.True(chart.Frame.Window.EndTicks > zoomed.Window.EndTicks);
            Assert.Equal(zoomed.Window.DurationTicks, chart.Frame.Window.DurationTicks);
            window.MouseWheel(position, new Vector(0, -1));
            Dispatcher.UIThread.RunJobs();
            Assert.True(chart.Frame.IsFollowingLatest);
            var wider = chart.Frame; scene.Advance(); Dispatcher.UIThread.RunJobs();
            Assert.True(chart.Frame.Window.EndTicks > wider.Window.EndTicks);

            chart.Focus(); TestUi.Press(window, Key.Left); Dispatcher.UIThread.RunJobs();
            Assert.False(chart.Frame.IsFollowingLatest);
            var manual = chart.Frame; scene.Advance(); Dispatcher.UIThread.RunJobs();
            Assert.True(chart.Frame.SourceVersion > manual.SourceVersion);
            Assert.Equal(manual.Window, chart.Frame.Window);
            TestUi.Press(window, Key.Home); Dispatcher.UIThread.RunJobs();
            Assert.True(chart.Frame.IsFollowingLatest);
            Assert.Equal(chart.Frame.Extent.EndTicks, chart.Frame.Window.EndTicks);
            var beforePause = chart.Frame.SourceVersion;
            scene.SetPaused(true); scene.Advance(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(beforePause, chart.Frame.SourceVersion);
            scene.SetPaused(false); scene.Advance(); Dispatcher.UIThread.RunJobs();
            Assert.True(chart.Frame.SourceVersion > beforePause);
        }
        finally { await window.StopAsync(); window.Close(); }
    }
}
