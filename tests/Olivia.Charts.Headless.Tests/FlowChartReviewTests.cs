using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Olivia.Avalonia;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Headless.Tests;

/// <summary>Independent review regressions; native platform execution is not implied.</summary>
public sealed class FlowChartReviewTests
{
    private static FlowChartSource Source() => new(
        [new("a", "Same"), new("b", "Same"), new("c", "Same")],
        [new("ab", "a", "b", 4), new("bc", "b", "c", 4)], 91);

    [AvaloniaFact]
    public void Same_frame_nested_root_key_keeps_newer_selection_without_source_replacement()
    {
        var chart = new FlowPlot(); var window = TestUi.Show(chart, 1000, 620);
        try
        {
            var source = Source(); chart.SetSource(source); chart.Focus(); var revision = chart.FrameRevision;
            var once = false;
            window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (once || !ReferenceEquals(e.Source, chart) || e.Key != Key.Home) return;
                once = true; TestUi.Press(window, Key.End);
            }, RoutingStrategies.Tunnel);
            TestUi.Press(window, Key.Home);
            Assert.True(once); Assert.Same(source, chart.Snapshot!.Source);
            Assert.Equal(revision, chart.FrameRevision);
            Assert.Equal("bc", chart.SelectedHit!.Value.Key);
            Assert.False(chart.SelectedHit.Value.IsNode);
            TestUi.Press(window, Key.Home);
            Assert.Equal("a", chart.SelectedHit!.Value.Key);
        }
        finally { chart.ReleaseSource(); window.Close(); }
    }

    [AvaloniaFact]
    public void Same_frame_nested_root_press_keeps_newer_selection_without_source_replacement()
    {
        var chart = new FlowPlot(); var window = TestUi.Show(chart, 1000, 620);
        try
        {
            var source = Source(); chart.SetSource(source); chart.Focus(); var revision = chart.FrameRevision;
            var first = chart.NodeRect(chart.Snapshot!.Nodes[0]).Center;
            var last = chart.NodeRect(chart.Snapshot.Nodes[2]).Center;
            var once = false;
            window.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            {
                if (once || !ReferenceEquals(e.Source, chart)) return;
                once = true; TestUi.Click(window, last);
            }, RoutingStrategies.Tunnel);
            TestUi.Click(window, first);
            Assert.True(once); Assert.Same(source, chart.Snapshot!.Source);
            Assert.Equal(revision, chart.FrameRevision);
            Assert.Equal("c", chart.SelectedHit!.Value.Key);
            Assert.True(chart.SelectedHit.Value.IsNode);
            TestUi.Click(window, first);
            Assert.Equal("a", chart.SelectedHit!.Value.Key);
        }
        finally { chart.ReleaseSource(); window.Close(); }
    }

    [AvaloniaFact]
    public void Same_source_focus_callback_nested_key_retires_older_pointer_selection()
    {
        var chart = new FlowPlot { Height = 500 }; var other = new Button { Content = "Other" };
        var panel = new StackPanel(); panel.Children.Add(chart); panel.Children.Add(other);
        var window = TestUi.Show(panel, 1000, 560);
        try
        {
            var source = Source(); chart.SetSource(source); other.Focus(); var once = false;
            chart.GotFocus += (_, _) =>
            {
                if (once) return;
                once = true; TestUi.Press(window, Key.End);
            };
            TestUi.Click(window, chart.NodeRect(chart.Snapshot!.Nodes[0]).Center);
            Assert.True(once); Assert.Same(source, chart.Snapshot.Source);
            Assert.Equal("bc", chart.SelectedHit!.Value.Key);
            TestUi.Click(window, chart.NodeRect(chart.Snapshot.Nodes[0]).Center);
            Assert.Equal("a", chart.SelectedHit!.Value.Key);
        }
        finally { chart.ReleaseSource(); window.Close(); }
    }

    [AvaloniaFact]
    public void Programmatic_selection_during_root_or_focus_callback_retires_older_routed_input()
    {
        foreach (var mode in new[] { "root-key", "pointer-focus" })
        {
            var chart = new FlowPlot { Height = 500 }; var other = new Button { Content = "Other" };
            var panel = new StackPanel(); panel.Children.Add(chart); panel.Children.Add(other);
            var window = TestUi.Show(panel, 1000, 560);
            try
            {
                var source = Source(); chart.SetSource(source); var once = false;
                if (mode == "root-key")
                {
                    chart.Focus();
                    window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
                    {
                        if (once || !ReferenceEquals(e.Source, chart) || e.Key != Key.Home) return;
                        once = true; Assert.True(chart.SelectItem(4, chart.FrameRevision));
                    }, RoutingStrategies.Tunnel);
                    TestUi.Press(window, Key.Home);
                }
                else
                {
                    other.Focus();
                    chart.GotFocus += (_, _) =>
                    {
                        if (once) return;
                        once = true; Assert.True(chart.SelectItem(4, chart.FrameRevision));
                    };
                    TestUi.Click(window, chart.ProjectNode(chart.Snapshot!.Nodes[0]).Center);
                }
                Assert.True(once); Assert.Same(source, chart.Snapshot!.Source);
                Assert.Equal("bc", chart.SelectedHit!.Value.Key);
                TestUi.Press(window, Key.Home); Assert.Equal("a", chart.SelectedHit!.Value.Key);
            }
            finally { chart.ReleaseSource(); window.Close(); }
        }
    }

    [AvaloniaFact]
    public void Node_projection_uses_shared_endpoint_arithmetic_and_legend_cast_is_bounded()
    {
        var chart = new FlowPlot(); var window = TestUi.Show(chart, 1001, 620);
        try
        {
            chart.SetSource(Source()); var a = chart.PlotArea;
            foreach (var node in chart.Snapshot!.Nodes)
            {
                var r = chart.ProjectNode(node);
                Assert.Equal(a.Left + node.Left * a.Width, r.Left);
                Assert.Equal(a.Left + node.Right * a.Width, r.Right);
                Assert.Equal(a.Top + node.Top * a.Height, r.Top);
                Assert.Equal(a.Top + node.Bottom * a.Height, r.Bottom);
                Assert.True(chart.SelectAt(new(r.Center.X, Math.BitDecrement(r.Bottom)), chart.FrameRevision));
                Assert.True(chart.SelectedHit!.Value.IsNode); Assert.Equal(node.Hit.Key, chart.SelectedHit.Value.Key);
                Assert.False(chart.SelectAt(new(r.Center.X, r.Bottom), chart.FrameRevision));
                Assert.Null(chart.SelectedHit);
                Assert.True(chart.SelectAt(new(Math.BitDecrement(r.Right), r.Center.Y), chart.FrameRevision));
                Assert.True(chart.SelectedHit!.Value.IsNode); Assert.Equal(node.Hit.Key, chart.SelectedHit.Value.Key);
                chart.SelectAt(new(r.Right, r.Center.Y), chart.FrameRevision);
                Assert.False(chart.SelectedHit is { IsNode: true } hit && hit.Key == node.Hit.Key);
            }
            foreach (var ribbon in chart.Snapshot.Ribbons)
            {
                Assert.Equal(a.Left + ribbon.StartX * a.Width, chart.ProjectNode(chart.Snapshot.Nodes[ribbon.SourceNodeIndex]).Right);
                Assert.Equal(a.Left + ribbon.EndX * a.Width, chart.ProjectNode(chart.Snapshot.Nodes[ribbon.TargetNodeIndex]).Left);
            }
        }
        finally { chart.ReleaseSource(); window.Close(); }
        // Logical layout only: no attempt to allocate or certify a huge native surface.
        var detached = new FlowPlot();
        detached.Measure(new Size(1000, 1e12)); detached.Arrange(new Rect(0, 0, 1000, 1e12));
        Assert.Equal(320, detached.LegendCapacity);
    }
}
