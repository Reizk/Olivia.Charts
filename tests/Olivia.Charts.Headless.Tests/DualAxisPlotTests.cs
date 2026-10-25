using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Olivia.Avalonia;
using Olivia.Core.Charts;
using Olivia.Core.Display;
using Olivia.Core.Snapshots;
using SkiaSharp;
using Xunit;

namespace Olivia.Headless.Tests;

/// <summary>Independent adapter contracts, including real software-rendered pixels and gated work.</summary>
public sealed class DualAxisPlotTests
{
    private const long Epoch = 638977446000000000;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [AvaloniaFact]
    public async Task Shared_time_window_keeps_units_independent_and_selection_preserves_full_raw_identity()
    {
        var temperature = Series(101, 1000, i => 20 + Math.Sin(i * .03));
        var pressure = Series(202, 1000, i => 2000 + Math.Cos(i * .021) * 500);
        var source = Source(37, temperature, pressure);
        var chart = new DualAxisHistoryPlot { PointBudget = 32 };
        chart.SetSource(source);
        var window = TestUi.Show(chart, 1000, 400);
        try
        {
            await SettleAsync(chart);
            var snapshot = Assert.IsType<DualAxisChartSnapshot>(chart.Snapshot);
            Assert.Same(source, snapshot.Source);
            Assert.Equal(37, snapshot.SourceVersion);
            Assert.Equal(source.Extent, snapshot.Window);
            Assert.Equal(source.Extent, chart.ViewWindow);
            Assert.InRange(snapshot.PointCount, 1, 32);
            Assert.Equal(2, chart.AxisPresentations.Length);
            Assert.Equal(snapshot.PointCount, snapshot.Series.Sum(series => series.PointCount));
            Assert.All(snapshot.Series, series => Assert.All(series.Segments.SelectMany(segment => segment.Points), point =>
                Assert.Equal(series.Definition.Source.Samples[point.RawIndex], point.Sample)));
            var left = Assert.Single(chart.AxisPresentations, axis => axis.AxisId == "temperature");
            var right = Assert.Single(chart.AxisPresentations, axis => axis.AxisId == "pressure");
            Assert.Equal(HistoryAxisSide.Left, left.Side);
            Assert.Equal(HistoryAxisSide.Right, right.Side);
            Assert.True(left.Ticks.Max(tick => tick.Value) < 100);
            Assert.True(right.Ticks.Min(tick => tick.Value) > 1000);
            Assert.Contains("°C", left.Label);
            Assert.Contains("kPa", right.Label);

            // Reused keys across the two series must never confuse their raw identities.
            foreach (var index in new[] { 1, 137, 411, 731, 998 })
            {
                var first = chart.SelectNearest("temperature-series", temperature.Samples[index].UtcTicks);
                Assert.NotNull(first);
                Assert.Equal(37, first?.DatasetVersion);
                Assert.Equal(37, first?.SourceVersion);
                Assert.Equal("temperature-series", first?.SeriesId);
                Assert.Equal(101, first?.SeriesSourceVersion);
                Assert.Equal(temperature.Samples[index], first?.Sample);
                Assert.Equal(temperature.Samples[index].Key, first?.Key);
                Assert.Equal(temperature.Samples[index].UtcTicks, first?.UtcTicks);
                Assert.Equal(temperature.Samples[index].Value, first?.Value);
                var second = chart.SelectNearest("pressure-series", pressure.Samples[index].UtcTicks);
                Assert.Equal("pressure-series", second?.SeriesId);
                Assert.Equal(202, second?.SeriesSourceVersion);
                Assert.Equal(pressure.Samples[index], second?.Sample);
                Assert.Equal(second, chart.SelectedHit);
            }
            var displayedKeys = snapshot.Series.Single(series => series.Definition.Id == "temperature-series")
                .Segments.SelectMany(segment => segment.Points).Select(point => point.Key).ToHashSet();
            var undisplayed = temperature.Samples.First(sample => !displayedKeys.Contains(sample.Key));
            Assert.Equal(undisplayed, chart.SelectNearest("temperature-series", undisplayed.UtcTicks)?.Sample);
            Assert.Null(chart.SelectNearest("unknown-series", Epoch));
            TestUi.Capture(window);
        }
        finally { await CloseAsync(chart, window); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_pixels_show_both_tick_scales_legend_assignments_and_independently_projected_paths(bool lightPalette)
    {
        var chart = new DualAxisHistoryPlot
        {
            Options = DualAxisChartOptions.Default
                .WithAxisDomain("temperature", new HistoryValueDomain(0, 100))
                .WithAxisDomain("pressure", new HistoryValueDomain(0, 10000))
        };
        chart.Palette = lightPalette ? UiPalette.Light : UiPalette.Dark;
        chart.SetSource(Source(1, Series(1, 100, _ => 25), Series(2, 100, _ => 7500)));
        var window = TestUi.Show(chart, 1200, 480);
        try
        {
            await SettleAsync(chart);
            using var pixels = SKBitmap.Decode(TestUi.Capture(window));
            var area = chart.PlotArea;
            var background = SKColor.Parse(lightPalette ? "#FFFFFF" : "#14202E");
            var temperature = SKColor.Parse(lightPalette ? "#9C3C13" : "#FFBE98");
            var pressure = SKColor.Parse(lightPalette ? "#006F68" : "#61D5C3");
            Assert.True(CountInk(pixels, new Rect(8, area.Top - 10, 86, area.Height + 20), background) > 100,
                "Left tick numbers must be painted outside the plot.");
            Assert.True(CountInk(pixels, new Rect(area.Right + 8, area.Top - 10, 88, area.Height + 20), background) > 100,
                "Right tick numbers must be painted outside the plot.");
            for (var row = 0; row < 2; row++)
                Assert.True(CountInk(pixels, new Rect(46, 38 + row * 20, 950, 17), background) > 100,
                    "Every legend assignment must contain rendered text, not only metadata.");
            Assert.Contains("Left", chart.LegendPresentations.Single(item => item.SeriesId == "temperature-series").Text);
            Assert.Contains("°C", chart.LegendPresentations.Single(item => item.SeriesId == "temperature-series").Text);
            Assert.Contains("Right", chart.LegendPresentations.Single(item => item.SeriesId == "pressure-series").Text);
            Assert.Contains("kPa", chart.LegendPresentations.Single(item => item.SeriesId == "pressure-series").Text);
            Assert.True(CountColor(pixels, new Rect(area.Left + 20, area.Top + area.Height * .75 - 3, area.Width - 40, 6), temperature) > 100);
            Assert.True(CountColor(pixels, new Rect(area.Left + 20, area.Top + area.Height * .25 - 3, area.Width - 40, 6), pressure) > 100);
            Assert.Equal(0, CountColor(pixels, new Rect(area.Left + 20, area.Top + area.Height * .25 - 3, area.Width - 40, 6), temperature));
            Assert.Equal(0, CountColor(pixels, new Rect(area.Left + 20, area.Top + area.Height * .75 - 3, area.Width - 40, 6), pressure));

            chart.Options = chart.Options.WithAxisVisibility("pressure", false);
            await SettleAsync(chart);
            using var hidden = SKBitmap.Decode(TestUi.Capture(window));
            Assert.Equal(0, CountColor(hidden, area.Deflate(5), pressure));
            Assert.True(CountColor(hidden, area.Deflate(5), temperature) > 100);
            Assert.Equal(0, CountInk(hidden, new Rect(area.Right + 8, area.Top - 10, 88, area.Height + 20), background));
        }
        finally { await CloseAsync(chart, window); }
    }

    [AvaloniaFact]
    public async Task Eight_series_have_distinct_rendered_strokes_and_small_viewport_fallback_does_not_requery()
    {
        var definitions = Enumerable.Range(0, 8).Select(index => new HistorySeriesDefinition($"sensor-{index}",
            $"Sensor {index}", index % 2 == 0 ? "temperature" : "pressure",
            Series(index + 1, 50, _ => (index + 1) * (index % 2 == 0 ? 10d : 1000d))));
        var chart = new DualAxisHistoryPlot
        {
            PointBudget = 64,
            Options = DualAxisChartOptions.Default
                .WithAxisDomain("temperature", new HistoryValueDomain(0, 100))
                .WithAxisDomain("pressure", new HistoryValueDomain(0, 10000))
        };
        chart.SetSource(new DualAxisHistorySource(Axes(), definitions, 1));
        var window = TestUi.Show(chart, 1200, 600);
        try
        {
            await SettleAsync(chart);
            Assert.True(chart.IsPlotViewportAvailable);
            Assert.Equal(8, chart.LegendPresentations.Length);
            Assert.InRange(chart.Snapshot!.PointCount, 16, 64);
            var palette = new[] { "#61D5C3", "#FFBE98", "#6495ED", "#DA70D6", "#DAA520", "#32CD32", "#FF1493", "#6A5ACD" }.Select(SKColor.Parse).ToArray();
            using var pixels = SKBitmap.Decode(TestUi.Capture(window));
            var area = chart.PlotArea;
            for (var index = 0; index < 8; index++)
            {
                var band = new Rect(area.Left + 20, area.Bottom - area.Height * (index + 1) / 10 - 3, area.Width - 40, 6);
                Assert.True(CountColor(pixels, band, palette[index]) > 50, $"Series {index} needs its own visible stroke.");
            }
            var visible = chart.Snapshot;
            var work = chart.WhenIdle;
            var statistics = chart.SourceStatistics;
            var view = chart.ViewWindow;
            window.Height = 300;
            Dispatcher.UIThread.RunJobs();
            Assert.False(chart.IsPlotViewportAvailable);
            Assert.Same(visible, chart.Snapshot);
            Assert.Same(work, chart.WhenIdle);
            Assert.Equal(statistics, chart.SourceStatistics);
            using var small = SKBitmap.Decode(TestUi.Capture(window));
            Assert.True(CountInk(small, new Rect(12, chart.Bounds.Height - 43, 700, 16), SKColor.Parse("#14202E")) > 100,
                "The small viewport needs a visible enlarge notice.");
            Assert.All(palette, color => Assert.Equal(0, CountColor(small, new Rect(104, 220, 900, 30), color)));
            var point = new Point(chart.PlotArea.Left + 50, chart.PlotArea.Top);
            window.MouseWheel(point, new Vector(0, 1));
            TestUi.Click(window, point);
            Assert.Equal(view, chart.ViewWindow);
            Assert.Null(chart.SelectedHit);
            Assert.NotNull(chart.SelectNearest("sensor-7", Epoch));
            window.Height = 600;
            Dispatcher.UIThread.RunJobs();
            Assert.True(chart.IsPlotViewportAvailable);
            Assert.Same(visible, chart.Snapshot);
            Assert.Same(work, chart.WhenIdle);
        }
        finally { await CloseAsync(chart, window); }
    }

    [AvaloniaFact]
    public async Task Fixed_domain_clips_real_lines_without_offscale_boundary_smears_or_drawing_outside_plot()
    {
        var temperature = new HistorySeries(new[]
        {
            new HistorySample(0, Epoch, -100),
            new HistorySample(1, Epoch + 1000, -100),
            new HistorySample(2, Epoch + 2000, 5),
            new HistorySample(3, Epoch + 3000, 100),
            new HistorySample(4, Epoch + 4000, 100)
        }, 1);
        var chart = new DualAxisHistoryPlot
        {
            Options = DualAxisChartOptions.Default
                .WithAxisDomain("temperature", new HistoryValueDomain(0, 10))
                .WithAxisVisibility("pressure", false)
        };
        chart.SetSource(Source(1, temperature, Series(2, 5, _ => 5000)));
        var window = TestUi.Show(chart, 1200, 480);
        try
        {
            await SettleAsync(chart);
            using var pixels = SKBitmap.Decode(TestUi.Capture(window));
            var area = chart.PlotArea;
            var line = SKColor.Parse("#FFBE98");
            Assert.True(CountColor(pixels, area.Deflate(3), line) > 50, "The domain-crossing portion must remain visible.");
            Assert.Equal(0, CountColor(pixels, new Rect(area.Left + 5, area.Top + 2, area.Width * .4, area.Height - 4), line));
            Assert.Equal(0, CountColor(pixels, new Rect(area.Left + area.Width * .65, area.Top + 2, area.Width * .3, area.Height - 4), line));
            // Wholly off-scale edges may not become horizontal strokes on either domain boundary.
            Assert.Equal(0, CountColor(pixels, new Rect(area.Left + 8, area.Bottom - 2, area.Width * .2, 4), line));
            Assert.Equal(0, CountColor(pixels, new Rect(area.Left + area.Width * .8, area.Top - 2, area.Width * .15, 4), line));
            Assert.Equal(0, CountColor(pixels, new Rect(5, area.Top, area.Left - 10, area.Height), line));
            Assert.Equal(0, CountColor(pixels, new Rect(area.Right + 5, area.Top, 90, area.Height), line));
            Assert.Equal(0, CountColor(pixels, new Rect(area.Left, area.Top - 12, area.Width, 10), line));
            Assert.Equal(0, CountColor(pixels, new Rect(area.Left, area.Bottom + 2, area.Width, 7), line));
        }
        finally { await CloseAsync(chart, window); }
    }

    [AvaloniaFact]
    public async Task Hidden_series_rescales_its_axis_and_hidden_axis_suppresses_all_assigned_series()
    {
        var source = new DualAxisHistorySource(Axes(), new[]
        {
            new HistorySeriesDefinition("temperature-series", "Temperature", "temperature", Series(1, 40, i => i / 4d)),
            new HistorySeriesDefinition("hot-series", "Hot sensor", "temperature", Series(2, 40, i => 1000 + i * 25)),
            new HistorySeriesDefinition("pressure-series", "Pressure", "pressure", Series(3, 40, i => 100 + i))
        }, 5);
        var chart = new DualAxisHistoryPlot { PointBudget = 24 };
        chart.SetSource(source);
        var window = TestUi.Show(chart, 1000, 400);
        try
        {
            await SettleAsync(chart);
            var original = TestUi.Capture(window);
            Assert.True(chart.AxisPresentations.Single(axis => axis.AxisId == "temperature").Ticks.Max(tick => tick.Value) > 1500);
            chart.Options = chart.Options.WithSeriesVisibility("hot-series", false);
            await SettleAsync(chart);
            Assert.True(chart.AxisPresentations.Single(axis => axis.AxisId == "temperature").Ticks.Max(tick => tick.Value) < 20);
            Assert.False(chart.LegendPresentations.Single(series => series.SeriesId == "hot-series").IsVisible);
            Assert.Null(chart.SelectNearest("hot-series", Epoch));
            Assert.False(original.SequenceEqual(TestUi.Capture(window)));

            chart.Options = chart.Options.WithAxisVisibility("temperature", false);
            await SettleAsync(chart);
            Assert.DoesNotContain(chart.AxisPresentations, axis => axis.AxisId == "temperature");
            Assert.Null(chart.SelectNearest("temperature-series", Epoch));
            Assert.Null(chart.SelectNearest("hot-series", Epoch));
            Assert.NotNull(chart.SelectNearest("pressure-series", Epoch));
            Assert.All(chart.LegendPresentations.Where(series => series.AxisId == "temperature"), series => Assert.False(series.IsVisible));
            Assert.InRange(chart.Snapshot!.PointCount, 1, chart.PointBudget);

            chart.Options = DualAxisChartOptions.Default;
            await SettleAsync(chart);
            Assert.Equal(2, chart.AxisPresentations.Length);
            Assert.All(chart.LegendPresentations, series => Assert.True(series.IsVisible));
            Assert.True(chart.AxisPresentations.Single(axis => axis.AxisId == "temperature").Ticks.Max(tick => tick.Value) > 1500);
        }
        finally { await CloseAsync(chart, window); }
    }

    [AvaloniaFact]
    public async Task Culture_and_timezone_change_only_presentation_even_while_replacement_is_pending()
    {
        var gate = new QueryGate();
        var chart = new DualAxisHistoryPlot(new SnapshotBudget(3, 12, 3), gate.Query);
        chart.SetSource(SmallSource(1, 1234.5678901234567));
        var window = TestUi.Show(chart, 1200, 400);
        var ambientCulture = CultureInfo.CurrentCulture;
        var ambientUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            await SettleAsync(chart);
            chart.SelectNearest("temperature-series", Epoch);
            var visible = chart.Snapshot;
            var selection = chart.SelectedHit;
            var pixels = TestUi.Capture(window);
            chart.SetSource(SmallSource(2, 4321.25));
            await gate.Entered.Task.WaitAsync(Deadline);
            var statistics = chart.SourceStatistics;
            var work = chart.WhenIdle;
            var view = chart.ViewWindow;
            var queryCount = gate.QueryCount;
            var viewChanges = 0;
            chart.ViewChanged += (_, _) => viewChanges++;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var format = new ValueDisplayFormat("America/New_York", "de-DE", numberFormat: "N2");
            for (var index = 0; index < 10; index++)
                chart.DisplayFormat = index % 2 == 0 ? ValueDisplayFormat.Default : format;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
            Assert.Equal("tr-TR", CultureInfo.CurrentUICulture.Name);
            Assert.Same(visible, chart.Snapshot);
            Assert.Equal(selection, chart.SelectedHit);
            Assert.Same(work, chart.WhenIdle);
            Assert.False(work.IsCompleted);
            Assert.Equal(statistics, chart.SourceStatistics);
            Assert.Equal(queryCount, gate.QueryCount);
            Assert.Equal(view, chart.ViewWindow);
            Assert.Equal(0, viewChanges);
            Assert.Equal(format.FormatTimestamp(visible!.Window.StartTicks), chart.AxisStartLabel);
            Assert.Equal(format.FormatTimestamp(visible.Window.EndTicks - 1), chart.AxisEndLabel);
            Assert.Contains("1.234,57", chart.SelectedDisplayText);
            Assert.Contains(format.FormatTimestamp(Epoch), chart.SelectedDisplayText);
            Assert.All(chart.AxisPresentations.SelectMany(axis => axis.Ticks), tick =>
                Assert.Equal(format.FormatNumber(tick.Value), tick.Text));
            Assert.Contains(Epoch.ToString(CultureInfo.InvariantCulture), chart.AccessibleSummary);
            Assert.Contains("1234.5678901234567", chart.AccessibleSummary);
            Assert.Equal(chart.AccessibleSummary, AutomationProperties.GetHelpText(chart));
            Assert.Equal(chart.AccessibleSummary, ToolTip.GetTip(chart));
            Assert.False(pixels.SequenceEqual(TestUi.Capture(window)));

            gate.Release.TrySetResult();
            await SettleAsync(chart);
            Assert.Equal(2, chart.Snapshot!.DatasetVersion);
            Assert.Equal(4321.25, chart.SelectedHit?.Value);
            Assert.Contains("4.321,25", chart.SelectedDisplayText);
        }
        finally
        {
            CultureInfo.CurrentCulture = ambientCulture;
            CultureInfo.CurrentUICulture = ambientUiCulture;
            gate.Release.TrySetResult();
            await CloseAsync(chart, window);
        }
    }

    [AvaloniaFact]
    public async Task Pointer_selects_series_in_its_own_domain_wheel_preserves_anchor_and_released_drag_stops()
    {
        var source = Source(9, Series(11, 1000, _ => 25), Series(22, 1000, _ => 7500));
        var chart = new DualAxisHistoryPlot
        {
            PointBudget = 20,
            Options = DualAxisChartOptions.Default
                .WithAxisDomain("temperature", new HistoryValueDomain(0, 100))
                .WithAxisDomain("pressure", new HistoryValueDomain(0, 10000))
        };
        chart.SetSource(source);
        var window = TestUi.Show(chart, 1000, 400);
        try
        {
            await SettleAsync(chart);
            var area = chart.PlotArea;
            var point = new Point(area.Left + area.Width * .5, area.Top + area.Height * .25);
            var initial = chart.ViewWindow!.Value;
            var target = initial.StartTicks + (long)(.5 * (initial.DurationTicks - 1));
            TestUi.Click(window, point);
            Assert.True(chart.IsFocused);
            Assert.Equal("pressure-series", chart.SelectedHit?.SeriesId);
            Assert.Equal(7500, chart.SelectedHit?.Value);
            Assert.Equal(22, chart.SelectedHit?.SeriesSourceVersion);
            Assert.InRange(Math.Abs(chart.SelectedHit!.Value.UtcTicks - target), 0, 500);

            var anchorPoint = new Point(area.Left + area.Width * .25, area.Top + area.Height * .5);
            var anchor = initial.StartTicks + (long)(.25 * (initial.DurationTicks - 1));
            window.MouseWheel(anchorPoint, new Vector(0, 1));
            await SettleAsync(chart);
            var zoomed = chart.ViewWindow!.Value;
            Assert.True(zoomed.DurationTicks < initial.DurationTicks);
            var beforeFraction = (anchor - initial.StartTicks) / (double)initial.DurationTicks;
            var afterFraction = (anchor - zoomed.StartTicks) / (double)zoomed.DurationTicks;
            Assert.InRange(Math.Abs(beforeFraction - afterFraction), 0, 2d / zoomed.DurationTicks);

            var dragStart = new Point(area.Left + area.Width * .5, area.Top + area.Height * .5);
            var dragEnd = new Point(dragStart.X - 60, dragStart.Y);
            window.MouseDown(dragStart, MouseButton.Left);
            window.MouseMove(dragEnd);
            window.MouseUp(dragEnd, MouseButton.Left);
            await SettleAsync(chart);
            var expected = zoomed.Pan((long)(60d / area.Width * zoomed.DurationTicks), source.Extent!.Value);
            Assert.Equal(expected, chart.ViewWindow);
            window.MouseMove(new Point(dragEnd.X - 60, dragEnd.Y));
            Assert.Equal(expected, chart.ViewWindow);
            Assert.Equal(chart.ViewWindow, chart.Snapshot!.Window);
        }
        finally { await CloseAsync(chart, window); }
    }

    [AvaloniaFact]
    public async Task Keyboard_zoom_pan_and_home_keep_both_series_on_one_bounded_time_window()
    {
        var source = Source(7, Series(1, 1000, i => i), Series(2, 1000, i => i * 100));
        var chart = new DualAxisHistoryPlot();
        chart.SetSource(source);
        var window = TestUi.Show(chart);
        try
        {
            await SettleAsync(chart);
            Assert.True(chart.Focus());
            TestUi.Press(window, Key.Add);
            var zoomed = chart.ViewWindow!.Value;
            Assert.True(zoomed.DurationTicks < source.Extent!.Value.DurationTicks);
            TestUi.Press(window, Key.Right);
            Assert.True(chart.ViewWindow!.Value.StartTicks > zoomed.StartTicks);
            TestUi.Press(window, Key.Left);
            Assert.InRange(chart.ViewWindow!.Value.StartTicks, source.Extent.Value.StartTicks, source.Extent.Value.EndTicks);
            TestUi.Press(window, Key.Subtract);
            Assert.True(chart.ViewWindow!.Value.DurationTicks > zoomed.DurationTicks);
            chart.Zoom(4);
            chart.Pan(double.MaxValue);
            Assert.Equal(source.Extent.Value.EndTicks, chart.ViewWindow!.Value.EndTicks);
            chart.Pan(-double.MaxValue);
            Assert.Equal(source.Extent.Value.StartTicks, chart.ViewWindow!.Value.StartTicks);
            TestUi.Press(window, Key.Home);
            await SettleAsync(chart);
            Assert.Equal(source.Extent, chart.ViewWindow);
            Assert.Equal(source.Extent, chart.Snapshot!.Window);
            Assert.Equal(2, chart.AxisPresentations.Length);
        }
        finally { await CloseAsync(chart, window); }
    }

    [AvaloniaTheory]
    [InlineData("escape")]
    [InlineData("focus")]
    [InlineData("capture")]
    [InlineData("capture-transfer")]
    [InlineData("deactivate")]
    [InlineData("resize-width")]
    [InlineData("resize-height")]
    [InlineData("detach")]
    public async Task Interrupted_drag_cannot_continue_after_escape_focus_capture_or_detach(string interruption)
    {
        var chart = new DualAxisHistoryPlot();
        chart.SetSource(Source(1, Series(1, 1000, i => i), Series(2, 1000, i => i * 100)));
        var other = new Button { Content = "Other focus target" };
        var host = new global::Avalonia.Controls.Grid { RowDefinitions = new RowDefinitions("*,40") };
        host.Children.Add(chart);
        global::Avalonia.Controls.Grid.SetRow(other, 1);
        host.Children.Add(other);
        var window = TestUi.Show(host, 1000, 440);
        IPointer? pointer = null;
        chart.AddHandler(InputElement.PointerPressedEvent, (_, args) => pointer = args.Pointer,
            RoutingStrategies.Bubble, handledEventsToo: true);
        try
        {
            await SettleAsync(chart);
            chart.Zoom(2);
            await SettleAsync(chart);
            var original = chart.ViewWindow;
            var area = chart.PlotArea;
            var start = new Point(area.Left + area.Width * .5, area.Top + area.Height * .5);
            var moved = new Point(start.X - 50, start.Y);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(moved);
            Assert.NotEqual(original, chart.ViewWindow);
            switch (interruption)
            {
                case "escape": TestUi.Press(window, Key.Escape); break;
                case "focus": Assert.True(other.Focus()); break;
                case "capture": Assert.NotNull(pointer); pointer.Capture(null); break;
                case "capture-transfer":
                    Assert.NotNull(pointer);
                    pointer.Capture(other);
                    Assert.Same(other, pointer.Captured);
                    break;
                case "deactivate":
                    typeof(WindowBase).GetMethod("HandleDeactivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
                    break;
                case "resize-width": window.Width = 300; Dispatcher.UIThread.RunJobs(); break;
                case "resize-height": window.Height = 290; Dispatcher.UIThread.RunJobs(); break;
                case "detach": host.Children.Remove(chart); break;
                default: Assert.Fail("Unknown interruption."); break;
            }
            var stopped = chart.ViewWindow;
            if (interruption != "detach") Assert.Equal(original, stopped);
            window.MouseMove(new Point(start.X - 100, start.Y));
            window.MouseUp(new Point(start.X - 100, start.Y), MouseButton.Left);
            Assert.Equal(stopped, chart.ViewWindow);
            if (interruption == "detach") host.Children.Add(chart);
            await SettleAsync(chart);
            Assert.Equal(stopped, chart.ViewWindow);
            Assert.Equal(stopped, chart.Snapshot!.Window);
        }
        finally { await CloseAsync(chart, window); }
    }

    [AvaloniaFact]
    public async Task Full_calendar_right_edge_click_and_wheel_never_round_past_the_last_valid_tick()
    {
        var samples = new[]
        {
            new HistorySample(0, 0, 1),
            new HistorySample(1, TimeWindow.MaximumExclusiveTicks - 1, 2)
        };
        var chart = new DualAxisHistoryPlot();
        chart.SetSource(Source(1, new HistorySeries(samples, 11), new HistorySeries(samples, 22)));
        var window = TestUi.Show(chart, 1200, 480);
        try
        {
            await SettleAsync(chart);
            var initial = chart.ViewWindow!.Value;
            var area = chart.PlotArea;
            var point = new Point(area.Right, area.Top + 2);
            TestUi.Click(window, point);
            Assert.Equal(TimeWindow.MaximumExclusiveTicks - 1, chart.SelectedHit?.UtcTicks);
            window.MouseWheel(point, new Vector(0, 1));
            await SettleAsync(chart);
            Assert.True(chart.ViewWindow!.Value.DurationTicks < initial.DurationTicks);
            Assert.Equal(TimeWindow.MaximumExclusiveTicks, chart.ViewWindow.Value.EndTicks);
            Assert.Equal(chart.ViewWindow, chart.Snapshot!.Window);
            TestUi.Capture(window);
        }
        finally { await CloseAsync(chart, window); }
    }

    [AvaloniaFact]
    public async Task Aggregate_gap_budget_failure_paints_no_false_continuous_lines()
    {
        var chart = new DualAxisHistoryPlot { PointBudget = 4 };
        chart.SetSource(Source(1, Series(1, 9, i => i % 2 == 0 ? i : null), Series(2, 9, i => i % 2 == 0 ? i * 100 : null)));
        var window = TestUi.Show(chart, 1200, 480);
        try
        {
            await SettleAsync(chart);
            Assert.Equal(ChartBudgetStatus.InsufficientBudget, chart.Snapshot!.BudgetStatus);
            Assert.Equal(10, chart.Snapshot.RequiredPointBudget);
            Assert.Equal(0, chart.Snapshot.PointCount);
            Assert.All(chart.Snapshot.Series, series =>
            {
                Assert.Empty(series.Segments);
                Assert.Equal(4, series.Gaps.Length);
            });
            Assert.Contains("too small", chart.Status);
            using var pixels = SKBitmap.Decode(TestUi.Capture(window));
            Assert.Equal(0, CountColor(pixels, chart.PlotArea.Deflate(4), SKColor.Parse("#61D5C3")));
            Assert.Equal(0, CountColor(pixels, chart.PlotArea.Deflate(4), SKColor.Parse("#FFBE98")));
        }
        finally { await CloseAsync(chart, window); }
    }

    [AvaloniaFact]
    public async Task Selection_reconciliation_uses_series_and_stable_key_when_replacement_timestamps_collide()
    {
        var gate = new QueryGate();
        var original = Source(1, new HistorySeries(new[] { new HistorySample(42, Epoch + 1000, 123) }, 11), Series(12, 2, _ => 1000));
        var replacement = Source(2, new HistorySeries(new[]
        {
            new HistorySample(1, Epoch, 1), new HistorySample(42, Epoch, 987.123456789)
        }, 21), Series(22, 2, _ => 2000));
        var chart = new DualAxisHistoryPlot(new SnapshotBudget(3, 20, 3), gate.Query);
        chart.SetSource(original);
        var window = TestUi.Show(chart);
        try
        {
            await SettleAsync(chart);
            Assert.Equal(42, chart.SelectNearest("temperature-series", Epoch + 1000)?.Key);
            chart.SetSource(replacement);
            await gate.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(123, chart.SelectedHit?.Value);
            Assert.Equal(1, chart.SelectedHit?.DatasetVersion);
            gate.Release.TrySetResult();
            await SettleAsync(chart);
            Assert.Equal(42, chart.SelectedHit?.Key);
            Assert.Equal("temperature-series", chart.SelectedHit?.SeriesId);
            Assert.Equal(2, chart.SelectedHit?.DatasetVersion);
            Assert.Equal(21, chart.SelectedHit?.SeriesSourceVersion);
            Assert.Equal(Epoch, chart.SelectedHit?.UtcTicks);
            Assert.Equal(987.123456789, chart.SelectedHit?.Value);
            chart.SetSource(Source(3, new HistorySeries(new[] { new HistorySample(1, Epoch, 9) }, 31), Series(32, 2, _ => 3000)));
            await SettleAsync(chart);
            Assert.Null(chart.SelectedHit);
        }
        finally { gate.Release.TrySetResult(); await CloseAsync(chart, window); }
    }

    [AvaloniaFact]
    public async Task Pending_options_do_not_relabel_or_hide_the_old_display_before_commit()
    {
        var gate = new QueryGate();
        var chart = new DualAxisHistoryPlot(new SnapshotBudget(3, 12, 3), gate.Query);
        chart.SetSource(SmallSource(1));
        var window = TestUi.Show(chart);
        try
        {
            await SettleAsync(chart);
            chart.SelectNearest("temperature-series", Epoch);
            var visible = chart.Snapshot!;
            var originalAxes = chart.AxisPresentations;
            var originalLegends = chart.LegendPresentations;
            chart.SetSource(SmallSource(2, 20));
            await gate.Entered.Task.WaitAsync(Deadline);
            chart.Options = chart.Options.WithAxisVisibility("temperature", false)
                .WithAxisDomain("pressure", new HistoryValueDomain(0, 10000));
            Assert.Same(visible, chart.Snapshot);
            Assert.NotSame(chart.Options, visible.Options);
            Assert.Equal(originalAxes.Select(axis => axis.Label), chart.AxisPresentations.Select(axis => axis.Label));
            Assert.Equal(originalAxes.SelectMany(axis => axis.Ticks), chart.AxisPresentations.SelectMany(axis => axis.Ticks));
            Assert.Equal(originalLegends, chart.LegendPresentations);
            Assert.Equal(10, chart.SelectNearest("temperature-series", Epoch)?.Value);
            Assert.Contains("Loading", chart.Status);
            Assert.Contains("Loading", AutomationProperties.GetHelpText(chart));
            Assert.Contains("Loading", Assert.IsType<string>(ToolTip.GetTip(chart)));
            gate.Release.TrySetResult();
            await SettleAsync(chart);
            Assert.Same(chart.Options, chart.Snapshot!.Options);
            Assert.DoesNotContain(chart.AxisPresentations, axis => axis.AxisId == "temperature");
            Assert.Null(chart.SelectedHit);
            Assert.Null(chart.SelectNearest("temperature-series", Epoch));
        }
        finally { gate.Release.TrySetResult(); await CloseAsync(chart, window); }
    }

    [AvaloniaTheory]
    [InlineData("empty")]
    [InlineData("gaps")]
    [InlineData("constant")]
    [InlineData("extreme")]
    public async Task Empty_gap_constant_and_extreme_sources_render_without_nonfinite_ticks(string scenario)
    {
        var samples = scenario switch
        {
            "empty" => Array.Empty<HistorySample>(),
            "gaps" => new[] { new HistorySample(0, Epoch, null), new HistorySample(1, Epoch + 1000, double.NaN), new HistorySample(2, Epoch + 2000, double.PositiveInfinity) },
            "constant" => new[] { new HistorySample(0, Epoch, 7.25), new HistorySample(1, Epoch + 1000, 7.25), new HistorySample(2, Epoch + 2000, 7.25) },
            "extreme" => new[] { new HistorySample(0, Epoch, -double.MaxValue), new HistorySample(1, Epoch + 1000, 0), new HistorySample(2, Epoch + 2000, double.MaxValue) },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var chart = new DualAxisHistoryPlot();
        chart.SetSource(SmallSource(1));
        var window = TestUi.Show(chart, 1100, 400);
        try
        {
            await SettleAsync(chart);
            chart.SetSource(Source(2, new HistorySeries(samples, 11), new HistorySeries(samples, 22)));
            await SettleAsync(chart);
            Assert.All(chart.AxisPresentations.SelectMany(axis => axis.Ticks), tick =>
            {
                Assert.True(double.IsFinite(tick.Value));
                Assert.True(double.IsFinite(tick.Position));
                Assert.DoesNotContain("NaN", tick.Text);
                Assert.DoesNotContain("Infinity", tick.Text);
            });
            if (scenario == "empty")
            {
                Assert.Null(chart.ViewWindow);
                Assert.Null(chart.Snapshot);
                Assert.Null(chart.SelectNearest("temperature-series", Epoch));
            }
            else if (scenario == "gaps")
            {
                Assert.Equal(0, chart.Snapshot!.PointCount);
                Assert.Null(chart.SelectNearest("temperature-series", Epoch));
            }
            else
            {
                Assert.Equal(6, chart.Snapshot!.PointCount);
                Assert.Equal(samples[^1].Value, chart.SelectNearest("temperature-series", samples[^1].UtcTicks)?.Value);
            }
            Assert.DoesNotContain("error", chart.Status, StringComparison.OrdinalIgnoreCase);
            TestUi.Capture(window);
        }
        finally { await CloseAsync(chart, window); }
    }

    [AvaloniaFact]
    public async Task Newest_dataset_wins_while_displayed_hits_and_running_query_ownership_remain_exact()
    {
        var gate = new QueryGate();
        var original = SmallSource(1, 10);
        var chart = new DualAxisHistoryPlot(new SnapshotBudget(3, 12, 3), gate.Query);
        chart.SetSource(original);
        var window = TestUi.Show(chart);
        try
        {
            await SettleAsync(chart);
            chart.SelectNearest("temperature-series", Epoch);
            chart.SetSource(SmallSource(2, 20));
            await gate.Entered.Task.WaitAsync(Deadline);
            for (var version = 3; version <= 100; version++)
                Assert.Equal(SnapshotPublishStatus.Published, chart.SetSource(SmallSource(version, version * 10)));
            Assert.True(gate.Token.CanBeCanceled);
            Assert.True(gate.Token.IsCancellationRequested);
            Assert.Same(original, chart.Snapshot!.Source);
            Assert.Equal(1, chart.Snapshot.DatasetVersion);
            Assert.Equal(10, chart.SelectNearest("temperature-series", Epoch)?.Value);
            Assert.Equal(1, chart.SelectedHit?.DatasetVersion);
            Assert.Equal(101, chart.SelectedHit?.SeriesSourceVersion);
            Assert.Equal(3, chart.SourceStatistics.RetainedVersions);
            Assert.Equal(12, chart.SourceStatistics.RetainedItems);
            Assert.Equal(1, chart.SourceStatistics.QueryLeases);
            Assert.Equal(1, chart.SourceStatistics.DisplayLeases);
            Assert.Equal(1, chart.SourceStatistics.PendingLeases);
            Assert.False(chart.WhenIdle.IsCompleted);
            gate.Release.TrySetResult();
            await SettleAsync(chart);
            Assert.Equal(100, chart.Snapshot!.DatasetVersion);
            Assert.Equal(1000, chart.SelectedHit?.Value);
            Assert.Equal(100, chart.SelectedHit?.DatasetVersion);
            Assert.Equal(10001, chart.SelectedHit?.SeriesSourceVersion);
            Assert.Equal(1, chart.SourceStatistics.RetainedVersions);
            Assert.Equal(1, chart.SourceStatistics.ActiveLeases);
            Assert.Equal(3, chart.SourceStatistics.VersionHighWaterMark);
        }
        finally { gate.Release.TrySetResult(); await CloseAsync(chart, window); }
    }

    [AvaloniaFact]
    public async Task Release_cancels_but_cannot_falsely_release_an_uncooperative_query_or_resurrect_its_frame()
    {
        var gate = new QueryGate();
        var chart = new DualAxisHistoryPlot(new SnapshotBudget(3, 12, 3), gate.Query);
        chart.SetSource(SmallSource(1));
        var window = TestUi.Show(chart);
        try
        {
            await SettleAsync(chart);
            chart.SelectNearest("pressure-series", Epoch);
            chart.SetSource(SmallSource(2));
            await gate.Entered.Task.WaitAsync(Deadline);
            chart.ReleaseSource();
            Assert.True(gate.Token.IsCancellationRequested);
            Assert.Null(chart.Snapshot);
            Assert.Null(chart.SelectedHit);
            Assert.Null(chart.SelectNearest("pressure-series", Epoch));
            Assert.Equal(1, chart.SourceStatistics.ActiveLeases);
            Assert.Equal(1, chart.SourceStatistics.QueryLeases);
            Assert.Equal(1, chart.SourceStatistics.RetainedVersions);
            Assert.Equal(4, chart.SourceStatistics.RetainedItems);
            Assert.False(chart.WhenIdle.IsCompleted);
            gate.Release.TrySetResult();
            await SettleAsync(chart);
            Assert.Null(chart.Snapshot);
            Assert.Equal(0, chart.SourceStatistics.ActiveLeases);
            Assert.Equal(0, chart.SourceStatistics.RetainedItems);
            chart.SetSource(SmallSource(3, 30));
            await SettleAsync(chart);
            Assert.Equal(3, chart.Snapshot!.DatasetVersion);
            Assert.Equal(30, chart.SelectNearest("temperature-series", Epoch)?.Value);
        }
        finally { gate.Release.TrySetResult(); await CloseAsync(chart, window); }
    }

    [AvaloniaFact]
    public async Task Detach_invalidates_inflight_work_and_reattach_renders_only_latest_accepted_dataset()
    {
        var gate = new QueryGate();
        var chart = new DualAxisHistoryPlot(new SnapshotBudget(3, 12, 3), gate.Query);
        chart.SetSource(SmallSource(1));
        var window = TestUi.Show(chart);
        try
        {
            await SettleAsync(chart);
            chart.SetSource(SmallSource(2));
            await gate.Entered.Task.WaitAsync(Deadline);
            var commits = 0;
            chart.StateChanged += (_, _) => { if (chart.Snapshot?.DatasetVersion != 1) commits++; };
            window.Content = null;
            Assert.True(gate.Token.IsCancellationRequested);
            Assert.Equal(1, chart.SourceStatistics.QueryLeases);
            Assert.False(chart.WhenIdle.IsCompleted);
            chart.SetSource(SmallSource(3, 30));
            gate.Release.TrySetResult();
            await SettleAsync(chart);
            Assert.Equal(0, commits);
            Assert.Equal(1, chart.Snapshot!.DatasetVersion);
            Assert.Equal(10, chart.SelectNearest("temperature-series", Epoch)?.Value);
            window.Content = chart;
            await SettleAsync(chart);
            Assert.Equal(3, chart.Snapshot!.DatasetVersion);
            Assert.Equal(30, chart.SelectNearest("temperature-series", Epoch)?.Value);
            Assert.True(commits > 0);
        }
        finally { gate.Release.TrySetResult(); await CloseAsync(chart, window); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_callbacks_remain_charged_until_they_return_even_after_query_returns(bool callbackThrows)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queryRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queryReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        var chart = new DualAxisHistoryPlot(new SnapshotBudget(3, 12, 3), async (source, view, options, budget, token) =>
        {
            if (source.DatasetVersion == 2)
            {
                registration = token.Register(() =>
                {
                    cancellationEntered.TrySetResult();
                    cancellationRelease.Task.GetAwaiter().GetResult();
                    if (callbackThrows) throw new InvalidOperationException("Deliberate cancellation callback failure.");
                });
                entered.TrySetResult();
                await queryRelease.Task;
            }
            var snapshot = source.Query(view, budget, options);
            if (source.DatasetVersion == 2) queryReturned.TrySetResult();
            return snapshot;
        });
        chart.SetSource(SmallSource(1));
        var window = TestUi.Show(chart);
        try
        {
            await SettleAsync(chart);
            chart.SetSource(SmallSource(2));
            await entered.Task.WaitAsync(Deadline);
            chart.ReleaseSource();
            await cancellationEntered.Task.WaitAsync(Deadline);
            queryRelease.TrySetResult();
            await queryReturned.Task.WaitAsync(Deadline);
            Assert.Null(chart.Snapshot);
            Assert.False(chart.WhenIdle.IsCompleted);
            Assert.Equal(1, chart.SourceStatistics.QueryLeases);
            Assert.Equal(4, chart.SourceStatistics.RetainedItems);
            cancellationRelease.TrySetResult();
            await SettleAsync(chart);
            Assert.Null(chart.Snapshot);
            Assert.Equal(0, chart.SourceStatistics.ActiveLeases);
            Assert.Equal(0, chart.SourceStatistics.RetainedItems);
            chart.SetSource(SmallSource(3, 30));
            await SettleAsync(chart);
            Assert.Equal(3, chart.Snapshot!.DatasetVersion);
        }
        finally
        {
            queryRelease.TrySetResult();
            cancellationRelease.TrySetResult();
            await CloseAsync(chart, window);
            registration.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Rejected_dataset_preserves_the_visible_source_and_exact_raw_values()
    {
        var original = SmallSource(1, 10);
        var chart = new DualAxisHistoryPlot(new SnapshotBudget(3, 4, 3));
        chart.SetSource(original);
        var window = TestUi.Show(chart);
        try
        {
            await SettleAsync(chart);
            Assert.Equal(SnapshotPublishStatus.BudgetExceeded, chart.SetSource(SmallSource(2, 20)));
            Assert.Equal(SnapshotPublishStatus.BudgetExceeded, chart.LastSourceStatus);
            Assert.Same(original, chart.Snapshot!.Source);
            Assert.Equal(1, chart.Snapshot.DatasetVersion);
            Assert.Equal(10, chart.SelectNearest("temperature-series", Epoch)?.Value);
            Assert.Equal(4, chart.SourceStatistics.RetainedItems);
            Assert.Equal(1, chart.SourceStatistics.Rejected);
            Assert.Contains("budget exceeded", chart.Status, StringComparison.OrdinalIgnoreCase);
            await SettleAsync(chart);
            Assert.Same(original, chart.Snapshot!.Source);
            chart.ReleaseSource();
            await SettleAsync(chart);
            Assert.Equal(SnapshotPublishStatus.Published, chart.SetSource(SmallSource(2, 20)));
            await SettleAsync(chart);
            Assert.Equal(20, chart.SelectNearest("temperature-series", Epoch)?.Value);
        }
        finally { await CloseAsync(chart, window); }
    }

    [AvaloniaFact]
    public async Task Rejecting_an_oversized_replacement_does_not_discard_the_previously_accepted_pending_dataset()
    {
        var gate = new QueryGate();
        var chart = new DualAxisHistoryPlot(new SnapshotBudget(3, 12, 3), gate.Query);
        chart.SetSource(SmallSource(1));
        var window = TestUi.Show(chart);
        try
        {
            await SettleAsync(chart);
            chart.SetSource(SmallSource(2));
            await gate.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(SnapshotPublishStatus.Published, chart.SetSource(SmallSource(3, 30)));
            var oversized = Source(4, Series(401, 8, i => i), Series(402, 8, i => i));
            Assert.Equal(SnapshotPublishStatus.BudgetExceeded, chart.SetSource(oversized));
            Assert.Equal(1, chart.SourceStatistics.PendingLeases);
            Assert.Equal(12, chart.SourceStatistics.RetainedItems);
            gate.Release.TrySetResult();
            await SettleAsync(chart);
            Assert.Equal(3, chart.Snapshot!.DatasetVersion);
            Assert.Equal(30, chart.SelectNearest("temperature-series", Epoch)?.Value);
            Assert.Equal(1, chart.SourceStatistics.RetainedVersions);
        }
        finally { gate.Release.TrySetResult(); await CloseAsync(chart, window); }
    }

    [AvaloniaFact]
    public void Lease_budget_must_allow_display_running_and_latest_owners()
    {
        Assert.Throws<ArgumentException>(() => new DualAxisHistoryPlot(new SnapshotBudget(3, 12, 1)));
        Assert.Throws<ArgumentException>(() => new DualAxisHistoryPlot(new SnapshotBudget(3, 12, 2)));
    }

    [AvaloniaTheory]
    [InlineData("source")]
    [InlineData("window")]
    [InlineData("options")]
    [InlineData("budget")]
    public async Task Foreign_provider_results_are_rejected_without_rebinding_displayed_data_or_selection(string mismatch)
    {
        var original = SmallSource(1, 10);
        var chart = new DualAxisHistoryPlot(new SnapshotBudget(3, 12, 3), (source, view, options, budget, token) =>
        {
            if (source.DatasetVersion != 2) return Task.FromResult(source.Query(view, budget, options, token));
            var result = mismatch switch
            {
                // The same public dataset version does not make another source object trustworthy.
                "source" => SmallSource(2, 999).Query(view, budget, options),
                "window" => source.Query(view.Zoom(2, view.StartTicks, source.Extent!.Value), budget, options),
                "options" => source.Query(view, budget, options.WithSeriesVisibility("temperature-series", false)),
                "budget" => source.Query(view, budget + 1, options),
                _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
            };
            return Task.FromResult(result);
        });
        chart.SetSource(original);
        var window = TestUi.Show(chart);
        try
        {
            await SettleAsync(chart);
            var snapshot = chart.Snapshot;
            chart.SelectNearest("temperature-series", Epoch);
            var selected = chart.SelectedHit;
            chart.SetSource(SmallSource(2, 20));
            await SettleAsync(chart);
            Assert.Contains("Chart error", chart.Status);
            Assert.Same(snapshot, chart.Snapshot);
            Assert.Same(original, chart.Snapshot!.Source);
            Assert.Equal(selected, chart.SelectedHit);
            Assert.Equal(10, chart.SelectNearest("temperature-series", Epoch)?.Value);
            Assert.Equal(1, chart.SelectedHit?.DatasetVersion);
            Assert.Equal(0, chart.SourceStatistics.QueryLeases);
            Assert.Equal(0, chart.SourceStatistics.PendingLeases);
            Assert.Equal(1, chart.SourceStatistics.DisplayLeases);
        }
        finally { await CloseAsync(chart, window); }
    }

    private static HistoryAxisDefinition[] Axes() =>
    [
        new("temperature", "Temperature", "°C", HistoryAxisSide.Left),
        new("pressure", "Pressure", "kPa", HistoryAxisSide.Right)
    ];

    private static HistorySeries Series(long version, int count, Func<int, double?> value) =>
        new(Enumerable.Range(0, count).Select(i => new HistorySample(i, Epoch + i * 1000, value(i))), version);

    private static DualAxisHistorySource Source(long version, HistorySeries temperature, HistorySeries pressure) => new(Axes(),
    [
        new HistorySeriesDefinition("temperature-series", "Temperature sensor", "temperature", temperature),
        new HistorySeriesDefinition("pressure-series", "Pressure sensor", "pressure", pressure)
    ], version);

    private static DualAxisHistorySource SmallSource(long version, double value = 10) =>
        Source(version, Series(version * 100 + 1, 2, i => value + i), Series(version * 100 + 2, 2, i => value * 100 + i));

    private static int CountColor(SKBitmap image, Rect bounds, SKColor color)
    {
        var count = 0;
        for (var y = Math.Max(0, (int)Math.Ceiling(bounds.Top)); y < Math.Min(image.Height, (int)Math.Floor(bounds.Bottom)); y++)
        for (var x = Math.Max(0, (int)Math.Ceiling(bounds.Left)); x < Math.Min(image.Width, (int)Math.Floor(bounds.Right)); x++)
        {
            var actual = image.GetPixel(x, y);
            // A 1.8 px horizontal pen on an integer Y has no fully opaque interior row.
            // Allow its antialias coverage while remaining far from the other series/background.
            if (Math.Abs(actual.Red - color.Red) <= 32 && Math.Abs(actual.Green - color.Green) <= 32
                && Math.Abs(actual.Blue - color.Blue) <= 32) count++;
        }
        return count;
    }

    private static int CountInk(SKBitmap image, Rect bounds, SKColor background)
    {
        var count = 0;
        for (var y = Math.Max(0, (int)Math.Ceiling(bounds.Top)); y < Math.Min(image.Height, (int)Math.Floor(bounds.Bottom)); y++)
        for (var x = Math.Max(0, (int)Math.Ceiling(bounds.Left)); x < Math.Min(image.Width, (int)Math.Floor(bounds.Right)); x++)
            if (image.GetPixel(x, y) != background) count++;
        return count;
    }

    private static async Task SettleAsync(DualAxisHistoryPlot chart)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < Deadline)
        {
            var work = chart.WhenIdle;
            await work.WaitAsync(Deadline);
            Dispatcher.UIThread.RunJobs();
            if (ReferenceEquals(work, chart.WhenIdle) && chart.WhenIdle.IsCompleted) return;
        }
        Assert.Fail("The dual-axis chart did not reach its latest idle render revision.");
    }

    private static async Task CloseAsync(DualAxisHistoryPlot chart, Window window)
    {
        chart.ReleaseSource();
        window.Close();
        await chart.WhenIdle.WaitAsync(Deadline);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class QueryGate
    {
        private int _queries;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public int QueryCount => Volatile.Read(ref _queries);

        public async Task<DualAxisChartSnapshot> Query(DualAxisHistorySource source, TimeWindow window,
            DualAxisChartOptions options, int budget, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _queries);
            if (source.DatasetVersion == 2)
            {
                Token = cancellationToken;
                Entered.TrySetResult();
                // Deliberately ignore cancellation until released. Ownership must remain charged.
                await Release.Task;
            }
            return source.Query(window, budget, options, CancellationToken.None);
        }
    }
}
