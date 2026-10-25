using System.Numerics;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class TreemapChartTests
{
    [Fact]
    public void UnorderedForestKeepsSiblingOrderRawIdentityVersionAndExactLeafRanges()
    {
        var source = Fixture(); var snapshot = source.Query();
        Assert.Same(source, snapshot.Source); Assert.Equal(96, snapshot.DatasetVersion);
        Assert.Equal(new[] { 1, 2 }, source.RootIndices); Assert.Equal(new[] { 0, 3, 4, 5 }, source.LeafIndices);
        Assert.Equal(new[] { 0, 3 }, source.GetChildren("a")); Assert.Equal(new[] { 4, 5 }, source.GetChildren("b"));
        Assert.Equal(new[] { "a", "a3", "a1", "b", "b0", "b6" }, snapshot.Rectangles.Select(r => r.Hit.Key));
        Assert.Equal(new[] { 0, 1, 1, 0, 1, 1 }, snapshot.Rectangles.Select(r => r.Depth));
        Assert.Equal(new[] { 0, 0, 1, 2, 2, 3 }, snapshot.Rectangles.Select(r => r.Hit.LeafStart));
        Assert.Equal(new[] { 2, 1, 1, 2, 1, 1 }, snapshot.Rectangles.Select(r => r.Hit.LeafCount));
        Assert.Equal(new[] { false, true, true, false, true, true }, snapshot.Rectangles.Select(r => r.IsLeaf));
        Assert.Equal(2, source.DepthCount); Assert.Equal(10, source.Total.RoundedValue);
        Assert.Equal(Units(10), source.Total.Units); Assert.Equal(source.Total, snapshot.Total);
        Assert.Equal(4, source.GetAggregate("a").RoundedValue); Assert.Equal(6, source.GetAggregate("b").RoundedValue);
        foreach (var rectangle in snapshot.Rectangles)
        {
            var hit = TreemapChartGeometry.GetHit(snapshot, Array.IndexOf(snapshot.Rectangles.ToArray(), rectangle))!.Value;
            Assert.Same(source.Nodes[rectangle.NodeIndex], hit.Node); Assert.Equal(96, hit.DatasetVersion);
            Assert.Equal("Same", hit.Label); Assert.Equal(hit.Node.Weight, hit.Weight); Assert.Equal(hit.Node.ParentKey, hit.ParentKey);
            var expected = source.LeafIndices.Skip(hit.LeafStart).Take(hit.LeafCount).Aggregate(BigInteger.Zero,
                (total, index) => total + Units(source.Nodes[index].Weight));
            Assert.Equal(expected, hit.Aggregate.Units);
        }
    }

    [Fact]
    public void ParentLeafSumPolicyRejectsNonzeroOwnWeightWithoutDoubleCounting()
    {
        Assert.Throws<ArgumentException>(() => new TreemapChartSource([new("root", "Same", null, 1), new("leaf", "Same", "root", 2)]));
        var source = new TreemapChartSource([new("root", "Same", null, -0d), new("leaf", "Same", "root", 2)]);
        Assert.Equal(Units(2), source.Total.Units); Assert.Equal(Units(2), source.GetAggregate("root").Units);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(source.Nodes[0].Weight));
        Assert.Equal(0, source.Query().Rectangles[0].Hit.Weight); Assert.Equal(2, source.Query().Rectangles[0].Hit.Aggregate.RoundedValue);
    }

    [Fact]
    public void MissingNegativeAndNonfiniteWeightsAreRejectedExplicitly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TreemapNode("a", "Same", null, null));
        foreach (var value in new[] { -1d, -double.Epsilon, double.MinValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => new TreemapNode("a", "Same", null, value));
    }

    [Fact]
    public void ExactAggregationKeepsTinyAddendsAndOverflowIndependentOfRoundedValue()
    {
        var source = new TreemapChartSource([new("a", "Same", null, double.MaxValue), new("b", "Same", null, double.Epsilon)]);
        Assert.Equal(Units(double.MaxValue) + 1, source.Total.Units);
        Assert.True(source.Total.IsOverflow); Assert.False(source.Total.IsExact); Assert.Equal(double.MaxValue, source.Total.RoundedValue);
        var overflow = new TreemapChartSource([new("a", "Same", null, double.MaxValue), new("b", "Same", null, double.MaxValue)]).Query();
        Assert.Equal(Units(double.MaxValue) * 2, overflow.Total.Units); Assert.True(overflow.Total.IsOverflow);
        Assert.False(overflow.Total.IsExact); Assert.Equal(double.PositiveInfinity, overflow.Total.RoundedValue);
        Assert.Equal(.5, overflow.Rectangles[0].Right); Assert.Equal(.5, overflow.Rectangles[1].Left);
        Assert.All(overflow.Rectangles, r => Assert.Equal(TreemapRectangleStatus.Visible, r.Status));
        AssertGeometry(overflow);
    }

    [Fact]
    public void ExactTotalsRoundToNearestEvenWithoutChangingRatioDenominators()
    {
        foreach (var (small, expected) in new[] { (Math.ScaleB(1d, -53), 1d),
            (3 * Math.ScaleB(1d, -53), Math.BitIncrement(Math.BitIncrement(1d))) })
        {
            var source = new TreemapChartSource([new("a", "Same", null, 1), new("b", "Same", null, small)]);
            Assert.Equal(Units(1) + Units(small), source.Total.Units); Assert.Equal(expected, source.Total.RoundedValue);
            Assert.False(source.Total.IsExact); Assert.False(source.Total.IsOverflow);
            var expectedEdge = (long)(TreemapChartGeometry.CoordinateUnits * Units(1) / source.Total.Units) * TreemapChartGeometry.CoordinateQuantum;
            Assert.Equal(expectedEdge, source.Query().Rectangles[0].Right);
        }
    }

    [Fact]
    public void ZeroAndCollapsedWeightsRetainKeyboardIdentityWithoutInflatingArea()
    {
        var snapshot = new TreemapChartSource([new("tiny", "Same", null, double.Epsilon), new("huge", "Same", null, double.MaxValue),
            new("negative-zero", "Same", null, -0d), new("zero", "Same", null, 0)]).Query();
        Assert.Equal(new[] { TreemapRectangleStatus.Collapsed, TreemapRectangleStatus.Visible, TreemapRectangleStatus.Zero,
            TreemapRectangleStatus.Zero }, snapshot.Rectangles.Select(r => r.Status));
        Assert.Equal(0, snapshot.Rectangles[0].Width); Assert.Equal(1, snapshot.Rectangles[1].Width);
        Assert.Equal(0, snapshot.Rectangles[2].Width); Assert.Equal(0, snapshot.Rectangles[3].Width);
        Assert.Equal(double.Epsilon, TreemapChartGeometry.GetHit(snapshot, 0)!.Value.Weight);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(TreemapChartGeometry.GetHit(snapshot, 2)!.Value.Weight));
        Assert.Equal("huge", TreemapChartGeometry.HitTest(snapshot, 0, 0)!.Value.Key);
        Assert.Equal("huge", TreemapChartGeometry.HitTest(snapshot, Math.BitDecrement(1d), .5)!.Value.Key);
    }

    [Fact]
    public void TrailingSubquantumWeightReceivesOnlyPartitionRemainderWhileLeadingWeightCollapses()
    {
        var tiny = new TreemapNode("tiny", "Same", null, double.Epsilon);
        var huge = new TreemapNode("huge", "Same", null, double.MaxValue);
        var leading = new TreemapChartSource([tiny, huge]).Query();
        var trailing = new TreemapChartSource([huge, tiny]).Query();
        Assert.Equal(TreemapRectangleStatus.Collapsed, leading.Rectangles[0].Status);
        Assert.Equal(0, leading.Rectangles[0].Width);
        Assert.Equal(TreemapRectangleStatus.Visible, trailing.Rectangles[1].Status);
        Assert.Equal(TreemapChartGeometry.CoordinateQuantum, trailing.Rectangles[1].Width);
        Assert.Equal(trailing.Rectangles[0].Right, trailing.Rectangles[1].Left);
        Assert.Equal(1, trailing.Rectangles[1].Right); Assert.Equal(leading.Total, trailing.Total);
        Assert.Same(tiny, TreemapChartGeometry.GetHit(leading, 0)!.Value.Node);
        Assert.Same(tiny, TreemapChartGeometry.GetHit(trailing, 1)!.Value.Node);
    }

    [Fact]
    public void EmptyAndAllZeroForestsHaveExplicitStateAndNoPointerAreas()
    {
        var empty = new TreemapChartSource([]).Query(new(geometryBudget: 0, workBudget: 0));
        Assert.True(empty.IsEmpty); Assert.False(empty.HasPositiveWeight); Assert.Equal(0, empty.RequiredGeometryCount);
        Assert.Equal(0, empty.RequiredWorkCount); Assert.Empty(empty.Rectangles); Assert.Equal(ChartBudgetStatus.Satisfied, empty.BudgetStatus);
        var zero = new TreemapChartSource([new("root", "Same", null, 0), new("a", "Same", "root", 0), new("b", "Same", "root", -0d)]).Query();
        Assert.False(zero.IsEmpty); Assert.False(zero.HasPositiveWeight); Assert.Equal(BigInteger.Zero, zero.Total.Units);
        Assert.True(zero.Total.IsExact); Assert.False(zero.Total.IsOverflow); Assert.Equal(0, zero.Total.RoundedValue);
        Assert.All(zero.Rectangles, r => { Assert.Equal(TreemapRectangleStatus.Zero, r.Status); Assert.True(r.Width == 0 || r.Height == 0); });
        Assert.NotNull(TreemapChartGeometry.GetHit(zero, 2)); Assert.Null(TreemapChartGeometry.HitTest(zero, 0, 0));
        Assert.Null(TreemapChartGeometry.HitTest(zero, .5, .5)); AssertGeometry(zero);
    }

    [Fact]
    public void AlternatingSliceDiceHasSharedEdgesNoPaddingAndExactParentCoverage()
    {
        var snapshot = Fixture().Query(); var a = snapshot.Rectangles[0]; var a3 = snapshot.Rectangles[1];
        var a1 = snapshot.Rectangles[2]; var b = snapshot.Rectangles[3]; var b0 = snapshot.Rectangles[4]; var b6 = snapshot.Rectangles[5];
        var boundary = (TreemapChartGeometry.CoordinateUnits * 4 / 10) * TreemapChartGeometry.CoordinateQuantum;
        Assert.Equal(0, a.Left); Assert.Equal(boundary, a.Right); Assert.Equal(a.Right, b.Left); Assert.Equal(1, b.Right);
        Assert.Equal(0, a.Top); Assert.Equal(1, a.Bottom); Assert.Equal(0, b.Top); Assert.Equal(1, b.Bottom);
        Assert.Equal(a.Left, a3.Left); Assert.Equal(a.Right, a3.Right); Assert.Equal(0, a3.Top); Assert.Equal(.75, a3.Bottom);
        Assert.Equal(a3.Bottom, a1.Top); Assert.Equal(1, a1.Bottom); Assert.Equal(a.Left, a1.Left); Assert.Equal(a.Right, a1.Right);
        Assert.Equal(0, b0.Height); Assert.Equal(b0.Bottom, b6.Top); Assert.Equal(1, b6.Bottom);
        Assert.Equal(b.Left, b6.Left); Assert.Equal(b.Right, b6.Right); AssertGeometry(snapshot);
    }

    [Fact]
    public void ThirdVisibleLevelAlternatesBackToHorizontalWithinItsParent()
    {
        var source = new TreemapChartSource([new("root", "Same", null, 0), new("group", "Same", "root", 0),
            new("other", "Same", "root", 3), new("a", "Same", "group", 1), new("b", "Same", "group", 2)]);
        var snapshot = source.Query(new(displayDepth: 3));
        var group = snapshot.Rectangles.Single(r => r.Hit.Key == "group");
        var a = snapshot.Rectangles.Single(r => r.Hit.Key == "a"); var b = snapshot.Rectangles.Single(r => r.Hit.Key == "b");
        Assert.Equal(.5, group.Bottom); Assert.Equal(group.Top, a.Top); Assert.Equal(group.Bottom, a.Bottom);
        Assert.Equal(group.Top, b.Top); Assert.Equal(group.Bottom, b.Bottom); Assert.Equal(a.Right, b.Left);
        Assert.Equal(group.Left, a.Left); Assert.Equal(group.Right, b.Right); Assert.Equal(2, a.Depth); AssertGeometry(snapshot);
    }

    [Fact]
    public void AncestorPrecisionCollapsePropagatesWithoutManufacturedDescendantArea()
    {
        var source = new TreemapChartSource([new("small", "Same", null, 0), new("huge", "Same", null, double.MaxValue),
            new("a", "Same", "small", double.Epsilon), new("b", "Same", "small", double.Epsilon)]);
        var snapshot = source.Query();
        Assert.All(snapshot.Rectangles.Where(r => r.Hit.Key != "huge"), r =>
        {
            Assert.Equal(TreemapRectangleStatus.Collapsed, r.Status); Assert.Equal(0, r.Width);
            Assert.NotNull(TreemapChartGeometry.GetHit(snapshot, Array.IndexOf(snapshot.Rectangles.ToArray(), r)));
        });
        Assert.Equal("huge", TreemapChartGeometry.HitTest(snapshot, 0, .25)!.Value.Key);
        var drilled = source.Query(focusKey: "small");
        Assert.All(drilled.Rectangles, r => Assert.Equal(TreemapRectangleStatus.Visible, r.Status));
        Assert.Equal(.5, drilled.Rectangles[0].Width); Assert.Equal(.5, drilled.Rectangles[1].Width);
        Assert.Equal(source.GetAggregate("small"), drilled.Total);
    }

    [Fact]
    public void DeepestDisplayedRawHitOwnsHalfOpenSiblingSeamsAndOutsideIsMiss()
    {
        var source = Fixture(); var snapshot = source.Query(); var edge = snapshot.Rectangles[0].Right;
        Assert.Equal("a3", TreemapChartGeometry.HitTest(snapshot, 0, 0)!.Value.Key);
        Assert.Equal("a1", TreemapChartGeometry.HitTest(snapshot, 0, .75)!.Value.Key);
        Assert.Equal("a3", TreemapChartGeometry.HitTest(snapshot, Math.BitDecrement(edge), Math.BitDecrement(.75))!.Value.Key);
        Assert.Equal("b6", TreemapChartGeometry.HitTest(snapshot, edge, 0)!.Value.Key);
        Assert.Equal("b6", TreemapChartGeometry.HitTest(snapshot, Math.BitDecrement(1d), Math.BitDecrement(1d))!.Value.Key);
        var shallow = source.Query(new(displayDepth: 1));
        Assert.Equal("a", TreemapChartGeometry.HitTest(shallow, 0, .5)!.Value.Key);
        Assert.Equal(2, shallow.RequiredGeometryCount); Assert.Equal(28, shallow.RequiredWorkCount);
        foreach (var (x, y) in new[] { (-double.Epsilon, .5), (.5, -double.Epsilon), (1d, .5), (.5, 1d), (double.MaxValue, .5) })
            Assert.Null(TreemapChartGeometry.HitTest(snapshot, x, y));
        Assert.Null(TreemapChartGeometry.GetHit(snapshot, -1)); Assert.Null(TreemapChartGeometry.GetHit(snapshot, 6));
    }

    [Fact]
    public void DrillFocusRescalesOnlyItsChildrenAndKeepsForestWideRawLeafRanges()
    {
        var source = Fixture(); var snapshot = source.Query(focusKey: "b");
        Assert.Equal("b", snapshot.FocusKey); Assert.Equal(new[] { "b0", "b6" }, snapshot.Rectangles.Select(r => r.Hit.Key));
        Assert.Equal(new[] { 0, 0 }, snapshot.Rectangles.Select(r => r.Depth));
        Assert.Equal(new[] { 2, 3 }, snapshot.Rectangles.Select(r => r.Hit.LeafStart));
        Assert.Equal(source.GetAggregate("b"), snapshot.Total); Assert.Equal(6, snapshot.Total.RoundedValue);
        Assert.Equal(0, snapshot.Rectangles[0].Width); Assert.Equal(1, snapshot.Rectangles[1].Width);
        Assert.Null(source.GetParentKey("b")); Assert.Equal("b", source.GetParentKey("b6"));
        Assert.Equal(source.Query().Rectangles.ToArray(), source.Query(focusKey: source.GetParentKey("b")).Rectangles.ToArray());
        Assert.Throws<ArgumentException>(() => source.Query(focusKey: "b6")); Assert.Throws<ArgumentException>(() => source.Query(focusKey: "unknown"));
    }

    [Fact]
    public void BothBudgetsRefuseTheWholeFrameAndExactThresholdsAdmitAllRectangles()
    {
        var source = Fixture(); var expected = source.Query();
        Assert.Equal(6, expected.RequiredGeometryCount); Assert.Equal(36, expected.RequiredWorkCount);
        foreach (var options in new[] { new TreemapChartOptions(geometryBudget: 5), new TreemapChartOptions(workBudget: 35),
            new TreemapChartOptions(geometryBudget: 0, workBudget: 0) })
        {
            var refused = source.Query(options);
            Assert.Equal(ChartBudgetStatus.InsufficientBudget, refused.BudgetStatus); Assert.Empty(refused.Rectangles);
            Assert.Equal(expected.Total, refused.Total); Assert.Equal(6, refused.RequiredGeometryCount); Assert.Equal(36, refused.RequiredWorkCount);
            Assert.Null(TreemapChartGeometry.GetHit(refused, 0)); Assert.Null(TreemapChartGeometry.HitTest(refused, .5, .5));
            Assert.Equal(expected.Rectangles.ToArray(), source.Query().Rectangles.ToArray());
        }
        var accepted = source.Query(new(geometryBudget: 6, workBudget: 36));
        Assert.Equal(ChartBudgetStatus.Satisfied, accepted.BudgetStatus); Assert.Equal(expected.Rectangles.ToArray(), accepted.Rectangles.ToArray());
        var focused = source.Query(new(geometryBudget: 2, workBudget: 28), "a");
        Assert.Equal(ChartBudgetStatus.Satisfied, focused.BudgetStatus); Assert.Equal(2, focused.Rectangles.Length);
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, source.Query(new(workBudget: 27), "a").BudgetStatus);
    }

    [Fact]
    public void CallerMutationCannotChangeFrozenNodesRangesOrPublishedGeometry()
    {
        var nodes = new[] { new TreemapNode("root", "Same", null, 0), new TreemapNode("leaf", "Same", "root", 7) };
        var original = nodes[1]; var source = new TreemapChartSource(nodes, 22); var snapshot = source.Query();
        nodes[0] = new("new", "Other", null, 20); nodes[1] = new("other", "Other", null, 30);
        Assert.Same(original, source.Nodes[1]); Assert.Same(original, snapshot.Rectangles[1].Hit.Node);
        Assert.Equal(7, source.Total.RoundedValue); Assert.Equal(22, snapshot.DatasetVersion);
        Assert.Equal(snapshot.Rectangles.ToArray(), source.Query().Rectangles.ToArray());
        Assert.Equal(new[] { 1 }, source.LeafIndices); Assert.Equal(new[] { 1 }, source.GetChildren("root"));
    }

    [Fact]
    public void MaximumFlatForestUsesAllNodesWithFiniteDeterministicCompleteCoverage()
    {
        var nodes = Enumerable.Range(0, TreemapChartSource.MaximumNodes).Select(i => new TreemapNode($"n{i}", "Same", null, 1)).ToArray();
        var source = new TreemapChartSource(nodes); var first = source.Query(); var second = source.Query();
        Assert.Equal(256, first.Rectangles.Length); Assert.Equal(256, first.RequiredGeometryCount); Assert.Equal(1536, first.RequiredWorkCount);
        Assert.Equal(256, source.LeafIndices.Length); Assert.Equal(1, source.DepthCount); Assert.Equal(Units(256), source.Total.Units);
        Assert.Equal(first.Rectangles.ToArray(), second.Rectangles.ToArray()); Assert.Equal(1, first.Rectangles[^1].Right);
        for (var i = 0; i < 256; i++)
        {
            var rectangle = first.Rectangles[i]; Assert.Same(nodes[i], rectangle.Hit.Node);
            Assert.Equal(i / 256d, rectangle.Left); Assert.Equal((i + 1) / 256d, rectangle.Right);
            Assert.Equal(nodes[i].Key, TreemapChartGeometry.HitTest(first, rectangle.Left, .5)!.Value.Key);
            Assert.Equal(i, rectangle.Hit.LeafStart); Assert.Equal(1, rectangle.Hit.LeafCount);
        }
        Assert.NotNull(TreemapChartGeometry.GetHit(first, 255)); Assert.Null(TreemapChartGeometry.GetHit(first, 256)); AssertGeometry(first);
    }

    [Fact]
    public void MaximumDepthIsAdmittedAndNextLevelIsRejectedBeforeAnyGeometry()
    {
        static TreemapNode[] Chain(int length) => Enumerable.Range(0, length).Select(i => new TreemapNode($"n{i}", "Same",
            i == 0 ? null : $"n{i - 1}", i == length - 1 ? 1 : 0)).ToArray();
        var source = new TreemapChartSource(Chain(16)); var snapshot = source.Query(new(displayDepth: 16));
        Assert.Equal(16, source.DepthCount); Assert.Equal(16, snapshot.Rectangles.Length); Assert.Equal(15, snapshot.Rectangles[^1].Depth);
        Assert.All(snapshot.Rectangles, r => { Assert.Equal(1, r.Width); Assert.Equal(1, r.Height); Assert.Equal(1, r.Hit.LeafCount); });
        Assert.Equal("n15", TreemapChartGeometry.HitTest(snapshot, .5, .5)!.Value.Key);
        Assert.Throws<ArgumentException>(() => new TreemapChartSource(Chain(17)));
    }

    [Fact]
    public void DuplicateKeysOrphansSelfParentsAndDisconnectedCyclesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new TreemapChartSource([new("a", "Same", null, 1), new("a", "Same", null, 1)]));
        Assert.Throws<ArgumentException>(() => new TreemapChartSource([new("a", "Same", "unknown", 0)]));
        Assert.Throws<ArgumentException>(() => new TreemapChartSource([new("a", "Same", "a", 0)]));
        Assert.Throws<ArgumentException>(() => new TreemapChartSource([new("a", "Same", "b", 0), new("b", "Same", "a", 0)]));
        Assert.Throws<ArgumentException>(() => new TreemapChartSource([new("root", "Same", null, 1), new("a", "Same", "b", 0),
            new("b", "Same", "c", 0), new("c", "Same", "a", 0)]));
        var ordinal = new TreemapChartSource([new("a", "Same", null, 0), new("A", "Same", "a", 1)]);
        Assert.Equal(2, ordinal.Nodes.Length); Assert.Equal(0, ordinal.GetNodeIndex("a")); Assert.Equal(1, ordinal.GetNodeIndex("A"));
    }

    [Fact]
    public void BoundedAdmissionDisposesAnEndlessInputAfterExactlyCapPlusOneAdvances()
    {
        var advances = 0; var disposed = false;
        IEnumerable<TreemapNode> Endless()
        {
            try { while (true) { advances++; yield return new($"n{advances}", "Same", null, 1); } }
            finally { disposed = true; }
        }
        Assert.Throws<ArgumentException>(() => new TreemapChartSource(Endless()));
        Assert.Equal(257, advances); Assert.True(disposed);
    }

    [Fact]
    public void EqualSubnormalOrMaximumLeavesHaveIdenticalGeometryAndNoLostAggregation()
    {
        TreemapChartRectangle[]? expected = null;
        foreach (var weight in new[] { double.Epsilon, 1d, double.MaxValue })
        {
            var source = new TreemapChartSource(Enumerable.Range(0, 3).Select(i => new TreemapNode($"n{i}", "Same", null, weight)));
            var snapshot = source.Query(); Assert.Equal(Units(weight) * 3, source.Total.Units);
            Assert.All(snapshot.Rectangles, r => Assert.Equal(TreemapRectangleStatus.Visible, r.Status));
            if (expected is not null)
                for (var i = 0; i < 3; i++) { Assert.Equal(expected[i].Left, snapshot.Rectangles[i].Left); Assert.Equal(expected[i].Right, snapshot.Rectangles[i].Right); }
            expected = snapshot.Rectangles.ToArray(); Assert.Equal(1, snapshot.Rectangles[^1].Right); AssertGeometry(snapshot);
        }
    }

    [Fact]
    public void TextValidationUsesBoundedValidUnicodeWithDisplayOnlyDuplicateLabels()
    {
        Assert.Throws<ArgumentNullException>(() => new TreemapNode(null!, "Same", null, 1));
        Assert.Throws<ArgumentNullException>(() => new TreemapNode("a", null!, null, 1));
        foreach (var text in new[] { "", " ", "\ud800", "\udc00", new string('x', 257) })
        {
            Assert.Throws<ArgumentException>(() => new TreemapNode(text, "Same", null, 1));
            Assert.Throws<ArgumentException>(() => new TreemapNode("a", "Same", text, 1));
        }
        Assert.Throws<ArgumentException>(() => new TreemapNode("a", "\ud800", null, 1));
        Assert.Throws<ArgumentException>(() => new TreemapNode("a", new string('x', 257), null, 1));
        var valid = new TreemapNode(new string('x', 256), "🙂中文", null, 1);
        Assert.Equal(256, valid.Key.Length); Assert.Equal("🙂中文", valid.Label);
        var emptyLabel = new TreemapNode("empty-label", "", null, 1); Assert.Equal("", emptyLabel.Label);
    }

    [Fact]
    public void NullInputsNegativeVersionsUnknownKeysAndInvalidOptionsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new TreemapChartSource(null!));
        Assert.Throws<ArgumentNullException>(() => new TreemapChartSource([null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TreemapChartSource([], -1));
        var source = Fixture();
        Assert.Throws<ArgumentNullException>(() => source.GetNodeIndex(null!));
        Assert.Throws<ArgumentException>(() => source.GetNodeIndex("unknown")); Assert.Throws<ArgumentException>(() => source.GetChildren("unknown"));
        Assert.Throws<ArgumentException>(() => source.GetAggregate("unknown")); Assert.Throws<ArgumentException>(() => source.GetParentKey("unknown"));
        foreach (var depth in new[] { int.MinValue, 0, 17, int.MaxValue }) Assert.Throws<ArgumentOutOfRangeException>(() => new TreemapChartOptions(displayDepth: depth));
        foreach (var budget in new[] { -1, 257, int.MaxValue }) Assert.Throws<ArgumentOutOfRangeException>(() => new TreemapChartOptions(geometryBudget: budget));
        foreach (var budget in new[] { -1, 1537, int.MaxValue }) Assert.Throws<ArgumentOutOfRangeException>(() => new TreemapChartOptions(workBudget: budget));
    }

    [Fact]
    public void NonfiniteCoordinatesAndNullSnapshotsAreRejected()
    {
        var snapshot = Fixture().Query();
        Assert.Throws<ArgumentNullException>(() => TreemapChartGeometry.HitTest(null!, 0, 0));
        Assert.Throws<ArgumentNullException>(() => TreemapChartGeometry.GetHit(null!, 0));
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => TreemapChartGeometry.HitTest(snapshot, invalid, .5));
            Assert.Throws<ArgumentOutOfRangeException>(() => TreemapChartGeometry.HitTest(snapshot, .5, invalid));
        }
    }

    private static TreemapChartSource Fixture() => new([new("a3", "Same", "a", 3), new("a", "Same", null, 0),
        new("b", "Same", null, 0), new("a1", "Same", "a", 1), new("b0", "Same", "b", 0), new("b6", "Same", "b", 6)], 96);

    private static BigInteger Units(double value)
    {
        var bits = (ulong)BitConverter.DoubleToInt64Bits(value); var exponent = (int)((bits >> 52) & 0x7ff);
        var mantissa = bits & 0x000f_ffff_ffff_ffffUL;
        return exponent == 0 ? new BigInteger(mantissa) : new BigInteger(mantissa | (1UL << 52)) << (exponent - 1);
    }

    private static void AssertGeometry(TreemapChartSnapshot snapshot)
    {
        foreach (var rectangle in snapshot.Rectangles)
        {
            Assert.InRange(rectangle.Left, 0, 1); Assert.InRange(rectangle.Right, rectangle.Left, 1);
            Assert.InRange(rectangle.Top, 0, 1); Assert.InRange(rectangle.Bottom, rectangle.Top, 1);
            foreach (var coordinate in new[] { rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom })
            { Assert.True(double.IsFinite(coordinate)); Assert.Equal(Math.Truncate(coordinate * TreemapChartGeometry.CoordinateUnits), coordinate * TreemapChartGeometry.CoordinateUnits); }
            if (rectangle.Status == TreemapRectangleStatus.Visible) { Assert.True(rectangle.Width > 0); Assert.True(rectangle.Height > 0); }
            else Assert.True(rectangle.Width == 0 || rectangle.Height == 0);
        }
    }
}
