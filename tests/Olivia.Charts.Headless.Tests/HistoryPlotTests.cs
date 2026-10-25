using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Olivia.Avalonia;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Headless.Tests;

public sealed class HistoryPlotTests
{
    private const long Epoch = 638956800000000000;

    private static HistorySeries Series(int count = 1000, long version = 1) => new(
        Enumerable.Range(0, count).Select(i => new HistorySample(i, Epoch + i * 1000, Math.Sin(i * .03))), version);

    [AvaloniaFact]
    public async Task Display_is_bounded_and_preserves_explicit_gap_segments_and_raw_selection()
    {
        var source = new HistorySeries(Enumerable.Range(0, 1000).Select(i =>
            new HistorySample(i, Epoch + i * 1000, i is >= 400 and < 410 ? null : Math.Sin(i * .03))), 7);
        var chart = new HistoryPlot { PointBudget = 24 };
        chart.SetSeries(source);
        var window = TestUi.Show(chart);
        try
        {
            await TestUi.SettleAsync(chart);
            var snapshot = Assert.IsType<HistoryChartSnapshot>(chart.Snapshot);
            Assert.Equal(7, snapshot.SourceVersion);
            Assert.Equal(1000, snapshot.RawSampleCount);
            Assert.Equal(24, snapshot.PointCount);
            Assert.Equal(ChartBudgetStatus.Satisfied, snapshot.BudgetStatus);
            Assert.Equal(2, snapshot.Segments.Length);
            var gap = Assert.Single(snapshot.Gaps);
            Assert.Equal(10, gap.SampleCount);
            Assert.Equal(HistoryGapReason.Missing, gap.Reasons);
            Assert.All(snapshot.Segments.SelectMany(segment => segment.Points), point =>
                Assert.Equal(source.Samples[point.RawIndex], point.Sample));
            Assert.DoesNotContain(snapshot.Segments, segment =>
                segment.RawStartIndex < 400 && segment.RawEndIndexExclusive > 410);

            var displayedKeys = snapshot.Segments.SelectMany(segment => segment.Points).Select(point => point.Key).ToHashSet();
            var undisplayed = source.Samples.First(sample => !sample.IsGap && !displayedKeys.Contains(sample.Key));
            Assert.Equal(undisplayed, chart.SelectNearest(undisplayed.UtcTicks));
            Assert.Equal(undisplayed, chart.SelectedSample);
            TestUi.Capture(window);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Insufficient_gap_budget_is_explicit_instead_of_drawing_a_false_continuous_line()
    {
        var chart = new HistoryPlot { PointBudget = 2 };
        chart.SetSeries(new HistorySeries(Enumerable.Range(0, 9).Select(i =>
            new HistorySample(i, Epoch + i, i % 2 == 0 ? i : null))));
        var window = TestUi.Show(chart);
        try
        {
            await TestUi.SettleAsync(chart);
            var snapshot = Assert.IsType<HistoryChartSnapshot>(chart.Snapshot);
            Assert.Equal(ChartBudgetStatus.InsufficientBudget, snapshot.BudgetStatus);
            Assert.Equal(5, snapshot.RequiredPointBudget);
            Assert.Empty(snapshot.Segments);
            Assert.Equal(4, snapshot.Gaps.Length);
            Assert.Contains("too small", chart.Status);
            TestUi.Capture(window);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Pointer_selects_exact_raw_sample_and_wheel_zooms_the_view()
    {
        var source = Series();
        var chart = new HistoryPlot { PointBudget = 20 };
        chart.SetSeries(source);
        var window = TestUi.Show(chart);
        try
        {
            await TestUi.SettleAsync(chart);
            var point = new Point(64 + (chart.Bounds.Width - 88) * .5, 80);
            var initial = chart.ViewWindow!.Value;
            var expectedTicks = initial.StartTicks + (long)(.5 * (initial.DurationTicks - 1));
            TestUi.Click(window, point);
            Assert.Equal(source.FindNearest(expectedTicks, initial), chart.SelectedSample);
            Assert.True(chart.IsFocused);
            window.MouseWheel(point, new Vector(0, 1));
            await TestUi.SettleAsync(chart);
            Assert.True(chart.ViewWindow!.Value.DurationTicks < initial.DurationTicks);
            Assert.Equal(chart.ViewWindow, chart.Snapshot!.Window);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Wheel_keeps_the_pointer_anchor_and_drag_pan_ends_on_release_or_detach()
    {
        var source = Series();
        var chart = new HistoryPlot();
        chart.SetSeries(source);
        var window = TestUi.Show(chart);
        try
        {
            await TestUi.SettleAsync(chart);
            var plotWidth = chart.Bounds.Width - 88;
            var anchorPoint = new Point(64 + plotWidth * .25, 80);
            var before = chart.ViewWindow!.Value;
            var anchor = before.StartTicks + (long)(.25 * (before.DurationTicks - 1));
            window.MouseWheel(anchorPoint, new Vector(0, 1));
            await TestUi.SettleAsync(chart);
            var zoomed = chart.ViewWindow!.Value;
            var fractionBefore = (anchor - before.StartTicks) / (double)before.DurationTicks;
            var fractionAfter = (anchor - zoomed.StartTicks) / (double)zoomed.DurationTicks;
            Assert.InRange(Math.Abs(fractionBefore - fractionAfter), 0, 2d / zoomed.DurationTicks);

            var dragStart = new Point(64 + plotWidth * .5, 80);
            var dragEnd = new Point(dragStart.X - 60, dragStart.Y);
            window.MouseDown(dragStart, MouseButton.Left);
            window.MouseMove(dragEnd);
            window.MouseUp(dragEnd, MouseButton.Left);
            await TestUi.SettleAsync(chart);
            var expected = zoomed.Pan((long)(60d / plotWidth * zoomed.DurationTicks), source.Extent!.Value);
            Assert.Equal(expected, chart.ViewWindow);
            window.MouseMove(new Point(dragEnd.X - 60, dragEnd.Y));
            Assert.Equal(expected, chart.ViewWindow);

            window.MouseDown(dragStart, MouseButton.Left);
            window.Content = null;
            window.Content = chart;
            await TestUi.SettleAsync(chart);
            window.MouseMove(dragEnd);
            window.MouseUp(dragEnd, MouseButton.Left);
            Assert.Equal(expected, chart.ViewWindow);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Keyboard_zoom_pan_and_home_are_bounded_and_commit_the_current_revision()
    {
        var source = Series();
        var chart = new HistoryPlot();
        chart.SetSeries(source);
        var window = TestUi.Show(chart);
        try
        {
            await TestUi.SettleAsync(chart);
            Assert.True(chart.Focus());
            TestUi.Press(window, Key.Add);
            var zoomed = chart.ViewWindow!.Value;
            Assert.True(zoomed.DurationTicks < source.Extent!.Value.DurationTicks);
            TestUi.Press(window, Key.Right);
            Assert.True(chart.ViewWindow!.Value.StartTicks > zoomed.StartTicks);
            TestUi.Press(window, Key.Left);
            Assert.InRange(chart.ViewWindow!.Value.StartTicks, source.Extent.Value.StartTicks, source.Extent.Value.EndTicks);
            chart.Pan(double.MaxValue);
            Assert.Equal(source.Extent.Value.EndTicks, chart.ViewWindow!.Value.EndTicks);
            chart.Pan(-double.MaxValue);
            Assert.Equal(source.Extent.Value.StartTicks, chart.ViewWindow!.Value.StartTicks);
            TestUi.Press(window, Key.Home);
            await TestUi.SettleAsync(chart);
            Assert.Equal(source.Extent, chart.ViewWindow);
            Assert.Equal(source.Extent, chart.Snapshot!.Window);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Replacing_source_keeps_selection_by_key_then_clears_it_when_key_is_absent()
    {
        var chart = new HistoryPlot();
        chart.SetSeries(Series(100, 1));
        var window = TestUi.Show(chart);
        try
        {
            await TestUi.SettleAsync(chart);
            chart.SelectNearest(Epoch);
            chart.SetSeries(new HistorySeries(new[] { new HistorySample(0, Epoch, 777) }, 2));
            await TestUi.SettleAsync(chart);
            Assert.Equal(0, chart.SelectedSample!.Value.Key);
            Assert.Equal(777, chart.SelectedSample.Value.Value);
            Assert.Equal(2, chart.Snapshot!.SourceVersion);
            chart.SetSeries(new HistorySeries(new[] { new HistorySample(900, Epoch + 15_000, 1) }, 3));
            await TestUi.SettleAsync(chart);
            Assert.Null(chart.SelectedSample);
            Assert.Equal(3, chart.Snapshot!.SourceVersion);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Detached_render_work_cannot_publish_and_reattach_uses_the_latest_source()
    {
        var chart = new HistoryPlot();
        chart.SetSeries(Series(100, 1));
        var window = TestUi.Show(chart);
        try
        {
            await TestUi.SettleAsync(chart);
            var commits = 0;
            chart.StateChanged += (_, _) => commits++;
            chart.SetSeries(Series(20_000, 2));
            window.Content = null;
            await chart.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, commits);
            Assert.Equal(1, chart.Snapshot!.SourceVersion);
            chart.SetSeries(Series(120, 3));
            window.Content = chart;
            await TestUi.SettleAsync(chart);
            Assert.Equal(3, chart.Snapshot!.SourceVersion);
            Assert.Equal(120, chart.Snapshot.RawSampleCount);
            Assert.True(commits > 0);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Exact_selection_uses_the_displayed_version_until_the_new_frame_commits()
    {
        var chart = new HistoryPlot();
        chart.SetSeries(new HistorySeries(new[] { new HistorySample(0, Epoch, 10) }, 1));
        var window = TestUi.Show(chart);
        try
        {
            await TestUi.SettleAsync(chart);
            chart.SetSeries(new HistorySeries(new[] { new HistorySample(0, Epoch, 20) }, 2));
            // No dispatcher yield: the previous frame is still what the user can see.
            Assert.Equal(1, chart.Snapshot!.SourceVersion);
            Assert.Equal(10, chart.SelectNearest(Epoch)!.Value.Value);
            await TestUi.SettleAsync(chart);
            Assert.Equal(2, chart.Snapshot!.SourceVersion);
            Assert.Equal(20, chart.SelectedSample!.Value.Value);
            Assert.Equal(20, chart.SelectNearest(Epoch)!.Value.Value);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Extreme_finite_values_render_without_overflow_and_remain_exact_in_selection()
    {
        var chart = new HistoryPlot();
        chart.SetSeries(new HistorySeries(new[]
        {
            new HistorySample(0, Epoch, -double.MaxValue),
            new HistorySample(1, Epoch + 1, 0),
            new HistorySample(2, Epoch + 2, double.MaxValue)
        }));
        var window = TestUi.Show(chart);
        try
        {
            await TestUi.SettleAsync(chart);
            Assert.Equal(3, chart.Snapshot!.PointCount);
            Assert.Equal(double.MaxValue, chart.SelectNearest(Epoch + 2)!.Value.Value);
            TestUi.Capture(window);
            Assert.DoesNotContain("error", chart.Status, StringComparison.OrdinalIgnoreCase);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Empty_source_clears_an_existing_snapshot()
    {
        var chart = new HistoryPlot();
        chart.SetSeries(Series());
        var window = TestUi.Show(chart);
        try
        {
            await TestUi.SettleAsync(chart);
            chart.SetSeries(new HistorySeries(Array.Empty<HistorySample>(), 2));
            await TestUi.SettleAsync(chart);
            Assert.Null(chart.Snapshot);
            Assert.Null(chart.ViewWindow);
            Assert.Contains("No samples", chart.Status);
            TestUi.Capture(window);
        }
        finally { window.Close(); }
    }
}
