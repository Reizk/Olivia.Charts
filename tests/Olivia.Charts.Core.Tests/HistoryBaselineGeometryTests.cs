using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class HistoryBaselineGeometryTests
{
    private static readonly long Epoch = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    private const double PositionTolerance = 2e-15;
    private const double ValueTolerance = 4e-15;

    [Fact]
    public void Ten_utc_samples_have_the_original_padded_domain_five_ticks_and_nine_real_edges()
    {
        var source = Series(Enumerable.Range(0, 10).Select(i => (double?)i).ToArray());
        var snapshot = source.Query(source.Extent!.Value, 32);
        var geometry = HistoryPlotGeometry.Create(snapshot);
        Assert.Equal(new TimeWindow(Epoch, Epoch + 9 * TimeSpan.TicksPerSecond + 1), geometry.Window);
        Assert.Equal(166, geometry.SourceVersion);
        Assert.Equal(10, snapshot.RawSampleCount);
        Assert.Equal(10, geometry.Nodes.Length);
        Assert.Equal(new HistoryPlotValueDomain(0, 9), geometry.RawDomain);
        AssertDomain(geometry, -.72, 9.72);
        AssertTicks(geometry, [9.72, 7.11, 4.5, 1.89, -.72]);
        Assert.Equal(9, geometry.DataEdges.Length);
        Assert.Equal(geometry.Nodes[0], Assert.Single(geometry.Markers));
        Assert.Equal(new HistoryPlotSegmentGeometry(0, 10, 0, 10, 0, 9), Assert.Single(geometry.Segments));
        for (var i = 0; i < 10; i++)
        {
            var node = geometry.Nodes[i];
            Assert.Equal(new HistorySample(i, Epoch + i * TimeSpan.TicksPerSecond, i), node.Sample);
            Assert.Equal(i, node.RawIndex);
            // Decimal reference calculations do not call production normalization or domain code.
            Near((double)(i * 10000000m / 90000001m), node.Position.X, PositionTolerance);
            Near((double)((i + .72m) / 10.44m), node.Position.Y, PositionTolerance);
            Assert.Equal(node.Position, geometry.Normalize(node.UtcTicks, node.Value));
            if (i > 0) Assert.Equal(new HistoryPlotEdge(geometry.Nodes[i - 1], node), geometry.DataEdges[i - 1]);
        }
    }

    [Theory]
    [InlineData(5, 4.42, 5.58)]
    [InlineData(0, -.116, .116)]
    [InlineData(-5, -5.58, -4.42)]
    public void A_single_sample_has_one_marker_no_data_edge_and_the_original_constant_expansion(
        double value, double minimum, double maximum)
    {
        var source = Series(value);
        var geometry = HistoryPlotGeometry.Create(source.Query(source.Extent!.Value, 32));
        Assert.Equal(new TimeWindow(Epoch, Epoch + 1), geometry.Window);
        Assert.Equal(new HistoryPlotValueDomain(value, value), geometry.RawDomain);
        AssertDomain(geometry, minimum, maximum);
        var node = Assert.Single(geometry.Nodes);
        Assert.Equal(node, Assert.Single(geometry.Markers));
        Assert.Empty(geometry.DataEdges);
        Assert.Equal(0, node.Position.X);
        Near(.5, node.Position.Y, PositionTolerance);
        Assert.Equal(new HistoryPlotSegmentGeometry(0, 1, 0, 1, 0, 0), Assert.Single(geometry.Segments));
        Assert.Equal(5, geometry.Ticks.Length);
        Near(value, geometry.Ticks[2].Value, ValueTolerance);
    }

    [Fact]
    public void An_empty_source_has_no_fabricated_domain_ticks_nodes_edges_or_markers()
    {
        var source = Series();
        Assert.Null(source.Extent);
        var geometry = HistoryPlotGeometry.Create(source.Query(new TimeWindow(Epoch, Epoch), 32));
        Assert.Null(geometry.RawDomain);
        Assert.Null(geometry.DisplayDomain);
        Assert.Empty(geometry.Ticks);
        Assert.Empty(geometry.Nodes);
        Assert.Empty(geometry.DataEdges);
        Assert.Empty(geometry.Markers);
        Assert.Empty(geometry.Segments);
        Assert.Empty(geometry.Gaps);
        Assert.Throws<InvalidOperationException>(() => geometry.Normalize(Epoch, 0));
        Assert.Throws<ArgumentNullException>(() => HistoryPlotGeometry.Create(null!));
    }

    [Fact]
    public void Gaps_split_actual_edges_and_keep_one_marker_for_each_finite_run()
    {
        var source = Series(0, 1, null, double.NaN, 4, 5, double.PositiveInfinity, 7);
        var snapshot = source.Query(source.Extent!.Value, 32);
        var geometry = HistoryPlotGeometry.Create(snapshot);
        Assert.Equal(new long[] { 0, 1, 4, 5, 7 }, geometry.Nodes.Select(node => node.Key));
        Assert.Equal(new long[] { 0, 4, 7 }, geometry.Markers.Select(node => node.Key));
        Assert.Equal(new (long, long)[] { (0, 1), (4, 5) }, geometry.DataEdges.Select(edge => (edge.Start.Key, edge.End.Key)));
        Assert.Equal(snapshot.Gaps.ToArray(), geometry.Gaps.ToArray());
        Assert.Equal(new HistoryPlotSegmentGeometry[] {
            new(0, 2, 0, 2, 0, 1), new(4, 6, 2, 2, 1, 1), new(7, 8, 4, 1, 2, 0)
        }, geometry.Segments.ToArray());
    }

    [Fact]
    public void Insufficient_budget_and_all_gaps_retain_gap_metadata_without_data_geometry()
    {
        foreach (var source in new[] { Series(0, 1, null, 3), Series(null, double.NaN, double.NegativeInfinity) })
        {
            var snapshot = source.Query(source.Extent!.Value, 0);
            var geometry = HistoryPlotGeometry.Create(snapshot);
            Assert.Null(geometry.RawDomain);
            Assert.Null(geometry.DisplayDomain);
            Assert.Empty(geometry.Ticks);
            Assert.Empty(geometry.Nodes);
            Assert.Empty(geometry.DataEdges);
            Assert.Empty(geometry.Markers);
            Assert.Equal(snapshot.Gaps.ToArray(), geometry.Gaps.ToArray());
        }
    }

    [Fact]
    public void Opposite_maximum_values_keep_finite_normalized_nodes_and_original_ticks()
    {
        var source = Series(-double.MaxValue, 0, double.MaxValue);
        var geometry = HistoryPlotGeometry.Create(source.Query(source.Extent!.Value, 32));
        Assert.Equal(new HistoryPlotValueDomain(-double.MaxValue, double.MaxValue), geometry.DisplayDomain);
        Assert.Equal(new double[] { 0, .5, 1 }, geometry.Nodes.Select(node => node.Position.Y));
        Assert.Equal(new double[] { double.MaxValue, double.MaxValue / 2, 0, -double.MaxValue / 2, -double.MaxValue },
            geometry.Ticks.Select(tick => tick.Value));
        Assert.All(geometry.Nodes, node => Assert.True(double.IsFinite(node.Position.X) && double.IsFinite(node.Position.Y)));
    }

    [Fact]
    public void Extraction_preserves_the_existing_extreme_endpoint_label_overflow_without_throwing()
    {
        // This is an existing label limitation, not a claim of arbitrary extreme label support.
        const double magnitude = 1.7597120767402753e308;
        var source = Series(-magnitude, magnitude);
        var geometry = HistoryPlotGeometry.Create(source.Query(source.Extent!.Value, 32));
        var domain = geometry.DisplayDomain!.Value;
        Assert.True(double.IsNegativeInfinity(domain.Minimum));
        Assert.True(double.IsPositiveInfinity(domain.Maximum));
        Assert.True(double.IsPositiveInfinity(geometry.Ticks[0].Value));
        Assert.All(geometry.Nodes, node => Assert.True(double.IsFinite(node.Position.Y)));
        Assert.Single(geometry.DataEdges);
    }

    [Fact]
    public void Another_query_and_array_copies_cannot_mutate_a_committed_geometry()
    {
        var source = Series(0, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        var full = HistoryPlotGeometry.Create(source.Query(source.Extent!.Value, 32));
        var originalNode = full.Nodes[0];
        var copy = full.Nodes.ToArray();
        copy[0] = default;
        var zoomed = HistoryPlotGeometry.Create(source.Query(new TimeWindow(Epoch + 4 * TimeSpan.TicksPerSecond,
            Epoch + 6 * TimeSpan.TicksPerSecond), 32));
        Assert.Equal(2, zoomed.Nodes.Length);
        Assert.Equal(10, full.Nodes.Length);
        Assert.Equal(originalNode, full.Nodes[0]);
        Assert.Equal(new HistoryPlotValueDomain(0, 9), full.RawDomain);
        Assert.Equal(new HistoryPlotValueDomain(4, 5), zoomed.RawDomain);
    }

    private static HistorySeries Series(params double?[] values) => new(values.Select((value, i) =>
        new HistorySample(i, Epoch + i * TimeSpan.TicksPerSecond, value)), 166);

    private static void AssertDomain(HistoryPlotGeometry geometry, double minimum, double maximum)
    {
        Assert.NotNull(geometry.DisplayDomain);
        var domain = geometry.DisplayDomain!.Value;
        Near(minimum, domain.Minimum, ValueTolerance);
        Near(maximum, domain.Maximum, ValueTolerance);
    }

    private static void AssertTicks(HistoryPlotGeometry geometry, double[] values)
    {
        Assert.Equal(5, geometry.Ticks.Length);
        for (var i = 0; i < values.Length; i++)
        {
            Near(values[i], geometry.Ticks[i].Value, ValueTolerance);
            Assert.Equal(1 - i / 4d, geometry.Ticks[i].Position);
        }
    }

    private static void Near(double expected, double actual, double tolerance) =>
        Assert.InRange(Math.Abs(actual - expected), 0, tolerance);
}
