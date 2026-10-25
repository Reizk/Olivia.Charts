using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Olivia.Avalonia;
using Olivia.Charts.Demo;
using Xunit;

namespace Olivia.Headless.Tests;

public sealed class SurfaceGalleryTests
{
    [AvaloniaFact]
    public async Task Static_surface_stays_idle_supports_camera_controls_and_updates_only_on_request()
    {
        var window = new GalleryWindow(); window.Show();
        try
        {
            await window.SelectExampleAsync("surface");
            var scene = window.CurrentScene!;
            var chart = Assert.IsType<SurfacePlot>(scene.View);
            await Settle(window, chart);
            var source = chart.Source;
            var revision = chart.FrameRevision;
            var camera = chart.Camera;
            // Longer than several old 100ms animation ticks, without pausing the scene.
            await Task.Delay(350); Dispatcher.UIThread.RunJobs();
            Assert.Same(source, chart.Source); Assert.Equal(revision, chart.FrameRevision); Assert.False(chart.IsRendering);
            Assert.False(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PauseExample").IsVisible);

            var center = chart.TranslatePoint(chart.PlotArea.Center, window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            window.MouseMove(center + new Vector(55, 25), RawInputModifiers.LeftMouseButton);
            window.MouseUp(center + new Vector(55, 25), MouseButton.Left);
            await Settle(window, chart);
            Assert.NotEqual(camera.Yaw, chart.Camera.Yaw); Assert.NotEqual(camera.Pitch, chart.Camera.Pitch);
            Assert.Same(source, chart.DisplayedSnapshot!.Source);
            var rotated = chart.Camera;
            window.MouseWheel(center, new Vector(0, 1)); await Settle(window, chart);
            Assert.True(chart.Camera.HalfHeight < rotated.HalfHeight);
            TestUi.Press(window, Key.Home); await Settle(window, chart);
            Assert.Equal(camera.Yaw, chart.Camera.Yaw); Assert.Equal(camera.Pitch, chart.Camera.Pitch);

            var update = scene.Options.OfType<Button>().Single(b => Equals(b.Content, "切换曲面"));
            TestUi.Click(window, update.TranslatePoint(new Point(update.Bounds.Width / 2, update.Bounds.Height / 2), window)!.Value);
            await Settle(window, chart);
            Assert.NotSame(source, chart.Source); Assert.Equal(source!.Version + 1, chart.Source!.Version);
            Assert.Same(chart.Source, chart.DisplayedSnapshot!.Source);
            Assert.Equal(camera.Yaw, chart.Camera.Yaw); Assert.Equal(camera.Pitch, chart.Camera.Pitch);
            revision = chart.FrameRevision;
            await Task.Delay(350); Dispatcher.UIThread.RunJobs();
            Assert.Equal(revision, chart.FrameRevision); Assert.False(chart.IsRendering);
            TestUi.Capture(window, "surface-static.png");
        }
        finally { await window.StopAsync(); window.Close(); }
    }

    private static async Task Settle(Window window, SurfacePlot chart)
    {
        await TestUi.EventuallyAsync(() => !chart.IsRendering && chart.Snapshot is not null, "Static surface did not finish rendering.");
        TestUi.Capture(window); Assert.Null(chart.RasterError);
        Assert.Equal(chart.FrameRevision, chart.DisplayedFrameRevision);
    }
}
