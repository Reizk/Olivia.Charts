using System.Numerics;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class FlowChartTests
{
    [Fact]
    public void BalancedSixNodeFixtureKeepsOriginalNodeEdgeIdentityVersionAndExactConservation()
    {
        var source = Balanced(); var snapshot = source.Query();
        Assert.Same(source, snapshot.Source); Assert.Equal(95, snapshot.DatasetVersion);
        Assert.Equal(6, snapshot.Nodes.Length); Assert.Equal(8, snapshot.Ribbons.Length);
        Assert.Equal(3, snapshot.LayerCount); Assert.Equal(14, snapshot.RequiredGeometryCount);
        Assert.Equal(46, snapshot.RequiredWorkCount); Assert.Equal(10, snapshot.Scale.RoundedValue);
        Assert.True(snapshot.Scale.IsExact); Assert.False(snapshot.Scale.IsOverflow);
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2 }, snapshot.Nodes.Select(n => n.Layer));
        Assert.Equal(new[] { FlowNodeKind.Source, FlowNodeKind.Source, FlowNodeKind.Internal,
            FlowNodeKind.Internal, FlowNodeKind.Sink, FlowNodeKind.Sink }, snapshot.Nodes.Select(n => n.Kind));
        Assert.All(snapshot.Nodes.Where(n => n.Kind == FlowNodeKind.Internal), n =>
        {
            Assert.Equal(FlowBalanceStatus.Balanced, n.BalanceStatus);
            Assert.Equal(5, n.Incoming.RoundedValue); Assert.Equal(n.Incoming.Units, n.Outgoing.Units);
        });
        Assert.All(snapshot.Nodes.Where(n => n.Kind != FlowNodeKind.Internal), n => Assert.Equal(FlowBalanceStatus.NotApplicable, n.BalanceStatus));
        for (var i = 0; i < source.Nodes.Length; i++)
        {
            var hit = FlowChartGeometry.GetHit(snapshot, i)!.Value;
            Assert.True(hit.IsNode); Assert.Same(source.Nodes[i], hit.NodeHit!.Value.Node);
            Assert.Null(hit.EdgeHit); Assert.Equal(95, hit.DatasetVersion); Assert.Equal(source.Nodes[i].Key, hit.Key);
        }
        for (var i = 0; i < source.Edges.Length; i++)
        {
            var hit = FlowChartGeometry.GetHit(snapshot, source.Nodes.Length + i)!.Value;
            Assert.False(hit.IsNode); Assert.Null(hit.NodeHit); Assert.Same(source.Edges[i], hit.EdgeHit!.Value.Edge);
            Assert.Equal(source.Edges[i].Weight, hit.EdgeHit.Value.Weight); Assert.Equal(95, hit.DatasetVersion);
            Assert.Equal(source.Edges[i].SourceKey, hit.EdgeHit.Value.SourceKey);
            Assert.Equal(source.Edges[i].TargetKey, hit.EdgeHit.Value.TargetKey);
        }
    }

    [Fact]
    public void TinyExactImbalanceIsNotHiddenByEqualRoundedTotals()
    {
        var source = new FlowChartSource(Nodes("a", "b", "c", "d"),
            [new("one", "a", "b", 1), new("tiny", "c", "b", double.Epsilon), new("out", "b", "d", 1)]);
        var middle = source.Query().Nodes[1];
        Assert.Equal(FlowBalanceStatus.Imbalanced, middle.BalanceStatus);
        Assert.Equal(1, middle.Incoming.RoundedValue); Assert.Equal(1, middle.Outgoing.RoundedValue);
        Assert.Equal(BigInteger.One, middle.Incoming.Units - middle.Outgoing.Units);
        Assert.False(middle.Incoming.IsExact); Assert.True(middle.Outgoing.IsExact);
        Assert.False(middle.Incoming.IsOverflow);
    }

    [Fact]
    public void ExactOverflowRemainsBalancedAndIsReportedBeforeRoundedInfinity()
    {
        var source = new FlowChartSource(Nodes("a", "b", "c", "d", "e"),
            [new("a-c", "a", "c", double.MaxValue), new("b-c", "b", "c", double.MaxValue),
             new("c-d", "c", "d", double.MaxValue), new("c-e", "c", "e", double.MaxValue)]);
        var snapshot = source.Query(); var middle = snapshot.Nodes[2];
        Assert.Equal(FlowBalanceStatus.Balanced, middle.BalanceStatus);
        Assert.Equal(Units(double.MaxValue) * 2, middle.Incoming.Units);
        Assert.Equal(middle.Incoming.Units, middle.Outgoing.Units);
        Assert.True(middle.Incoming.IsOverflow); Assert.True(middle.Outgoing.IsOverflow);
        Assert.False(middle.Incoming.IsExact); Assert.Equal(double.PositiveInfinity, middle.Incoming.RoundedValue);
        Assert.True(snapshot.Scale.IsOverflow); Assert.All(snapshot.Ribbons, r => Assert.True(double.IsFinite(r.Thickness)));
        var justOver = new FlowChartSource(Nodes("a", "b", "c"),
            [new("large", "a", "c", double.MaxValue), new("tiny", "b", "c", double.Epsilon)]).Query().Nodes[2].Incoming;
        Assert.True(justOver.IsOverflow); Assert.False(justOver.IsExact);
        Assert.Equal(double.MaxValue, justOver.RoundedValue); Assert.Equal(Units(double.MaxValue) + 1, justOver.Units);
    }

    [Fact]
    public void TotalRoundingUsesNearestEvenWithoutLosingTheExactUnits()
    {
        foreach (var (small, expected) in new[] { (Math.ScaleB(1d, -53), 1d),
            (3 * Math.ScaleB(1d, -53), Math.BitIncrement(Math.BitIncrement(1d))) })
        {
            var total = new FlowChartSource(Nodes("a", "b", "c"),
                [new("big", "a", "c", 1), new("small", "b", "c", small)]).Query().Nodes[2].Incoming;
            Assert.Equal(Units(1) + Units(small), total.Units); Assert.Equal(expected, total.RoundedValue);
            Assert.False(total.IsExact); Assert.False(total.IsOverflow);
        }
    }

    [Fact]
    public void ZeroEdgesStillDefineTopologyAndKeepSignedZeroWithNoRibbonHitArea()
    {
        var snapshot = new FlowChartSource(Nodes("a", "b", "c", "isolated"),
            [new("negative-zero", "a", "b", -0d), new("zero", "b", "c", 0)]).Query();
        Assert.Equal(new[] { 0, 1, 2, 0 }, snapshot.Nodes.Select(n => n.Layer));
        Assert.Equal(new[] { FlowNodeKind.Source, FlowNodeKind.Internal, FlowNodeKind.Sink, FlowNodeKind.Isolated }, snapshot.Nodes.Select(n => n.Kind));
        Assert.Equal(FlowBalanceStatus.Balanced, snapshot.Nodes[1].BalanceStatus);
        Assert.Equal(FlowBalanceStatus.NotApplicable, snapshot.Nodes[3].BalanceStatus);
        Assert.False(snapshot.IsEmpty); Assert.Equal(BigInteger.Zero, snapshot.Scale.Units);
        Assert.All(snapshot.Nodes, n => Assert.True(n.Height > 0));
        Assert.All(snapshot.Ribbons, r => { Assert.Equal(FlowRibbonStatus.Zero, r.Status); Assert.Equal(0, r.Thickness); });
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(snapshot.Ribbons[0].Hit.Weight));
        var hit = FlowChartGeometry.GetHit(snapshot, 4)!.Value;
        Assert.Equal("negative-zero", hit.Key); Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(hit.EdgeHit!.Value.Weight));
        Assert.Null(FlowChartGeometry.HitTest(snapshot, .5, .99));
    }

    [Fact]
    public void LongestPathLayersIncludeSkipEdgesAndDisconnectedComponentsInInputOrder()
    {
        var snapshot = new FlowChartSource(Nodes("last", "side", "first", "middle", "alone", "tail"),
            [new("skip", "first", "last", 1), new("first-middle", "first", "middle", 0),
             new("middle-last", "middle", "last", 1), new("side-tail", "side", "tail", 1)]).Query();
        Assert.Equal(new[] { 2, 0, 0, 1, 0, 1 }, snapshot.Nodes.Select(n => n.Layer));
        Assert.Equal(new[] { "last", "side", "first", "middle", "alone", "tail" }, snapshot.Nodes.Select(n => n.Hit.Key));
        Assert.True(snapshot.Nodes[1].Top < snapshot.Nodes[2].Top); Assert.True(snapshot.Nodes[2].Top < snapshot.Nodes[4].Top);
        Assert.True(snapshot.Nodes[3].Top < snapshot.Nodes[5].Top);
        Assert.All(snapshot.Ribbons, r => Assert.True(snapshot.Nodes[r.TargetNodeIndex].Layer > snapshot.Nodes[r.SourceNodeIndex].Layer));
        Assert.Equal(2, snapshot.Nodes[snapshot.Ribbons[0].TargetNodeIndex].Layer - snapshot.Nodes[snapshot.Ribbons[0].SourceNodeIndex].Layer);
    }

    [Fact]
    public void OneGlobalScaleGivesBitExactMatchingEndpointWidthsAndOrderedNonoverlappingPorts()
    {
        var snapshot = Balanced().Query(); var q = FlowChartGeometry.CoordinateUnits;
        foreach (var ribbon in snapshot.Ribbons)
        {
            var expectedTicks = (long)(Units(ribbon.Hit.Weight) * (4 * q) / (5 * snapshot.Scale.Units));
            Assert.Equal(expectedTicks * FlowChartGeometry.CoordinateQuantum, ribbon.Thickness);
            Assert.Equal(ribbon.Thickness, ribbon.SourceBottom - ribbon.SourceTop);
            Assert.Equal(ribbon.Thickness, ribbon.TargetBottom - ribbon.TargetTop);
            Assert.Equal(FlowRibbonStatus.Visible, ribbon.Status);
            var source = snapshot.Nodes[ribbon.SourceNodeIndex]; var target = snapshot.Nodes[ribbon.TargetNodeIndex];
            Assert.Equal(source.Right, ribbon.StartX); Assert.Equal(target.Left, ribbon.EndX);
            Assert.InRange(ribbon.SourceTop, source.Top, source.Bottom); Assert.InRange(ribbon.SourceBottom, ribbon.SourceTop, source.Bottom);
            Assert.InRange(ribbon.TargetTop, target.Top, target.Bottom); Assert.InRange(ribbon.TargetBottom, ribbon.TargetTop, target.Bottom);
        }
        foreach (var group in snapshot.Ribbons.GroupBy(r => r.SourceNodeIndex))
        {
            var ordered = group.ToArray();
            for (var i = 1; i < ordered.Length; i++) Assert.Equal(ordered[i - 1].SourceBottom, ordered[i].SourceTop);
        }
        foreach (var group in snapshot.Ribbons.GroupBy(r => r.TargetNodeIndex))
        {
            var ordered = group.ToArray();
            for (var i = 1; i < ordered.Length; i++) Assert.Equal(ordered[i - 1].TargetBottom, ordered[i].TargetTop);
        }
        Assert.Equal(snapshot.Ribbons[0].Thickness, snapshot.Ribbons[7].Thickness);
    }

    [Fact]
    public void UniformAndSubnormalOnlyWeightsUseTheSameProportionalMapping()
    {
        foreach (var value in new[] { 1d, double.Epsilon, double.MaxValue })
        {
            var snapshot = new FlowChartSource(Nodes("a", "b", "c"),
                [new("a-c", "a", "c", value), new("b-c", "b", "c", value)]).Query();
            Assert.Equal(snapshot.Ribbons[0].Thickness, snapshot.Ribbons[1].Thickness);
            Assert.Equal(FlowRibbonStatus.Visible, snapshot.Ribbons[0].Status);
            Assert.Equal(Units(value) * 2, snapshot.Scale.Units);
            Assert.InRange(snapshot.Ribbons[0].Thickness, .4 - FlowChartGeometry.CoordinateQuantum, .4);
        }
    }

    [Fact]
    public void PrecisionCollapsedPositiveWeightsRemainDistinctFromZeroAndSelectable()
    {
        var snapshot = new FlowChartSource(Nodes("a", "b", "c", "d"),
            [new("large", "a", "d", double.MaxValue), new("tiny", "b", "d", double.Epsilon),
             new("zero", "c", "d", 0)]).Query();
        Assert.Equal(new[] { FlowRibbonStatus.Visible, FlowRibbonStatus.Collapsed, FlowRibbonStatus.Zero }, snapshot.Ribbons.Select(r => r.Status));
        Assert.Equal(0, snapshot.Ribbons[1].Thickness); Assert.Equal(0, snapshot.Ribbons[2].Thickness);
        Assert.Equal(double.Epsilon, FlowChartGeometry.GetHit(snapshot, 5)!.Value.EdgeHit!.Value.Weight);
        Assert.Equal(0, FlowChartGeometry.GetHit(snapshot, 6)!.Value.EdgeHit!.Value.Weight);
        var tiny = snapshot.Ribbons[1];
        Assert.NotEqual("tiny", FlowChartGeometry.HitTest(snapshot, (tiny.StartX + tiny.EndX) / 2, (tiny.SourceTop + tiny.TargetTop) / 2)?.Key);
        Assert.Equal(Units(double.MaxValue) + 1, snapshot.Nodes[3].Incoming.Units);
    }

    [Fact]
    public void BothBudgetsRefuseAtomicallyAndExactThresholdsAdmitTheWholeFrame()
    {
        var source = Balanced();
        foreach (var options in new[] { new FlowChartOptions(13, 46), new FlowChartOptions(14, 45), new FlowChartOptions(0, 0) })
        {
            var snapshot = source.Query(options);
            Assert.Equal(ChartBudgetStatus.InsufficientBudget, snapshot.BudgetStatus); Assert.Same(options, snapshot.Options);
            Assert.Empty(snapshot.Nodes); Assert.Empty(snapshot.Ribbons); Assert.Equal(BigInteger.Zero, snapshot.Scale.Units);
            Assert.Equal(14, snapshot.RequiredGeometryCount); Assert.Equal(46, snapshot.RequiredWorkCount);
            Assert.False(snapshot.IsEmpty); Assert.Null(FlowChartGeometry.GetHit(snapshot, 0));
            Assert.Null(FlowChartGeometry.HitTest(snapshot, .1, .1));
        }
        var admitted = source.Query(new(14, 46));
        Assert.Equal(ChartBudgetStatus.Satisfied, admitted.BudgetStatus); Assert.Equal(6, admitted.Nodes.Length); Assert.Equal(8, admitted.Ribbons.Length);
    }

    [Fact]
    public void EmptyAndIsolatedGraphsAreDistinctAndHaveFiniteBoundedGeometry()
    {
        var empty = new FlowChartSource([], []).Query(new(0, 0));
        Assert.True(empty.IsEmpty); Assert.Equal(0, empty.LayerCount); Assert.Equal(ChartBudgetStatus.Satisfied, empty.BudgetStatus);
        Assert.Equal(0, empty.RequiredGeometryCount); Assert.Equal(0, empty.RequiredWorkCount);
        Assert.Empty(empty.Nodes); Assert.Empty(empty.Ribbons); Assert.Null(FlowChartGeometry.GetHit(empty, 0));
        var isolated = new FlowChartSource(Nodes("one"), []).Query();
        Assert.False(isolated.IsEmpty); Assert.Equal(1, isolated.LayerCount);
        Assert.Equal(FlowNodeKind.Isolated, isolated.Nodes[0].Kind); Assert.Equal(FlowBalanceStatus.NotApplicable, isolated.Nodes[0].BalanceStatus);
        AssertGeometry(isolated); var node = isolated.Nodes[0];
        Assert.Equal("one", FlowChartGeometry.HitTest(isolated, node.Left + node.Width / 2, node.Top + node.Height / 2)!.Value.Key);
    }

    [Fact]
    public void HalfOpenHitsMatchStraightStripsWithNodeAndLastPaintedRibbonPrecedence()
    {
        var snapshot = new FlowChartSource(Nodes("a", "b", "c", "d"),
            [new("first", "a", "d", 1), new("last", "b", "c", 1)]).Query();
        foreach (var node in snapshot.Nodes)
        {
            Assert.Equal(node.Hit.Key, FlowChartGeometry.HitTest(snapshot, node.Left, node.Top)!.Value.Key);
            Assert.Equal(node.Hit.Key, FlowChartGeometry.HitTest(snapshot, Math.BitDecrement(node.Right), Math.BitDecrement(node.Bottom))!.Value.Key);
            Assert.NotEqual(node.Hit.Key, FlowChartGeometry.HitTest(snapshot, node.Left, node.Bottom)?.Key);
            Assert.NotEqual(node.Hit.Key, FlowChartGeometry.HitTest(snapshot, node.Right, node.Top)?.Key);
        }
        var last = snapshot.Ribbons[1]; var x = (last.StartX + last.EndX) / 2;
        var top = (last.SourceTop + last.TargetTop) / 2;
        Assert.Equal("last", FlowChartGeometry.HitTest(snapshot, x, top)!.Value.Key);
        Assert.Equal("last", FlowChartGeometry.HitTest(snapshot, x, top + last.Thickness / 2)!.Value.Key);
        Assert.Null(FlowChartGeometry.HitTest(snapshot, x, top + last.Thickness));
        Assert.Equal("last", FlowChartGeometry.HitTest(snapshot, last.StartX, last.SourceTop + last.Thickness / 2)!.Value.Key);
        Assert.Equal("c", FlowChartGeometry.HitTest(snapshot, last.EndX, last.TargetTop + last.Thickness / 2)!.Value.Key);
        foreach (var point in new[] { (-double.Epsilon, 0d), (0d, -double.Epsilon), (1d, .5), (.5, 1d), (double.MaxValue, 0d) })
            Assert.Null(FlowChartGeometry.HitTest(snapshot, point.Item1, point.Item2));
        Assert.Null(FlowChartGeometry.GetHit(snapshot, -1)); Assert.Null(FlowChartGeometry.GetHit(snapshot, 6));
    }

    [Fact]
    public void ProjectedStripPredicateUsesExactSignedCornersAndHalfOpenSlopedBoundaries()
    {
        static bool Projected(double x, double y) => FlowChartGeometry.ContainsRibbon(x, y, -3, 7, 1, 3, 2, 5);
        Assert.True(Projected(-3, 1)); Assert.False(Projected(-3, 2)); Assert.False(Projected(7, 4));
        Assert.True(Projected(2, 2)); Assert.False(Projected(2, Math.BitDecrement(2d)));
        Assert.True(Projected(2, Math.BitDecrement(3.5))); Assert.False(Projected(2, 3.5));
        Assert.True(FlowChartGeometry.ContainsRibbon(0, 0, -double.MaxValue, double.MaxValue,
            -double.MaxValue, -double.MaxValue, double.MaxValue, double.MaxValue));
        Assert.False(FlowChartGeometry.ContainsRibbon(double.MaxValue, 0, -double.MaxValue, double.MaxValue,
            -double.MaxValue, -double.MaxValue, double.MaxValue, double.MaxValue));
        Assert.False(FlowChartGeometry.ContainsRibbon(1, 1, 1, 1, 0, 0, 2, 2));
        Assert.False(FlowChartGeometry.ContainsRibbon(.5, .5, 0, 1, 0, 1, 0, 1));
        Assert.True(FlowChartGeometry.ContainsRibbon(.5, .25, 0, 1, 0, 0, 0, 1));
        Assert.False(FlowChartGeometry.ContainsRibbon(0, 0, 0, 1, 0, 0, 0, 1));
        Assert.Throws<ArgumentException>(() => FlowChartGeometry.ContainsRibbon(0, 0, 1, 0, 0, 0, 1, 1));
        Assert.Throws<ArgumentException>(() => FlowChartGeometry.ContainsRibbon(0, 0, 0, 1, 2, 0, 1, 1));
        Assert.Throws<ArgumentException>(() => FlowChartGeometry.ContainsRibbon(0, 0, 0, 1, 0, 2, 1, 1));
        for (var argument = 0; argument < 8; argument++)
            foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                var values = new[] { .5, .5, 0, 1, 0, 0, 1, 1 }; values[argument] = invalid;
                Assert.Throws<ArgumentOutOfRangeException>(() => FlowChartGeometry.ContainsRibbon(
                    values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]));
            }
    }

    [Fact]
    public void ImmutableSnapshotsDoNotChangeWhenCallerCollectionsAreReplaced()
    {
        var nodes = Nodes("a", "b"); var edges = new[] { new FlowEdge("edge", "a", "b", 3) };
        var originalNode = nodes[0]; var originalEdge = edges[0];
        var source = new FlowChartSource(nodes, edges, 7); var before = source.Query();
        nodes[0] = new("new", "New"); edges[0] = new("other", "a", "b", 99);
        var after = source.Query();
        Assert.Same(originalNode, before.Nodes[0].Hit.Node); Assert.Same(originalEdge, before.Ribbons[0].Hit.Edge);
        Assert.Equal(before.Nodes.ToArray(), after.Nodes.ToArray()); Assert.Equal(before.Ribbons.ToArray(), after.Ribbons.ToArray());
        Assert.Equal(3, source.Edges[0].Weight); Assert.Equal(7, before.DatasetVersion);
    }

    [Fact]
    public void MaximumGraphUsesAllOriginalRecordsWithinFiniteDeterministicGeometry()
    {
        var nodes = Enumerable.Range(0, 64).Select(i => new FlowNode($"n{i}", "Same")).ToArray();
        var edges = (from sourceIndex in Enumerable.Range(0, 32) from targetIndex in Enumerable.Range(32, 32)
                     select new FlowEdge($"e{sourceIndex}-{targetIndex}", $"n{sourceIndex}", $"n{targetIndex}", 1)).Take(256).ToArray();
        var source = new FlowChartSource(nodes, edges); var first = source.Query(); var second = source.Query();
        Assert.Equal(64, first.Nodes.Length); Assert.Equal(256, first.Ribbons.Length);
        Assert.Equal(320, first.RequiredGeometryCount); Assert.Equal(832, first.RequiredWorkCount);
        Assert.Equal(ChartBudgetStatus.Satisfied, first.BudgetStatus); Assert.Equal(first.Nodes.ToArray(), second.Nodes.ToArray()); Assert.Equal(first.Ribbons.ToArray(), second.Ribbons.ToArray());
        AssertGeometry(first); Assert.NotNull(FlowChartGeometry.GetHit(first, 319)); Assert.Null(FlowChartGeometry.GetHit(first, 320));
        for (var i = 0; i < 64; i++) Assert.Same(nodes[i], first.Nodes[i].Hit.Node);
        for (var i = 0; i < 256; i++) Assert.Same(edges[i], first.Ribbons[i].Hit.Edge);
    }

    [Fact]
    public void GraphAdmissionRejectsDuplicateIdentitiesEndpointsSelfEdgesUnknownEndpointsAndCycles()
    {
        Assert.Throws<ArgumentException>(() => new FlowChartSource(Nodes("a", "a"), []));
        Assert.Throws<ArgumentException>(() => new FlowChartSource(Nodes("a", "b", "c"), [new("e", "a", "b", 1), new("e", "a", "c", 1)]));
        Assert.Throws<ArgumentException>(() => new FlowChartSource(Nodes("a", "b"), [new("e", "a", "b", 1), new("f", "a", "b", 0)]));
        Assert.Throws<ArgumentException>(() => new FlowChartSource(Nodes("a"), [new("e", "a", "a", 0)]));
        Assert.Throws<ArgumentException>(() => new FlowChartSource(Nodes("a"), [new("e", "a", "missing", 1)]));
        Assert.Throws<ArgumentException>(() => new FlowChartSource(Nodes("a"), [new("e", "missing", "a", 1)]));
        foreach (var weight in new[] { 0d, 1d })
        {
            Assert.Throws<ArgumentException>(() => new FlowChartSource(Nodes("a", "b"), [new("a-b", "a", "b", weight), new("b-a", "b", "a", weight)]));
            Assert.Throws<ArgumentException>(() => new FlowChartSource(Nodes("a", "b", "c", "outside"),
                [new("a-b", "a", "b", weight), new("b-c", "b", "c", weight), new("c-a", "c", "a", weight)]));
        }
        var caseSensitive = new FlowChartSource(Nodes("a", "A"), [new("e", "a", "A", 1)]).Query();
        Assert.Equal(2, caseSensitive.Nodes.Length); Assert.All(caseSensitive.Nodes, n => Assert.Equal("Same", n.Hit.Label));
    }

    [Fact]
    public void MissingNegativeAndNonfiniteWeightsAreExplicitlyRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowEdge("e", "a", "b", null));
        foreach (var weight in new[] { -1d, -double.Epsilon, double.MinValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => new FlowEdge("e", "a", "b", weight));
    }

    [Fact]
    public void NodeAndEdgeCapPlusOneAdmissionDisposesUnboundedEnumerators()
    {
        var advances = 0; var disposed = false;
        IEnumerable<FlowNode> EndlessNodes()
        {
            try { while (true) { advances++; yield return new($"n{advances}", "Same"); } }
            finally { disposed = true; }
        }
        Assert.Throws<ArgumentException>(() => new FlowChartSource(EndlessNodes(), []));
        Assert.Equal(65, advances); Assert.True(disposed);
        advances = 0; disposed = false;
        IEnumerable<FlowEdge> EndlessEdges()
        {
            try { while (true) { advances++; yield return new($"e{advances}", "a", "b", 0); } }
            finally { disposed = true; }
        }
        Assert.Throws<ArgumentException>(() => new FlowChartSource(Nodes("a", "b"), EndlessEdges()));
        Assert.Equal(257, advances); Assert.True(disposed);
    }

    [Fact]
    public void NullTextInvalidUnicodeVersionsBudgetsAndCoordinatesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new FlowNode(null!, "Label"));
        Assert.Throws<ArgumentNullException>(() => new FlowNode("a", null!));
        Assert.Throws<ArgumentNullException>(() => new FlowEdge(null!, "a", "b", 1));
        Assert.Throws<ArgumentNullException>(() => new FlowEdge("e", null!, "b", 1));
        Assert.Throws<ArgumentNullException>(() => new FlowEdge("e", "a", null!, 1));
        foreach (var key in new[] { "", " ", new string('x', 257), "\uD800", "\uDC00" })
        {
            Assert.Throws<ArgumentException>(() => new FlowNode(key, "Same"));
            Assert.Throws<ArgumentException>(() => new FlowEdge(key, "a", "b", 1));
            Assert.Throws<ArgumentException>(() => new FlowEdge("e", key, "b", 1));
            Assert.Throws<ArgumentException>(() => new FlowEdge("e", "a", key, 1));
        }
        Assert.Throws<ArgumentException>(() => new FlowNode("a", new string('x', 257)));
        Assert.Throws<ArgumentException>(() => new FlowNode("a", "\uD800"));
        Assert.Throws<ArgumentNullException>(() => new FlowChartSource(null!, []));
        Assert.Throws<ArgumentNullException>(() => new FlowChartSource([], null!));
        Assert.Throws<ArgumentNullException>(() => new FlowChartSource([null!], []));
        Assert.Throws<ArgumentNullException>(() => new FlowChartSource(Nodes("a"), [null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowChartSource([], [], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowChartOptions(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowChartOptions(321));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowChartOptions(workBudget: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowChartOptions(workBudget: 833));
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FlowChartGeometry.HitTest(Balanced().Query(), invalid, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => FlowChartGeometry.HitTest(Balanced().Query(), 0, invalid));
        }
        Assert.Throws<ArgumentNullException>(() => FlowChartGeometry.HitTest(null!, 0, 0));
        Assert.Throws<ArgumentNullException>(() => FlowChartGeometry.GetHit(null!, 0));
    }

    [Fact]
    public void DeterministicFullExponentDagsKeepExactSumsAndFiniteGeometryWithoutSampling()
    {
        var random = new Random(95017);
        for (var iteration = 0; iteration < 48; iteration++)
        {
            var nodes = Enumerable.Range(0, 16).Select(i => new FlowNode($"n{i}", "Same")).ToArray();
            var edges = new List<FlowEdge>(); var incoming = new BigInteger[16]; var outgoing = new BigInteger[16];
            for (var source = 0; source < 16; source++)
                for (var target = source + 1; target < 16; target++)
                    if (random.Next(3) == 0)
                    {
                        var weight = Math.ScaleB(random.NextDouble(), random.Next(-1074, 1024));
                        edges.Add(new($"e{source}-{target}", $"n{source}", $"n{target}", weight));
                        incoming[target] += Units(weight); outgoing[source] += Units(weight);
                    }
            var snapshot = new FlowChartSource(nodes, edges).Query(); AssertGeometry(snapshot);
            Assert.Equal(edges.Count, snapshot.Ribbons.Length);
            for (var i = 0; i < 16; i++)
            {
                Assert.Equal(incoming[i], snapshot.Nodes[i].Incoming.Units); Assert.Equal(outgoing[i], snapshot.Nodes[i].Outgoing.Units);
                if (snapshot.Nodes[i].Kind == FlowNodeKind.Internal)
                    Assert.Equal(incoming[i] == outgoing[i] ? FlowBalanceStatus.Balanced : FlowBalanceStatus.Imbalanced, snapshot.Nodes[i].BalanceStatus);
            }
        }
    }

    private static void AssertGeometry(FlowChartSnapshot snapshot)
    {
        foreach (var node in snapshot.Nodes)
        {
            Assert.InRange(node.Left, 0, 1); Assert.InRange(node.Right, node.Left, 1);
            Assert.InRange(node.Top, 0, 1); Assert.InRange(node.Bottom, node.Top, 1);
            Assert.True(node.Width > 0); Assert.True(node.Height > 0);
        }
        foreach (var ribbon in snapshot.Ribbons)
        {
            Assert.True(ribbon.EndX > ribbon.StartX); Assert.InRange(ribbon.Thickness, 0, 1);
            Assert.InRange(ribbon.SourceTop, 0, 1); Assert.InRange(ribbon.SourceBottom, ribbon.SourceTop, 1);
            Assert.InRange(ribbon.TargetTop, 0, 1); Assert.InRange(ribbon.TargetBottom, ribbon.TargetTop, 1);
            Assert.Equal(ribbon.Thickness, ribbon.SourceBottom - ribbon.SourceTop);
            Assert.Equal(ribbon.Thickness, ribbon.TargetBottom - ribbon.TargetTop);
        }
    }
    private static BigInteger Units(double value)
    {
        if (value == 0) return BigInteger.Zero;
        var exponent = Math.ILogB(value);
        if (exponent < -1022) return new BigInteger(value / double.Epsilon);
        var integer = new BigInteger(Math.ScaleB(value, 52 - exponent));
        return integer << (exponent + 1022);
    }
    private static FlowNode[] Nodes(params string[] keys) => keys.Select(key => new FlowNode(key, "Same")).ToArray();
    private static FlowChartSource Balanced() => new(Nodes("intake-a", "intake-b", "review-a", "review-b", "delivery-a", "delivery-b"),
        [new("a-ra", "intake-a", "review-a", 4), new("a-rb", "intake-a", "review-b", 2),
         new("b-ra", "intake-b", "review-a", 1), new("b-rb", "intake-b", "review-b", 3),
         new("ra-da", "review-a", "delivery-a", 3), new("ra-db", "review-a", "delivery-b", 2),
         new("rb-da", "review-b", "delivery-a", 1), new("rb-db", "review-b", "delivery-b", 4)], 95);
}
