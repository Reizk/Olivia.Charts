using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Olivia.Avalonia;
using Olivia.Charts.Demo;

namespace Olivia.Charts.DesktopSmoke;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => AppBuilder.Configure<SmokeApp>().UsePlatformDetect()
        .WithInterFont().StartWithClassicDesktopLifetime(args);
}

public sealed class SmokeApp : Application
{
    public override void Initialize() => Styles.Add(ChartsApp.CreateTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var output = Path.GetFullPath(desktop.Args?.FirstOrDefault() ?? "artifacts/desktop");
            var window = new GalleryWindow(); desktop.MainWindow = window;
            window.Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
            {
                var results = new List<object>(); var failed = false;
                Directory.CreateDirectory(output);
                try
                {
                    foreach (var example in ExampleCatalog.All)
                    {
                        var watch = Stopwatch.StartNew();
                        await window.SelectExampleAsync(example.Id);
                        var scene = window.CurrentScene!;
                        if (scene.View is RealtimeSensorPlot sensor)
                        {
                            await Settle(scene);
                            sensor.Zoom(1.5); await Settle(scene);
                            var zoomed = sensor.Frame!;
                            scene.Advance(); await Settle(scene);
                            if (!sensor.Frame!.IsFollowingLatest || sensor.Frame.SourceVersion <= zoomed.SourceVersion || sensor.Frame.Window.EndTicks <= zoomed.Window.EndTicks)
                                throw new InvalidOperationException("Native sensor zoom stopped following live data.");
                            sensor.Pan(-.2); await Settle(scene);
                            if (sensor.Frame.IsFollowingLatest) throw new InvalidOperationException("Native pan did not enter history mode.");
                            sensor.FollowLatest(); await Settle(scene);
                            if (!sensor.Frame.IsFollowingLatest) throw new InvalidOperationException("Native follow did not restore live data.");
                            scene.Reset?.Invoke(); await Settle(scene);
                        }
                        scene.SetPaused(true);
                        foreach (var dark in new[] { false, true })
                        {
                            window.SetDarkTheme(dark);
                            await Settle(scene);
                            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
                            bitmap.Render(window);
                            bitmap.Save(Path.Combine(output, example.Id + (dark ? "-dark.png" : "-light.png")), PngBitmapEncoderOptions.Default);
                        }
                        results.Add(new { id = example.Id, elapsed_ms = watch.ElapsedMilliseconds, status = scene.ReadStatus(), passed = true });
                    }
                    await window.StopAsync();
                }
                catch (Exception error)
                {
                    failed = true; results.Add(new { passed = false, error = error.ToString() });
                }
                finally
                {
                    await window.StopAsync();
                    File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                    {
                        platform = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(),
                        native_desktop = true, passed = !failed, examples = results
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    desktop.Shutdown(failed ? 1 : 0);
                }
            });
        }
        base.OnFrameworkInitializationCompleted();
    }
    private static async Task Settle(ChartScene scene)
    {
        // Yield to native layout/render work; finish on observable worker state, not a fixed sleep.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var plots = scene.View.GetVisualDescendants().Prepend(scene.View).ToArray();
            var ready = plots.All(p => p switch
            {
                HistoryPlot history => history.WhenIdle.IsCompleted,
                DualAxisHistoryPlot dual => dual.WhenIdle.IsCompleted,
                LargeDataPlot large => large.WhenIdle.IsCompleted,
                RealtimeSensorPlot sensors => sensors.Frame is not null && sensors.Frame.Panes.Length == 4,
                SurfacePlot surface => !surface.IsRendering && surface.DisplayedFrameRevision == surface.FrameRevision,
                _ => true
            });
            foreach (var surface in plots.OfType<SurfacePlot>())
                if (surface.RasterError is { } error) throw new InvalidOperationException(error);
            if (ready && scene.View.Bounds.Width > 0) return;
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Native chart render did not settle.");
            await Task.Delay(10);
        }
    }
}
