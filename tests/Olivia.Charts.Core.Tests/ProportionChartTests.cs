using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class ProportionChartTests
{
    [Fact]
    public void CopiesOriginalIdentityOrderLabelsNullableWeightsAndVersion()
    {
        var input = new[] { new ProportionCategory("b", "Same", 3), new ProportionCategory("a", "Same", 1) };
        var source = new ProportionChartSource(input, 91);
        input[0] = new("changed", "Changed", 9);
        var snapshot = source.Query();
        Assert.Equal(new[] { "b", "a" }, source.Categories.Select(c => c.Key));
        Assert.All(source.Categories, c => Assert.Equal("Same", c.Label));
        Assert.Equal(91, snapshot.DatasetVersion);
        Assert.Equal(3, snapshot.Slices[0].Hit.Weight);
        Assert.Equal(91, snapshot.Slices[0].Hit.DatasetVersion);
        Assert.Same(source, snapshot.Source);
        Assert.Equal(.75, snapshot.Slices[0].Fraction);
        Assert.Equal(0, snapshot.Slices[0].StartAngle);
        Assert.Equal(Math.Tau * .75, snapshot.Slices[0].EndAngle);
        Assert.Equal(snapshot.Slices[0].EndAngle, snapshot.Slices[1].StartAngle);
        Assert.Equal(Math.Tau, snapshot.Slices[1].EndAngle);
        Assert.Equal(4, snapshot.TotalWeight);
        Assert.False(snapshot.TotalOverflow);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    public void InvalidWeightsAreRejectedExplicitly(double? value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ProportionCategory("x", "X", value));

    [Fact]
    public void EmptyAndAllZeroRemainEmptyWithOriginalEntriesAvailable()
    {
        var empty = new ProportionChartSource([]).Query();
        Assert.True(empty.IsEmpty); Assert.Empty(empty.Slices); Assert.Equal(0, empty.TotalWeight);
        var zero = Source([0, -0d]).Query();
        Assert.True(zero.IsEmpty); Assert.Equal(0, zero.TotalScale); Assert.Equal(0, zero.ScaledTotal);
        Assert.All(zero.Slices, s => { Assert.Equal(ProportionSliceStatus.Zero, s.Status); Assert.Equal(0, s.SweepAngle); });
        Assert.Null(ProportionChartGeometry.HitTest(zero, 0, 0));
        Assert.Equal("1", ProportionChartGeometry.GetHit(zero, 1)!.Value.Key);
    }

    [Fact]
    public void OverflowUsesFiniteScaledTotalAndDoesNotBreakAngles()
    {
        var snapshot = Source([double.MaxValue, double.MaxValue, double.MaxValue]).Query();
        Assert.True(snapshot.TotalOverflow); Assert.Equal(double.PositiveInfinity, snapshot.TotalWeight);
        Assert.Equal(double.MaxValue, snapshot.TotalScale); Assert.Equal(3, snapshot.ScaledTotal);
        Assert.All(snapshot.Slices, s => Assert.Equal(1d / 3, s.Fraction));
        Assert.Equal(Math.Tau, snapshot.Slices[^1].EndAngle);
        AssertPartition(snapshot);
    }

    [Fact]
    public void SubnormalWeightsAreNotMistakenForEmptyAndCollapsedWeightsRetainBits()
    {
        var tiny = Source([double.Epsilon, double.Epsilon, double.Epsilon]).Query();
        Assert.False(tiny.IsEmpty); Assert.Equal(3, tiny.ScaledTotal);
        Assert.All(tiny.Slices, s => Assert.Equal(ProportionSliceStatus.Visible, s.Status));
        var extreme = Source([double.Epsilon, double.MaxValue, 1, 0, double.Epsilon]).Query();
        Assert.Equal(ProportionSliceStatus.Collapsed, extreme.Slices[0].Status);
        Assert.Equal(ProportionSliceStatus.Visible, extreme.Slices[1].Status);
        Assert.Equal(ProportionSliceStatus.Collapsed, extreme.Slices[2].Status);
        Assert.Equal(ProportionSliceStatus.Zero, extreme.Slices[3].Status);
        Assert.Equal(ProportionSliceStatus.Collapsed, extreme.Slices[4].Status);
        Assert.Equal(double.Epsilon, ProportionChartGeometry.GetHit(extreme, 4)!.Value.Weight);
        AssertPartition(extreme);
    }

    [Fact]
    public void HalfOpenCardinalSeamsAndPieCenterHaveDeterministicOwners()
    {
        var snapshot = Source([1, 1, 1, 1]).Query();
        Assert.Equal("0", ProportionChartGeometry.HitTest(snapshot, 1, 0)!.Value.Key);
        Assert.Equal("1", ProportionChartGeometry.HitTest(snapshot, 0, 1)!.Value.Key);
        Assert.Equal("2", ProportionChartGeometry.HitTest(snapshot, -1, 0)!.Value.Key);
        Assert.Equal("3", ProportionChartGeometry.HitTest(snapshot, 0, -1)!.Value.Key);
        Assert.Equal("0", ProportionChartGeometry.HitTest(snapshot, 0, 0)!.Value.Key);
        Assert.Equal("3", ProportionChartGeometry.HitTest(snapshot, .5, -double.Epsilon)!.Value.Key);
        Assert.Null(ProportionChartGeometry.HitTest(snapshot, 1, 1));
        Assert.Null(ProportionChartGeometry.HitTest(snapshot, double.MaxValue, 0));
        Assert.Null(ProportionChartGeometry.GetHit(snapshot, -1));
        Assert.Null(ProportionChartGeometry.GetHit(snapshot, 4));
    }

    [Fact]
    public void DonutHoleAndBoundaryAreExcludedWithoutUnderflowLeaks()
    {
        var donut = Source([1]).Query(new(innerRadius: .5));
        Assert.Null(ProportionChartGeometry.HitTest(donut, 0, 0));
        Assert.Null(ProportionChartGeometry.HitTest(donut, .5, 0));
        Assert.NotNull(ProportionChartGeometry.HitTest(donut, Math.BitIncrement(.5), 0));
        Assert.NotNull(ProportionChartGeometry.HitTest(donut, 1, 0));
        var tinyHole = Source([1]).Query(new(innerRadius: double.Epsilon));
        Assert.Null(ProportionChartGeometry.HitTest(tinyHole, double.Epsilon, 0));
        Assert.NotNull(ProportionChartGeometry.HitTest(tinyHole, 2 * double.Epsilon, 0));
        Assert.NotNull(ProportionChartGeometry.HitTest(tinyHole, double.Epsilon, double.Epsilon));
    }

    [Fact]
    public void WholeFrameBudgetRefusalIsExplicitAndRecoverable()
    {
        var source = Source([1, 0, double.Epsilon]);
        var refused = source.Query(new(geometryBudget: 2));
        Assert.Equal(3, refused.RequiredSliceCount);
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, refused.BudgetStatus);
        Assert.Empty(refused.Slices); Assert.False(refused.IsEmpty);
        Assert.Null(ProportionChartGeometry.HitTest(refused, .5, 0));
        Assert.Null(ProportionChartGeometry.GetHit(refused, 0));
        Assert.Equal(3, source.Query(new(geometryBudget: 3)).Slices.Length);
    }

    [Fact]
    public void AdmissionIsBoundedDisposesIteratorAndRejectsDuplicateKeys()
    {
        var advances = 0; var disposed = false;
        IEnumerable<ProportionCategory> Endless()
        {
            try { while (true) { advances++; yield return new(advances.ToString(), "Same", 1); } }
            finally { disposed = true; }
        }
        Assert.Throws<ArgumentException>(() => new ProportionChartSource(Endless()));
        Assert.Equal(257, advances); Assert.True(disposed);
        Assert.Throws<ArgumentException>(() => new ProportionChartSource([new("x", "A", 1), new("x", "B", 2)]));
        Assert.Throws<ArgumentNullException>(() => new ProportionChartSource(null!));
        Assert.Throws<ArgumentNullException>(() => new ProportionChartSource([null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProportionChartSource([], -1));
        var maximum = Source(Enumerable.Repeat<double?>(1, 256).ToArray()).Query();
        Assert.Equal(256, maximum.Slices.Length); AssertPartition(maximum);
    }

    [Fact]
    public void InvalidTextOptionsAndCoordinatesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new ProportionCategory(null!, "", 0));
        Assert.Throws<ArgumentException>(() => new ProportionCategory(" ", "", 0));
        Assert.Throws<ArgumentException>(() => new ProportionCategory(new string('x', 257), "", 0));
        Assert.Throws<ArgumentException>(() => new ProportionCategory("x", new string('x', 257), 0));
        Assert.Throws<ArgumentException>(() => new ProportionCategory("x", "\uD800", 0));
        foreach (var radius in new[] { -1d, 1, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => new ProportionChartOptions(radius));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProportionChartOptions(geometryBudget: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProportionChartOptions(geometryBudget: 257));
        var snapshot = Source([1]).Query();
        Assert.Throws<ArgumentOutOfRangeException>(() => ProportionChartGeometry.HitTest(snapshot, double.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProportionChartGeometry.HitTest(snapshot, 0, double.PositiveInfinity));
    }

    [Fact]
    public void SkewedDeterministicDatasetsAlwaysCloseAndKeepFiniteMonotonePartitions()
    {
        var random = new Random(7349);
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var values = Enumerable.Range(0, 253).Select(_ => (double?)Math.ScaleB(random.NextDouble(), random.Next(-1074, 1024))).Concat(new double?[] { double.MaxValue, 0, double.Epsilon }).ToArray();
            AssertPartition(Source(values).Query());
        }
    }

    private static void AssertPartition(ProportionChartSnapshot snapshot)
    {
        var previous = 0d;
        foreach (var slice in snapshot.Slices)
        {
            Assert.Equal(previous, slice.StartAngle);
            Assert.InRange(slice.EndAngle, slice.StartAngle, Math.Tau);
            Assert.InRange(slice.Fraction, 0, 1);
            Assert.True(double.IsFinite(slice.SweepAngle));
            if (slice.Status != ProportionSliceStatus.Visible) Assert.Equal(0, slice.SweepAngle);
            previous = slice.EndAngle;
        }
        if (!snapshot.IsEmpty) Assert.Equal(Math.Tau, previous);
    }
    private static ProportionChartSource Source(double?[] values) => new(values.Select((w, i) => new ProportionCategory(i.ToString(), "Same", w)));
}
