using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Olivia.Avalonia;
using Olivia.Charts.Demo;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Olivia.Headless.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Olivia.Headless.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<ChartsApp>().UseSkia()
        .WithInterFont().With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

internal static class TestUi
{
    public static Window Show(Control control, double width = 800, double height = 320)
    {
        var window = new Window { Width = width, Height = height, Content = control };
        window.Show(); Dispatcher.UIThread.RunJobs(); return window;
    }
    public static async Task EventuallyAsync(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail(message);
    }
    public static async Task SettleAsync(HistoryPlot chart) => await EventuallyAsync(
        () => chart.WhenIdle.IsCompleted, "History worker did not settle.");
    public static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
    }
    public static void Click(Window window, Point point)
    {
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
    }
    public static byte[] Capture(Window window, string? fileName = null)
    {
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        using var stream = new MemoryStream(); frame.Save(stream, PngBitmapEncoderOptions.Default);
        var bytes = stream.ToArray(); Assert.True(bytes.Length > 1000);
        if (fileName is not null) File.WriteAllBytes(ArtifactPath(fileName), bytes);
        return bytes;
    }
    public static string ArtifactPath(string fileName)
    {
        if (Path.IsPathRooted(fileName) || fileName.Contains(':') || fileName.Split('/', '\\').Any(p => p is "" or "." or ".."))
            throw new ArgumentException("Use a relative artifact path without traversal.", nameof(fileName));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "docs"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var output = Path.Combine(directory.FullName, "artifacts", "screenshots");
        var result = Path.Combine(output, fileName); Directory.CreateDirectory(Path.GetDirectoryName(result)!);
        return result;
    }
}
