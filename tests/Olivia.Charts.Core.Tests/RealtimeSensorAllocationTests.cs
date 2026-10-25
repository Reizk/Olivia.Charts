using System.Collections.Immutable;
using Olivia.Core.Charts;
using Olivia.Core.Workspaces;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class RealtimeSensorAllocationTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Diagnostic_edges_preserve_original_segment_adjacency_and_frozen_frame_semantics()
    {
        var queue = new Queue<Action>();
        RealtimeSensorFrame? frame = null;
        await using var session = new RealtimeSensorSession(queue.Enqueue, value => frame = value,
            new(capacity: 16, initialSampleCount: 0, pointBudget: 10));
        session.Initialize();
        double?[] values = [-5, 0, 18, 1, null, 6, -50, 7, double.PositiveInfinity, 0, 3, 4, -20,
            double.NaN, 33, null];
        for (var index = 0; index < values.Length; index++)
            session.Append(Row(index + 1, index, values[index]));
        session.SetWindow(new(Epoch.UtcTicks, Epoch.AddSeconds(values.Length).UtcTicks));
        Assert.True(session.PublishTick());
        Drain(queue);

        var frozen = Assert.IsType<RealtimeSensorFrame>(frame);
        Assert.Equal(4, frozen.Series[0].Segments.Length);
        Assert.Single(frozen.Series[0].Segments[^1].Points);
        Assert.Equal(new long[] { 1, 3, 4, 6, 7, 8, 10, 12, 13, 15 },
            frozen.Series[0].Segments.SelectMany(segment => segment.Points).Select(point => point.Key));
        var cached = new List<(RealtimeSensorSeriesFrame Series, ImmutableArray<HistoryPlotEdge> Edges)>();
        foreach (var pane in frozen.Panes)
        foreach (var series in pane.Series)
        {
            // The oracle uses original samples, snapshot adjacency and the public normalizer,
            // not the prepared Nodes or the implementation's flattened-node offsets.
            var expected = series.Snapshot.Segments.SelectMany(segment =>
                segment.Points.Zip(segment.Points.Skip(1), (start, end) => new HistoryPlotEdge(
                    OriginalNode(frozen, pane, series.Index, start),
                    OriginalNode(frozen, pane, series.Index, end)))).ToArray();
            var edges = series.DataEdges;
            Assert.Equal(6, edges.Length);
            Assert.Equal(expected, edges.ToArray());
            Assert.DoesNotContain(edges, edge => edge.Start.Key == 15 || edge.End.Key == 15);
            // ImmutableArray.Equals compares backing-array identity, not just contents.
            Assert.True(edges.Equals(series.DataEdges));
            cached.Add((series, edges));
        }
        Assert.Equal(new HistorySample(2, Epoch.AddSeconds(1).UtcTicks, 0),
            frozen.FindNearest(0, Epoch.AddSeconds(1).UtcTicks));

        session.SetPointBudget(2);
        Assert.True(session.PublishTick());
        Drain(queue);
        Assert.All(frame!.Panes.SelectMany(pane => pane.Series), series =>
        {
            Assert.Equal(ChartBudgetStatus.InsufficientBudget, series.Snapshot.BudgetStatus);
            Assert.Empty(series.Snapshot.Segments);
            Assert.False(series.DataEdges.IsDefault);
            Assert.Empty(series.DataEdges);
            Assert.True(series.DataEdges.Equals(series.DataEdges));
        });

        session.SetPointBudget(10);
        // The manual window stays fixed while retention evicts every original row.
        for (var index = 16; index < 32; index++) session.Append(Row(index + 1, index, index));
        Assert.True(session.PublishTick());
        Drain(queue);
        Assert.Empty(frame!.RawRows);
        Assert.All(frame.Panes.SelectMany(pane => pane.Series), series =>
        {
            Assert.Equal(ChartBudgetStatus.Satisfied, series.Snapshot.BudgetStatus);
            Assert.Empty(series.Nodes);
            Assert.False(series.DataEdges.IsDefault);
            Assert.Empty(series.DataEdges);
        });
        Assert.All(cached, item =>
        {
            Assert.True(item.Edges.Equals(item.Series.DataEdges));
            Assert.Equal(6, item.Series.DataEdges.Length);
        });
        Assert.Equal(16, frozen.RawRows.Length);
    }

    [Fact]
    public async Task Concurrent_first_readers_receive_the_same_cached_immutable_edge_array()
    {
        var queue = new Queue<Action>();
        RealtimeSensorFrame? frame = null;
        await using var session = new RealtimeSensorSession(queue.Enqueue, value => frame = value,
            new(initialSampleCount: 0, pointBudget: 64));
        session.Initialize();
        for (var index = 0; index < 32; index++) session.Append(Row(index + 1, index, index));
        session.SetWindow(new(Epoch.UtcTicks, Epoch.AddSeconds(32).UtcTicks));
        Assert.True(session.PublishTick());
        Drain(queue);
        var series = Assert.IsType<RealtimeSensorFrame>(frame).Panes[0].Series[0];

        const int readerCount = 8;
        using var ready = new CountdownEvent(readerCount);
        using var start = new ManualResetEventSlim();
        // No DataEdges access occurs before all dedicated readers reach the start gate.
        var readers = Enumerable.Range(0, readerCount).Select(_ => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            start.Wait();
            return series.DataEdges;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var allReady = ready.Wait(TimeSpan.FromSeconds(10));
        start.Set();
        var results = await Task.WhenAll(readers);

        Assert.True(allReady, "All readers must reach the gate before first access.");
        Assert.Equal(31, results[0].Length);
        Assert.All(results, edges => Assert.True(results[0].Equals(edges)));
        Assert.True(results[0].Equals(series.DataEdges));
        var changedCopy = results[0].RemoveAt(0);
        Assert.Equal(30, changedCopy.Length);
        Assert.Equal(31, series.DataEdges.Length);
        Assert.True(results[0].Equals(series.DataEdges));
    }

    private static HistoryPlotNode OriginalNode(RealtimeSensorFrame frame, RealtimeSensorPaneFrame pane,
        int channel, HistoryRepresentativePoint point)
    {
        var original = frame.RawRows[point.RawIndex].Sample(channel);
        Assert.Equal(original, point.Sample);
        return new(new(original, point.RawIndex), pane.Geometry.Normalize(original.UtcTicks, original.Value!.Value));
    }

    private static RealtimeSensorRow Row(long key, int second, double? value) => new(key,
        Epoch.AddSeconds(second).UtcTicks,
        Enumerable.Range(0, 8).Select(channel => value + channel * 100).ToImmutableArray());

    private static void Drain(Queue<Action> queue)
    {
        while (queue.TryDequeue(out var action)) action();
    }
}
