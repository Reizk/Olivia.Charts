using System.Numerics;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class SunburstChartTests
{
    [Fact]
    public void OriginalTwoLevelWeightsPreserveRawIdentityAndExactRanges()
    {
        var source = Fixture(); var frame = source.Query();
        Assert.Same(source, frame.Source); Assert.Equal(97, frame.DatasetVersion);
        Assert.Equal(new[] { "a", "one", "two", "b", "three", "four" }, frame.Sectors.Select(s => s.Hit.Key));
        Assert.Equal(new[] { 0, 1, 1, 0, 1, 1 }, frame.Sectors.Select(s => s.Depth));
        Assert.Equal(new[] { 2, 1, 1, 2, 1, 1 }, frame.Sectors.Select(s => s.Hit.LeafCount));
        Assert.Equal(new[] { 0, 0, 1, 2, 2, 3 }, frame.Sectors.Select(s => s.Hit.LeafStart));
        Assert.Equal(Units(10), frame.Total.Units); Assert.Equal(source.Hierarchy.Total, frame.Total);
        Assert.Equal(new[] { 1, 3 }, source.GetChildren("a")); Assert.Equal("a", source.GetParentKey("two"));
        Assert.Equal(Units(3), source.GetAggregate("a").Units);
        foreach (var sector in frame.Sectors)
        {
            Assert.Same(source.Nodes[sector.NodeIndex], sector.Hit.Node); Assert.Equal(97, sector.Hit.DatasetVersion);
            Assert.Equal(sector.Hit.Node.Weight, sector.Hit.Weight); Assert.Equal("Same", sector.Hit.Label);
            var sum = source.LeafIndices.Skip(sector.Hit.LeafStart).Take(sector.Hit.LeafCount)
                .Aggregate(BigInteger.Zero, (t, i) => t + Units(source.Nodes[i].Weight));
            Assert.Equal(sum, sector.Hit.Aggregate.Units);
        }
        AssertGeometry(frame);
    }

    [Fact]
    public void ParentConditionedCumulativeTicksPartitionEachAngleWithoutPadding()
    {
        var frame = Fixture().Query(); var a = frame.Sectors[0]; var one = frame.Sectors[1];
        var two = frame.Sectors[2]; var b = frame.Sectors[3]; var three = frame.Sectors[4]; var four = frame.Sectors[5];
        var aTicks = SunburstChartGeometry.AngleUnits * 3 / 10;
        Assert.Equal(0, a.StartTurn); Assert.Equal(aTicks * SunburstChartGeometry.AngleQuantum, a.EndTurn);
        Assert.Equal(a.EndTurn, b.StartTurn); Assert.Equal(1, b.EndTurn);
        Assert.Equal(a.StartTurn, one.StartTurn); Assert.Equal(a.EndTurn, two.EndTurn);
        Assert.Equal(aTicks / 3 * SunburstChartGeometry.AngleQuantum, one.EndTurn);
        Assert.Equal(one.EndTurn, two.StartTurn); Assert.Equal(b.StartTurn, three.StartTurn);
        Assert.Equal(three.EndTurn, four.StartTurn); Assert.Equal(b.EndTurn, four.EndTurn);
        Assert.Equal(.2, a.InnerRadius); Assert.Equal(.6, a.OuterRadius, 14);
        Assert.Equal(a.OuterRadius, one.InnerRadius); Assert.Equal(1, one.OuterRadius);
    }

    [Fact]
    public void EqualBandsEncodeDepthAndLeavesNeverFillUnusedOuterBands()
    {
        var frame = new SunburstChartSource([new("root", "Same", null, 1)]).Query(new(displayDepth: 4));
        Assert.Equal(.2, frame.Sectors[0].InnerRadius); Assert.Equal(.4, frame.Sectors[0].OuterRadius);
        Assert.Equal("root", SunburstChartGeometry.HitTest(frame, .3, 0)!.Value.Key);
        Assert.Null(SunburstChartGeometry.HitTest(frame, .5, 0));
        Assert.Equal(1, frame.Sectors[0].SweepTurn);
    }

    [Fact]
    public void CardinalAnglesAndSignedZeroSeamHaveOneHalfOpenOwner()
    {
        var frame = new SunburstChartSource(Enumerable.Range(0, 4).Select(i => new TreemapNode($"n{i}", "Same", null, 1)))
            .Query(new(displayDepth: 1));
        Assert.Equal("n0", SunburstChartGeometry.HitTest(frame, .5, 0)!.Value.Key);
        Assert.Equal("n0", SunburstChartGeometry.HitTest(frame, .5, -0d)!.Value.Key);
        Assert.Equal("n1", SunburstChartGeometry.HitTest(frame, 0, .5)!.Value.Key);
        Assert.Equal("n2", SunburstChartGeometry.HitTest(frame, -.5, 0)!.Value.Key);
        Assert.Equal("n2", SunburstChartGeometry.HitTest(frame, -.5, -0d)!.Value.Key);
        Assert.Equal("n3", SunburstChartGeometry.HitTest(frame, 0, -.5)!.Value.Key);
        Assert.Equal("n3", SunburstChartGeometry.HitTest(frame, .5, -double.Epsilon)!.Value.Key);
        Assert.Equal("n0", SunburstChartGeometry.HitTest(frame, .5, double.Epsilon)!.Value.Key);
    }

    [Fact]
    public void HoleOutsideAndSharedRadiusHaveExplicitHalfOpenOwnership()
    {
        var frame = new SunburstChartSource([new("root", "Same", null, 0), new("child", "Same", "root", 1)]).Query();
        var seam = frame.Sectors[0].OuterRadius;
        Assert.Null(SunburstChartGeometry.HitTest(frame, 0, 0));
        Assert.Null(SunburstChartGeometry.HitTest(frame, Math.BitDecrement(.2), 0));
        Assert.Equal("root", SunburstChartGeometry.HitTest(frame, .2, 0)!.Value.Key);
        Assert.Equal("root", SunburstChartGeometry.HitTest(frame, Math.BitDecrement(seam), 0)!.Value.Key);
        Assert.Equal("child", SunburstChartGeometry.HitTest(frame, seam, 0)!.Value.Key);
        Assert.Equal("child", SunburstChartGeometry.HitTest(frame, Math.BitDecrement(1d), 0)!.Value.Key);
        foreach (var point in new[] { (1d, 0d), (-1d, 0d), (0d, 1d), (0d, -1d), (.8, .8), (double.MaxValue, 0d) })
            Assert.Null(SunburstChartGeometry.HitTest(frame, point.Item1, point.Item2));
    }

    [Fact]
    public void EmptyAndAllZeroAreDistinctAndPreserveZeroKeyboardIdentity()
    {
        var empty = new SunburstChartSource([]).Query(new(geometryBudget: 0, workBudget: 0));
        Assert.True(empty.IsEmpty); Assert.False(empty.HasPositiveWeight); Assert.Empty(empty.Sectors);
        Assert.Equal(0, empty.RequiredWorkCount); Assert.Equal(ChartBudgetStatus.Satisfied, empty.BudgetStatus);
        var zero = new SunburstChartSource([new("root", "Same", null, 0), new("leaf", "Same", "root", -0d)]).Query();
        Assert.False(zero.IsEmpty); Assert.False(zero.HasPositiveWeight); Assert.Equal(BigInteger.Zero, zero.Total.Units);
        Assert.All(zero.Sectors, s => { Assert.Equal(SunburstSectorStatus.Zero, s.Status); Assert.Equal(0, s.SweepTurn); });
        Assert.Equal("leaf", SunburstChartGeometry.GetHit(zero, 1)!.Value.Key);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(zero.Sectors[1].Hit.Weight));
        Assert.Null(SunburstChartGeometry.HitTest(zero, .8, 0));
    }

    [Fact]
    public void LeadingTinyCollapsesAndTrailingTinyReceivesOnlyLastTickRemainder()
    {
        var tiny = new TreemapNode("tiny", "Same", null, double.Epsilon);
        var huge = new TreemapNode("huge", "Same", null, double.MaxValue);
        var leading = new SunburstChartSource([tiny, huge]).Query(new(displayDepth: 1));
        var trailing = new SunburstChartSource([huge, tiny]).Query(new(displayDepth: 1));
        Assert.Equal(SunburstSectorStatus.Collapsed, leading.Sectors[0].Status);
        Assert.Equal(0, leading.Sectors[0].SweepTurn); Assert.Same(tiny, SunburstChartGeometry.GetHit(leading, 0)!.Value.Node);
        Assert.Equal(SunburstSectorStatus.Visible, trailing.Sectors[1].Status);
        Assert.Equal(SunburstChartGeometry.AngleQuantum, trailing.Sectors[1].SweepTurn);
        Assert.Equal(1, trailing.Sectors[1].EndTurn); Assert.Equal(trailing.Sectors[0].EndTurn, trailing.Sectors[1].StartTurn);
        Assert.True(leading.Total.IsOverflow); Assert.Equal(Units(double.MaxValue) + 1, leading.Total.Units);
        Assert.Equal("huge", SunburstChartGeometry.HitTest(leading, .5, 0)!.Value.Key);
        Assert.Equal("tiny", SunburstChartGeometry.HitTest(trailing, .5, -double.Epsilon)!.Value.Key);
    }

    [Fact]
    public void CollapsedAncestorPropagatesAndDrillRestoresExactConditionalAngles()
    {
        var source = new SunburstChartSource([new("small", "Same", null, 0), new("huge", "Same", null, double.MaxValue),
            new("a", "Same", "small", double.Epsilon), new("b", "Same", "small", double.Epsilon)]);
        Assert.All(source.Query().Sectors.Where(s => s.Hit.Key != "huge"), s => Assert.Equal(SunburstSectorStatus.Collapsed, s.Status));
        var focused = source.Query(focusKey: "small");
        Assert.Equal("small", focused.FocusKey); Assert.Equal(source.GetAggregate("small"), focused.Total);
        Assert.Equal(new[] { "a", "b" }, focused.Sectors.Select(s => s.Hit.Key));
        Assert.All(focused.Sectors, s => { Assert.Equal(SunburstSectorStatus.Visible, s.Status); Assert.Equal(.5, s.SweepTurn); Assert.Equal(0, s.Depth); });
        Assert.Equal(0, focused.Sectors[0].Hit.LeafStart); Assert.Equal(1, focused.Sectors[1].Hit.LeafStart);
        Assert.Equal(source.Query().Sectors.ToArray(), source.Query(focusKey: source.GetParentKey("small")).Sectors.ToArray());
    }

    [Fact]
    public void OverflowAndSubnormalEqualWeightsHaveIdenticalFinitePartitions()
    {
        double[]? expected = null;
        foreach (var weight in new[] { double.Epsilon, 1, double.MaxValue })
        {
            var frame = new SunburstChartSource(Enumerable.Range(0, 3).Select(i => new TreemapNode($"n{i}", "Same", null, weight))).Query();
            Assert.Equal(Units(weight) * 3, frame.Total.Units);
            Assert.Equal(weight == double.MaxValue, frame.Total.IsOverflow);
            var boundaries = frame.Sectors.SelectMany(s => new[] { s.StartTurn, s.EndTurn }).ToArray();
            if (expected is not null) Assert.Equal(expected, boundaries);
            expected = boundaries; AssertGeometry(frame);
        }
    }

    [Fact]
    public void GeometryAndWorkThresholdsRefuseAtomicallyWithoutLosingTotals()
    {
        var source = Fixture(); var expected = source.Query();
        Assert.Equal(6, expected.RequiredGeometryCount); Assert.Equal(36, expected.RequiredWorkCount);
        foreach (var options in new[] { new SunburstChartOptions(geometryBudget: 5), new SunburstChartOptions(workBudget: 35), new SunburstChartOptions(geometryBudget: 0, workBudget: 0) })
        {
            var frame = source.Query(options); Assert.Equal(ChartBudgetStatus.InsufficientBudget, frame.BudgetStatus);
            Assert.Empty(frame.Sectors); Assert.Equal(expected.Total, frame.Total); Assert.Equal(6, frame.RequiredGeometryCount);
            Assert.Equal(36, frame.RequiredWorkCount); Assert.False(frame.IsEmpty); Assert.True(frame.HasPositiveWeight);
            Assert.Null(SunburstChartGeometry.GetHit(frame, 0)); Assert.Null(SunburstChartGeometry.HitTest(frame, .5, 0));
        }
        Assert.Equal(expected.Sectors.ToArray(), source.Query(new(geometryBudget: 6, workBudget: 36)).Sectors.ToArray());
        Assert.Equal(ChartBudgetStatus.Satisfied, source.Query(new(geometryBudget: 2, workBudget: 28), "a").BudgetStatus);
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, source.Query(new(workBudget: 27), "a").BudgetStatus);
    }

    [Fact]
    public void MaximumFlatForestHasCompleteDeterministicTickPartition()
    {
        var source = new SunburstChartSource(Enumerable.Range(0, 256).Select(i => new TreemapNode($"n{i}", "Same", null, 1)));
        var frame = source.Query(new(displayDepth: 1));
        Assert.Equal(256, frame.Sectors.Length); Assert.Equal(1536, frame.RequiredWorkCount);
        Assert.Equal(frame.Sectors.ToArray(), source.Query(new(displayDepth: 1)).Sectors.ToArray());
        for (var i = 0; i < 256; i++)
        {
            var sector = frame.Sectors[i]; Assert.Equal(i / 256d, sector.StartTurn); Assert.Equal((i + 1) / 256d, sector.EndTurn);
            var angle = (sector.StartTurn + sector.EndTurn) * .5 * Math.Tau;
            Assert.Equal($"n{i}", SunburstChartGeometry.HitTest(frame, .7 * Math.Cos(angle), .7 * Math.Sin(angle))!.Value.Key);
            Assert.Equal(i, sector.Hit.LeafStart);
        }
        AssertGeometry(frame);
    }

    [Fact]
    public void SixteenDepthsAreBoundedAndSeventeenRejected()
    {
        var source = new SunburstChartSource(Chain(16)); var frame = source.Query(new(displayDepth: 16));
        Assert.Equal(16, source.DepthCount); Assert.Equal(16, frame.Sectors.Length);
        for (var i = 0; i < 16; i++)
        {
            var sector = frame.Sectors[i]; Assert.Equal(i, sector.Depth); Assert.Equal(1, sector.SweepTurn);
            Assert.Equal($"n{i}", SunburstChartGeometry.HitTest(frame, (sector.InnerRadius + sector.OuterRadius) / 2, 0)!.Value.Key);
            if (i > 0) Assert.Equal(frame.Sectors[i - 1].OuterRadius, sector.InnerRadius);
        }
        Assert.Equal(1, frame.Sectors[^1].OuterRadius); Assert.Throws<ArgumentException>(() => new SunburstChartSource(Chain(17)));
    }

    [Fact]
    public void StrictHierarchyAdmissionRejectsDuplicatesOrphansSelfAndDisconnectedCycles()
    {
        Assert.Throws<ArgumentException>(() => new SunburstChartSource([new("a", "Same", null, 1), new("a", "Same", null, 1)]));
        Assert.Throws<ArgumentException>(() => new SunburstChartSource([new("a", "Same", "missing", 0)]));
        Assert.Throws<ArgumentException>(() => new SunburstChartSource([new("a", "Same", "a", 0)]));
        Assert.Throws<ArgumentException>(() => new SunburstChartSource([new("root", "Same", null, 1), new("a", "Same", "b", 0), new("b", "Same", "a", 0)]));
        Assert.Throws<ArgumentException>(() => new SunburstChartSource([new("a", "Same", null, 1), new("b", "Same", "a", 1)]));
        var ordinal = new SunburstChartSource([new("a", "Same", null, 0), new("A", "Same", "a", 1)]);
        Assert.Equal(0, ordinal.GetNodeIndex("a")); Assert.Equal(1, ordinal.GetNodeIndex("A"));
    }

    [Fact]
    public void StrictWeightTextAndVersionAdmissionPreservesValidatedForestRules()
    {
        foreach (var weight in new double?[] { null, -1, -double.Epsilon, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => new SunburstChartSource([new("a", "Same", null, weight)]));
        foreach (var text in new[] { "", " ", "\ud800", new string('x', 257) })
            Assert.Throws<ArgumentException>(() => new SunburstChartSource([new(text, "Same", null, 1)]));
        Assert.Throws<ArgumentException>(() => new SunburstChartSource([new("a", new string('x', 257), null, 1)]));
        Assert.Throws<ArgumentNullException>(() => new SunburstChartSource(null!));
        Assert.Throws<ArgumentNullException>(() => new SunburstChartSource([null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SunburstChartSource([], -1));
        Assert.Single(new SunburstChartSource([new(new string('x', 256), "🙂中文", null, 1)]).Nodes);
    }

    [Fact]
    public void EndlessEnumeratorIsDisposedAfter257Advances()
    {
        var advances = 0; var disposed = false;
        IEnumerable<TreemapNode> Endless()
        {
            try { while (true) { advances++; yield return new($"n{advances}", "Same", null, 1); } }
            finally { disposed = true; }
        }
        Assert.Throws<ArgumentException>(() => new SunburstChartSource(Endless())); Assert.Equal(257, advances); Assert.True(disposed);
    }

    [Fact]
    public void CallerMutationCannotChangeFrozenSourcesOrSnapshots()
    {
        var nodes = new[] { new TreemapNode("a", "Same", null, 2) }; var original = nodes[0];
        var source = new SunburstChartSource(nodes, 12); var frame = source.Query(); nodes[0] = new("b", "Other", null, 40);
        Assert.Same(original, source.Nodes[0]); Assert.Same(original, frame.Sectors[0].Hit.Node);
        Assert.Equal(12, frame.DatasetVersion); Assert.Equal(2, frame.Total.RoundedValue);
        Assert.Equal(frame.Sectors.ToArray(), source.Query().Sectors.ToArray());
    }

    [Fact]
    public void InvalidOptionsFocusKeysAndCoordinateArgumentsFailExplicitly()
    {
        var source = Fixture(); var frame = source.Query();
        foreach (var depth in new[] { int.MinValue, 0, 17, int.MaxValue }) Assert.Throws<ArgumentOutOfRangeException>(() => new SunburstChartOptions(displayDepth: depth));
        foreach (var budget in new[] { -1, 257, int.MaxValue }) Assert.Throws<ArgumentOutOfRangeException>(() => new SunburstChartOptions(geometryBudget: budget));
        foreach (var budget in new[] { -1, 1537, int.MaxValue }) Assert.Throws<ArgumentOutOfRangeException>(() => new SunburstChartOptions(workBudget: budget));
        Assert.Throws<ArgumentException>(() => source.Query(focusKey: "one")); Assert.Throws<ArgumentException>(() => source.Query(focusKey: "unknown"));
        Assert.Throws<ArgumentNullException>(() => source.GetNodeIndex(null!)); Assert.Throws<ArgumentException>(() => source.GetChildren("unknown"));
        Assert.Throws<ArgumentException>(() => source.GetParentKey("unknown")); Assert.Throws<ArgumentException>(() => source.GetAggregate("unknown"));
        Assert.Throws<ArgumentNullException>(() => SunburstChartGeometry.HitTest(null!, 0, 0));
        Assert.Throws<ArgumentNullException>(() => SunburstChartGeometry.GetHit(null!, 0));
        Assert.Null(SunburstChartGeometry.GetHit(frame, -1)); Assert.Null(SunburstChartGeometry.GetHit(frame, frame.Sectors.Length));
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SunburstChartGeometry.HitTest(frame, value, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => SunburstChartGeometry.HitTest(frame, 0, value));
        }
    }

    private static SunburstChartSource Fixture() => new([new("a", "Same", null, 0), new("one", "Same", "a", 1),
        new("b", "Same", null, 0), new("two", "Same", "a", 2), new("three", "Same", "b", 3), new("four", "Same", "b", 4)], 97);
    private static TreemapNode[] Chain(int length) => Enumerable.Range(0, length).Select(i => new TreemapNode($"n{i}", "Same",
        i == 0 ? null : $"n{i - 1}", i == length - 1 ? 1 : 0)).ToArray();
    private static BigInteger Units(double value)
    {
        var bits = (ulong)BitConverter.DoubleToInt64Bits(value); var exponent = (int)((bits >> 52) & 0x7ff);
        var mantissa = bits & 0x000f_ffff_ffff_ffffUL;
        return exponent == 0 ? new BigInteger(mantissa) : new BigInteger(mantissa | (1UL << 52)) << (exponent - 1);
    }
    private static void AssertGeometry(SunburstChartSnapshot frame)
    {
        foreach (var sector in frame.Sectors)
        {
            Assert.InRange(sector.StartTurn, 0, 1); Assert.InRange(sector.EndTurn, sector.StartTurn, 1);
            Assert.InRange(sector.InnerRadius, .2, 1); Assert.InRange(sector.OuterRadius, sector.InnerRadius, 1);
            foreach (var turn in new[] { sector.StartTurn, sector.EndTurn })
                Assert.Equal(Math.Truncate(turn * SunburstChartGeometry.AngleUnits), turn * SunburstChartGeometry.AngleUnits);
            Assert.Equal(sector.Status == SunburstSectorStatus.Visible, sector.SweepTurn > 0);
            Assert.True(sector.OuterRadius > sector.InnerRadius);
        }
    }
}
