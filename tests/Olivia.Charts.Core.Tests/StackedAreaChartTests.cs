using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class StackedAreaChartTests
{
    [Fact]
    public void OrdinarySumsAndNormalizedPercentagesUseSameOriginals()
    {
        var source = Example();
        var ordinary = source.Query();
        var percent = source.Query(new(StackedAreaChartMode.Normalized100));
        Assert.Equal(StackedAreaChartStatus.Ready, ordinary.Status);
        Assert.Equal(new HistoryValueDomain(0, 5), ordinary.YDomain);
        Assert.Equal(5, Value(ordinary, "B", 0).StackTop);
        Assert.Equal(4, Value(ordinary, "B", 1).StackTop);
        Assert.Equal(2, Value(ordinary, "B", 0).StackBase);
        Assert.Equal(3, Value(ordinary, "B", 1).StackBase);
        Assert.Equal(.4, Value(percent, "A", 0).Proportion, 12);
        Assert.Equal(.75, Value(percent, "A", 1).Proportion, 12);
        Assert.Equal(40, Value(percent, "A", 0).Percentage, 12);
        Assert.Equal(1, Value(percent, "B", 0).StackTop);
        Assert.Equal(1, Value(percent, "B", 1).StackTop);
        Assert.Equal(new HistoryValueDomain(0, 1), percent.YDomain);
        Assert.Equal(ordinary.Contributions.Select(v => v.Hit), percent.Contributions.Select(v => v.Hit));
        Assert.Equal(new double?[] { 2, 3 }, source.Series[0].Samples.Select(p => p.Y));
        Assert.All(percent.Contributions, value => Assert.True(source.TryResolve(value.Hit)));
        AssertFinite(ordinary); AssertFinite(percent);
    }

    [Fact]
    public void VisibilityRenormalizesButRetainedOriginalIdentitySurvives()
    {
        var source = Example();
        var first = source.Query();
        var selected = first.Points[0].Hit;
        var visible = new List<string> { "B" };
        var options = new StackedAreaChartOptions(StackedAreaChartMode.Normalized100, visibleSeriesIds: visible);
        visible.Clear(); // Options already copied the caller's sequence.
        var hidden = source.Query(options);
        Assert.Equal(2, hidden.Points.Length);
        Assert.All(hidden.Contributions, value =>
        {
            Assert.Equal("B", value.Hit.SeriesId);
            Assert.Equal(0, value.StackBase);
            Assert.Equal(1, value.StackTop);
            Assert.Equal(1, value.Proportion);
        });
        Assert.True(source.TryResolve(selected));
        Assert.False(Example().TryResolve(selected));
        Assert.False(source.TryResolve(selected with { Sample = new(selected.Key, selected.X, 999) }));
        Assert.Equal(selected, source.Query().Points[0].Hit);
        var none = source.Query(new(visibleSeriesIds: Array.Empty<string>()));
        Assert.Empty(none.Points); Assert.Empty(none.Polygons);
        Assert.Equal(first.XDomain, none.XDomain);
        Assert.Null(none.YDomain);
    }

    [Fact]
    public void StaggeredBirthUsesActualXAndNeverInventsZeroObservations()
    {
        var source = new StackedAreaChartSource([
            new("A", "A", [new(10, 2015, 2), new(11, 2016, 3), new(12, 2017, 4)], 17),
            new("B", "B", [new(90, 2016, 1), new(91, 2017, 4)], 29)
        ], 31);
        var frame = source.Query(new(StackedAreaChartMode.Normalized100));
        Assert.Equal(5, frame.Points.Length);
        Assert.Equal(new[] { 2015d, 2016d, 2017d }, source.Coordinates);
        Assert.Equal(1, Value(frame, "A", 2015).Proportion);
        Assert.Equal(.75, Value(frame, "A", 2016).Proportion, 12);
        Assert.Equal(.25, Value(frame, "B", 2016).Proportion, 12);
        Assert.DoesNotContain(frame.Points, p => p.Hit.SeriesId == "B" && p.Hit.X == 2015);
        Assert.DoesNotContain(frame.Contributions, p => p.Hit.Key == 0);
        var band = Assert.Single(frame.Polygons.Where(p => p.SeriesIndex == 1));
        Assert.Equal(90, band.StartHit.Key); Assert.Equal(91, band.EndHit.Key);
        Assert.Equal(2016, band.StartHit.X);
        Assert.Same(source, band.StartHit.Source);
        Assert.Equal(29, band.StartHit.SeriesSourceVersion);
        Assert.Equal(31, band.StartHit.DatasetVersion);
    }

    [Fact]
    public void ZeroDenominatorStaysFiniteAndTrueZeroRemainsSelectable()
    {
        var source = new StackedAreaChartSource([
            new("A", "A", [new(11, 0, 0), new(12, 1, 0)]),
            new("B", "B", [new(21, 0, 0), new(22, 1, 0)])
        ]);
        var frame = source.Query(new(StackedAreaChartMode.Normalized100));
        Assert.Equal(StackedAreaChartStatus.Ready, frame.Status);
        Assert.Equal(4, frame.Points.Length);
        Assert.Empty(frame.Polygons);
        Assert.All(frame.Contributions, value =>
        {
            Assert.Equal(0, value.StackBase); Assert.Equal(0, value.StackTop);
            Assert.Equal(0, value.Proportion); Assert.Equal(0, value.Contribution);
        });
        var zero = frame.Points[0];
        Assert.Equal(zero.Hit, StackedAreaChartGeometry.HitTest(frame, zero.X, zero.Top, 100, 100, 0));
        AssertFinite(frame);
    }

    [Fact]
    public void FixedWindowClipsBandsWithoutSyntheticSelectableHits()
    {
        var source = Example();
        var frame = source.Query(new(fixedXDomain: new(.25, .75), fixedYDomain: new(1, 4)));
        Assert.Equal(StackedAreaChartStatus.Ready, frame.Status);
        Assert.Equal(new HistoryValueDomain(.25, .75), frame.XDomain);
        Assert.Equal(new HistoryValueDomain(1, 4), frame.YDomain);
        Assert.Empty(frame.Points);
        Assert.NotEmpty(frame.Polygons);
        Assert.Equal(4, frame.Contributions.Length);
        Assert.Null(StackedAreaChartGeometry.HitTest(frame, .5, .5, 400, 300));
        Assert.All(frame.Polygons, polygon =>
        {
            Assert.True(source.TryResolve(polygon.StartHit));
            Assert.True(source.TryResolve(polygon.EndHit));
        });
        AssertFinite(frame);
    }

    [Fact]
    public void SignedOrdinarySeriesStackSeparatelyAndUnsupportedSignsRejectAtomically()
    {
        var source = new StackedAreaChartSource([
            new("P", "P", [new(1, 0, 2), new(2, 1, 3)]),
            new("N", "N", [new(3, 0, -4), new(4, 1, -1)]),
            new("M", "M", [new(5, 0, -2), new(6, 1, -3)])
        ]);
        var before = source.Query();
        Assert.Equal(-6, Value(before, "M", 0).StackTop);
        Assert.Equal(-4, Value(before, "M", 0).StackBase);
        Assert.Equal(2, Value(before, "P", 0).StackTop);
        Assert.Equal(new HistoryValueDomain(-6, 3), before.YDomain);
        Assert.Throws<ArgumentException>(() => source.Query(new(StackedAreaChartMode.Normalized100)));
        Assert.Equal(StackedAreaChartStatus.Ready,
            source.Query(new(StackedAreaChartMode.Normalized100, visibleSeriesIds: ["P"])).Status);
        Assert.Throws<ArgumentException>(() => new StackedAreaChartSource([
            new("mixed", "mixed", [new(1, 0, -1), new(2, 1, 1)])]));
        Assert.Equal(before.Contributions.ToArray(), source.Query().Contributions.ToArray());
    }

    [Fact]
    public void InteriorMissingAndExplicitGapRejectInsteadOfBecomingZero()
    {
        Assert.Throws<ArgumentException>(() => new StackedAreaChartSource([
            new("A", "A", [new(1, 0, 1), new(2, 2, 1)]),
            new("B", "B", [new(3, 0, 1), new(4, 1, 1), new(5, 2, 1)])]));
        Assert.Throws<ArgumentException>(() => new StackedAreaChartSource([
            new("A", "A", [new(1, 0, 1), new(2, 1, null)])]));
        Assert.Throws<ArgumentException>(() => new StackedAreaChartSource([
            new("A", "A", [new(1, null, 1)])]));
        Assert.Throws<ArgumentException>(() => new StackedAreaChartSource([
            new("A", "A", [new(1, 0, double.NaN)])]));
    }

    [Fact]
    public void WorkAndGeometryBudgetsRefuseWholeFrameWithoutPartialResults()
    {
        var source = Example();
        var refused = source.Query(new(workBudget: source.RequiredWork - 1));
        Assert.Equal(StackedAreaChartStatus.WorkBudgetExceeded, refused.Status);
        Assert.False(refused.GeometryCountsKnown);
        Assert.Equal(-1, refused.RequiredPoints);
        Assert.Empty(refused.Points); Assert.Empty(refused.Polygons); Assert.Empty(refused.Contributions);
        var admitted = source.Query(new(workBudget: source.RequiredWork));
        Assert.Equal(StackedAreaChartStatus.Ready, admitted.Status);
        Assert.Equal(4, admitted.RequiredPoints);
        var geometry = source.Query(new(pointBudget: 3));
        Assert.Equal(StackedAreaChartStatus.GeometryBudgetExceeded, geometry.Status);
        Assert.True(geometry.GeometryCountsKnown);
        Assert.Equal(4, geometry.RequiredPoints);
        Assert.Empty(geometry.Points); Assert.Empty(geometry.Polygons);
        Assert.Empty(geometry.Segments); Assert.Empty(geometry.Contributions);
        Assert.Equal(StackedAreaChartStatus.GeometryBudgetExceeded, source.Query(new(polygonBudget: 0)).Status);
        Assert.Equal(StackedAreaChartStatus.GeometryBudgetExceeded, source.Query(new(segmentBudget: 0)).Status);
    }

    [Fact]
    public void OverflowRefusesOrdinaryWhileScaledNormalizationRemainsFinite()
    {
        var source = new StackedAreaChartSource([
            new("A", "A", [new(1, 0, double.MaxValue), new(2, 1, double.MaxValue)]),
            new("B", "B", [new(3, 0, double.MaxValue), new(4, 1, double.MaxValue)])]);
        var refused = source.Query();
        Assert.Equal(StackedAreaChartStatus.NumericOverflow, refused.Status);
        Assert.Empty(refused.Points); Assert.Empty(refused.Polygons); Assert.Empty(refused.Contributions);
        var normalized = source.Query(new(StackedAreaChartMode.Normalized100));
        Assert.Equal(StackedAreaChartStatus.Ready, normalized.Status);
        Assert.Equal(.5, Value(normalized, "A", 0).Proportion);
        Assert.Equal(1, Value(normalized, "B", 1).StackTop);
        Assert.All(source.Series.SelectMany(s => s.Samples), sample => Assert.Equal(double.MaxValue, sample.Y));
        AssertFinite(normalized);
    }

    [Fact]
    public void InvalidOptionsAndSourceLimitsDoNotMutateAcceptedSnapshot()
    {
        var source = Example();
        var before = source.Query();
        Assert.Throws<ArgumentOutOfRangeException>(() => new StackedAreaChartOptions((StackedAreaChartMode)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StackedAreaChartOptions(pointBudget: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StackedAreaChartOptions(workBudget: StackedAreaChartSource.MaximumWork + 1));
        Assert.Throws<ArgumentException>(() => new StackedAreaChartOptions(visibleSeriesIds: ["A", "A"]));
        Assert.Throws<ArgumentException>(() => source.Query(new(visibleSeriesIds: ["unknown"])));
        Assert.Throws<ArgumentException>(() => new StackedAreaChartSource([
            new("A", "A", Enumerable.Range(0, 257).Select(x => new AreaSample(x, x, 1)))]));
        Assert.Throws<ArgumentException>(() => new StackedAreaChartSource(
            Enumerable.Range(0, 9).Select(x => new AreaSeries(x.ToString(), "Series", [new(x, 0, 1)]))));
        Assert.Same(source, before.Source);
        Assert.Equal(4, before.Points.Length);
        Assert.Equal(before.Points.ToArray(), source.Query().Points.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => StackedAreaChartGeometry.HitTest(before, double.NaN, 0, 100, 100));
    }

    private static StackedAreaChartSource Example() => new([
        new("A", "A", [new(101, 0, 2), new(102, 1, 3)], 5),
        new("B", "B", [new(201, 0, 3), new(202, 1, 1)], 7)
    ], 11);

    private static StackedAreaChartContribution Value(StackedAreaChartSnapshot frame, string series, double x) =>
        Assert.Single(frame.Contributions.Where(value => value.Hit.SeriesId == series && value.Hit.X == x));

    private static void AssertFinite(StackedAreaChartSnapshot frame)
    {
        Assert.All(frame.Points, point =>
        {
            Assert.InRange(point.X, 0, 1); Assert.InRange(point.Top, 0, 1); Assert.InRange(point.Bottom, 0, 1);
            Assert.True(double.IsFinite(point.StackBase)); Assert.True(double.IsFinite(point.StackTop));
            Assert.True(double.IsFinite(point.Contribution)); Assert.True(double.IsFinite(point.Proportion));
        });
        Assert.All(frame.Polygons.SelectMany(p => p.Vertices), vertex =>
        { Assert.InRange(vertex.X, 0, 1); Assert.InRange(vertex.Y, 0, 1); });
        Assert.All(frame.Segments, segment =>
        {
            Assert.InRange(segment.Start.X, 0, 1); Assert.InRange(segment.Start.Y, 0, 1);
            Assert.InRange(segment.End.X, 0, 1); Assert.InRange(segment.End.Y, 0, 1);
        });
    }
}
