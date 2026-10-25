using System.Numerics;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class TreemapReviewTests
{
    [Fact]
    public void Exact_sums_distinguish_true_overflow_from_rounded_finite_values_and_ties_to_even()
    {
        var maxPlusTiny = Flat(double.MaxValue, double.Epsilon).Total;
        Assert.Equal(Units(double.MaxValue) + 1, maxPlusTiny.Units);
        Assert.True(maxPlusTiny.IsOverflow); Assert.False(maxPlusTiny.IsExact);
        Assert.Equal(double.MaxValue, maxPlusTiny.RoundedValue);
        var infinite = Flat(double.MaxValue, double.MaxValue).Total;
        Assert.Equal(Units(double.MaxValue) * 2, infinite.Units);
        Assert.True(infinite.IsOverflow); Assert.Equal(double.PositiveInfinity, infinite.RoundedValue);
        var evenDown = Flat(1, Math.ScaleB(1, -53)).Total;
        Assert.Equal(1, evenDown.RoundedValue); Assert.False(evenDown.IsExact);
        var evenUp = Flat(1, 3 * Math.ScaleB(1, -53)).Total;
        Assert.Equal(Math.BitIncrement(Math.BitIncrement(1)), evenUp.RoundedValue); Assert.False(evenUp.IsExact);
        var tiny = Flat(double.Epsilon, 2 * double.Epsilon, 3 * double.Epsilon).Total;
        Assert.Equal(new BigInteger(6), tiny.Units); Assert.Equal(6 * double.Epsilon, tiny.RoundedValue); Assert.True(tiny.IsExact);
    }

    [Fact]
    public void Cumulative_lattice_collapse_depends_on_order_but_never_invents_a_raw_minimum()
    {
        var smallFirst = Flat(double.Epsilon, 1).Query();
        Assert.Equal(TreemapRectangleStatus.Collapsed, smallFirst.Rectangles[0].Status);
        Assert.Equal(0, smallFirst.Rectangles[0].Width);
        Assert.Equal("n1", TreemapChartGeometry.HitTest(smallFirst, 0, 0)!.Value.Key);
        var smallLast = Flat(1, double.Epsilon).Query();
        Assert.Equal(TreemapChartGeometry.CoordinateQuantum, smallLast.Rectangles[1].Width);
        Assert.Equal(TreemapRectangleStatus.Visible, smallLast.Rectangles[1].Status);
        Assert.Equal(double.Epsilon, smallLast.Rectangles[1].Hit.Weight);
        Assert.Equal("n1", TreemapChartGeometry.HitTest(smallLast, smallLast.Rectangles[1].Left, 0)!.Value.Key);
        Assert.Equal("n0", TreemapChartGeometry.HitTest(smallLast, Math.BitDecrement(smallLast.Rectangles[1].Left), 0)!.Value.Key);
        foreach (var snapshot in new[] { smallFirst, smallLast })
        {
            Assert.Equal(Units(1) + 1, snapshot.Total.Units);
            Assert.Equal(double.Epsilon, TreemapChartGeometry.GetHit(snapshot, snapshot == smallFirst ? 0 : 1)!.Value.Weight);
            Assert.Equal(1, snapshot.Rectangles.Sum(r => r.Width));
        }
    }

    [Fact]
    public void Hidden_zero_cycle_and_depth_overflow_reject_without_hiding_valid_roots()
    {
        Assert.Throws<ArgumentException>(() => new TreemapChartSource([
            new("root", "Visible", null, 1), new("a", "Hidden", "b", 0), new("b", "Hidden", "a", -0d)]));
        Assert.Throws<ArgumentException>(() => new TreemapChartSource([new("a", "", "missing", 0)]));
        Assert.Throws<ArgumentException>(() => new TreemapChartSource([new("a", "", "a", 0)]));
        Assert.Throws<ArgumentException>(() => new TreemapChartSource([new("a", "", null, 1), new("b", "", "a", 0)]));
        var valid = Chain(16); Assert.Equal(16, valid.DepthCount);
        Assert.Equal(16, valid.Query(new(displayDepth: 16)).Rectangles.Length);
        Assert.Throws<ArgumentException>(() => Chain(17));
    }

    [Fact]
    public void Shuffled_children_preserve_original_identity_leaf_ranges_and_sign_of_raw_zero()
    {
        TreemapNode[] input = [new("leaf", "Repeated", "branch", 2), new("Root", "Repeated", null, -0d),
            new("zero", "Repeated", "branch", -0d), new("branch", "Repeated", "Root", 0), new("root", "Repeated", null, 3)];
        var source = new TreemapChartSource(input, 83); var s = source.Query(new(displayDepth: 16));
        input[0] = new("changed", "", null, 99);
        Assert.Equal(new[] { "Root", "branch", "leaf", "zero", "root" }, s.Rectangles.Select(r => r.Hit.Key));
        Assert.Equal(new[] { "leaf", "zero", "root" }, source.LeafIndices.Select(i => source.Nodes[i].Key));
        foreach (var r in s.Rectangles)
        {
            Assert.Same(source.Nodes[r.NodeIndex], r.Hit.Node); Assert.Equal(83, r.Hit.DatasetVersion);
            var expected = LeavesBelow(source, r.Hit.Key).Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), source.LeafIndices.Skip(r.Hit.LeafStart).Take(r.Hit.LeafCount)
                .Select(i => source.Nodes[i].Key).OrderBy(x => x, StringComparer.Ordinal));
        }
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(s.Rectangles[0].Hit.Weight));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(s.Rectangles[3].Hit.Weight));
        Assert.Equal(new[] { "branch", "leaf", "zero" }, source.Query(new(displayDepth: 16), "Root").Rectangles.Select(r => r.Hit.Key));
        Assert.Throws<ArgumentException>(() => source.Query(focusKey: "leaf"));
        Assert.Throws<ArgumentException>(() => source.Query(focusKey: "ROOT"));
    }

    [Fact]
    public void Random_full_exponent_forests_preserve_exact_sums_partition_coverage_and_area_error_bounds()
    {
        var random = new Random(962611);
        for (var sample = 0; sample < 36; sample++)
        {
            const int count = 128;
            var parents = new int[count]; var depths = new int[count]; parents[0] = -1;
            for (var i = 1; i < count; i++)
            {
                parents[i] = random.Next(8) == 0 ? -1 : random.Next(i);
                if (parents[i] >= 0 && depths[parents[i]] >= 15) parents[i] = -1;
                depths[i] = parents[i] < 0 ? 0 : depths[parents[i]] + 1;
            }
            var branch = parents.Where(i => i >= 0).ToHashSet();
            var nodes = Enumerable.Range(0, count).Select(i => new TreemapNode($"n{i}", "Same", parents[i] < 0 ? null : $"n{parents[i]}",
                branch.Contains(i) ? 0 : Weight(random))).OrderBy(_ => random.Next()).ToArray();
            var source = new TreemapChartSource(nodes, sample); var snapshot = source.Query(new(displayDepth: 16));
            Assert.Equal(count, snapshot.Rectangles.Length); Assert.Equal(count * 6, snapshot.RequiredWorkCount);
            Assert.Equal(nodes.Where(n => !nodes.Any(c => c.ParentKey == n.Key)).Aggregate(BigInteger.Zero, (v, n) => v + Units(n.Weight)), source.Total.Units);
            VerifyRectangles(source, snapshot);
            foreach (var key in nodes.Where(n => nodes.Any(c => c.ParentKey == n.Key)).Take(3).Select(n => n.Key))
                VerifyRectangles(source, source.Query(new(displayDepth: 16), key));
        }
    }

    [Fact]
    public void Focus_budget_charges_entire_source_and_exact_displayed_preorder_with_no_partial_hits()
    {
        var source = new TreemapChartSource([new("r", "", null, 0), new("a", "", "r", 1), new("b", "", "r", 0), new("outside", "", null, 2)]);
        var admitted = source.Query(new(displayDepth: 16, geometryBudget: 2, workBudget: 20), "r");
        Assert.Equal(2, admitted.RequiredGeometryCount); Assert.Equal(20, admitted.RequiredWorkCount);
        Assert.Equal(ChartBudgetStatus.Satisfied, admitted.BudgetStatus);
        foreach (var options in new[] { new TreemapChartOptions(16, 1, 20), new TreemapChartOptions(16, 2, 19) })
        {
            var refused = source.Query(options, "r");
            Assert.Equal(ChartBudgetStatus.InsufficientBudget, refused.BudgetStatus);
            Assert.Equal(admitted.Total, refused.Total); Assert.Equal("r", refused.FocusKey);
            Assert.Equal(2, refused.RequiredGeometryCount); Assert.Equal(20, refused.RequiredWorkCount); Assert.Empty(refused.Rectangles);
            Assert.Null(TreemapChartGeometry.GetHit(refused, 0)); Assert.Null(TreemapChartGeometry.HitTest(refused, .5, .5));
        }
        var empty = new TreemapChartSource([]).Query(new(1, 0, 0));
        Assert.True(empty.IsEmpty); Assert.False(empty.HasPositiveWeight); Assert.Equal(0, empty.RequiredWorkCount);
        Assert.Equal(ChartBudgetStatus.Satisfied, empty.BudgetStatus);
    }

    private static void VerifyRectangles(TreemapChartSource source, TreemapChartSnapshot snapshot)
    {
        var q = new BigInteger(TreemapChartGeometry.CoordinateUnits);
        foreach (var rectangle in snapshot.Rectangles)
        {
            var leaves = LeavesBelow(source, rectangle.Hit.Key).ToArray();
            var aggregate = leaves.Aggregate(BigInteger.Zero, (v, n) => v + Units(n.Weight));
            Assert.Equal(aggregate, rectangle.Hit.Aggregate.Units); Assert.Equal(leaves.Length, rectangle.Hit.LeafCount);
            var actualLeaves = source.LeafIndices.Skip(rectangle.Hit.LeafStart).Take(rectangle.Hit.LeafCount).Select(i => source.Nodes[i].Key).ToHashSet();
            Assert.True(actualLeaves.SetEquals(leaves.Select(n => n.Key)));
            foreach (var coordinate in new[] { rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom })
            { Assert.InRange(coordinate, 0, 1); Assert.Equal(Math.Truncate(coordinate / TreemapChartGeometry.CoordinateQuantum), coordinate / TreemapChartGeometry.CoordinateQuantum); }
            Assert.True(rectangle.Width >= 0 && rectangle.Height >= 0);
            var width = Ticks(rectangle.Right) - Ticks(rectangle.Left); var height = Ticks(rectangle.Bottom) - Ticks(rectangle.Top);
            if (aggregate.IsZero) Assert.Equal(TreemapRectangleStatus.Zero, rectangle.Status);
            else Assert.Equal(width.IsZero || height.IsZero ? TreemapRectangleStatus.Collapsed : TreemapRectangleStatus.Visible, rectangle.Status);
            if (!snapshot.Total.Units.IsZero)
            {
                // Independent global area oracle: each quantized split contributes < 1/Q absolute area error.
                var areaError = BigInteger.Abs(width * height * snapshot.Total.Units - q * q * aggregate);
                Assert.True(areaError <= (rectangle.Depth + 1) * q * snapshot.Total.Units);
            }
            if (rectangle.Status == TreemapRectangleStatus.Visible)
            {
                var points = new[] { (rectangle.Left, rectangle.Top), (Math.BitDecrement(rectangle.Right), Math.BitDecrement(rectangle.Bottom)) };
                foreach (var (x, y) in points)
                {
                    var expected = snapshot.Rectangles.Where(r => r.Status == TreemapRectangleStatus.Visible && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom).MaxBy(r => r.Depth);
                    Assert.Equal(expected.Hit, TreemapChartGeometry.HitTest(snapshot, x, y));
                }
            }
        }
        foreach (var group in snapshot.Rectangles.GroupBy(r => r.Hit.ParentKey))
        {
            var rectangles = group.ToArray(); var depth = rectangles[0].Depth;
            var parent = group.Key == snapshot.FocusKey ? (TreemapChartRectangle?)null : snapshot.Rectangles.Single(r => r.Hit.Key == group.Key);
            var left = parent?.Left ?? 0; var top = parent?.Top ?? 0; var right = parent?.Right ?? 1; var bottom = parent?.Bottom ?? 1;
            var denominator = parent?.Hit.Aggregate.Units ?? snapshot.Total.Units;
            var origin = depth % 2 == 0 ? left : top; var end = depth % 2 == 0 ? right : bottom;
            var previous = origin;
            foreach (var r in rectangles)
            {
                Assert.Equal(previous, depth % 2 == 0 ? r.Left : r.Top);
                Assert.Equal(depth % 2 == 0 ? top : left, depth % 2 == 0 ? r.Top : r.Left);
                Assert.Equal(depth % 2 == 0 ? bottom : right, depth % 2 == 0 ? r.Bottom : r.Right);
                var length = depth % 2 == 0 ? r.Width : r.Height;
                if (!denominator.IsZero)
                    Assert.True(BigInteger.Abs(Ticks(length) * denominator - Ticks(end - origin) * r.Hit.Aggregate.Units) < denominator);
                previous = depth % 2 == 0 ? r.Right : r.Bottom;
            }
            Assert.Equal(denominator.IsZero ? origin : end, previous);
        }
        Assert.Null(TreemapChartGeometry.HitTest(snapshot, 1, 0)); Assert.Null(TreemapChartGeometry.HitTest(snapshot, 0, 1));
    }
    private static IEnumerable<TreemapNode> LeavesBelow(TreemapChartSource source, string key)
    {
        foreach (var leaf in source.Nodes.Where(n => !source.Nodes.Any(c => c.ParentKey == n.Key)))
        {
            for (string? at = leaf.Key; at is not null; at = source.Nodes.Single(n => n.Key == at).ParentKey)
                if (at == key) { yield return leaf; break; }
        }
    }
    private static double Weight(Random random) => random.Next(8) switch
    {
        0 => 0, 1 => double.Epsilon, 2 => double.MaxValue,
        _ => Math.ScaleB(1 + random.NextDouble(), random.Next(-1074, 1024))
    };
    private static TreemapChartSource Flat(params double[] values) => new(values.Select((value, i) => new TreemapNode($"n{i}", "", null, value)));
    private static TreemapChartSource Chain(int count) => new(Enumerable.Range(0, count).Select(i => new TreemapNode($"n{i}", "", i == 0 ? null : $"n{i - 1}", i == count - 1 ? 1 : 0)));
    private static BigInteger Ticks(double value) => new(value / TreemapChartGeometry.CoordinateQuantum);
    private static BigInteger Units(double value)
    {
        if (value == 0) return BigInteger.Zero;
        // Independent decomposition: normalize the raw value with ILogB/ScaleB before extracting the integer significand.
        var exponent = Math.ILogB(value); var significand = new BigInteger(Math.ScaleB(value, 52 - exponent));
        return exponent < -1022 ? significand >> (-1022 - exponent) : significand << (exponent + 1022);
    }
}
