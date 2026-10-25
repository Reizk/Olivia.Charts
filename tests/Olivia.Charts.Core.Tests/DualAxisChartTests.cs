using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class DualAxisChartTests
{
    [Fact]
    public void DatasetCopiesDefinitionsAndKeepsIndependentIdentityVersionsAndExtentUnion()
    {
        var axes = new[] { Axis("temperature", HistoryAxisSide.Left), Axis("pressure", HistoryAxisSide.Right) };
        var first = new HistorySeries([new HistorySample(7, 5, 2), new HistorySample(9, 15, null)], 12);
        var second = new HistorySeries([new HistorySample(7, 100, 42)], 3);
        var definitions = new[] { new HistorySeriesDefinition("a", "A", "temperature", first),
            new HistorySeriesDefinition("b", "B", "pressure", second) };
        var source = new DualAxisHistorySource(axes, definitions, 88);
        axes[0] = Axis("mutated", HistoryAxisSide.Left);
        definitions[0] = new HistorySeriesDefinition("changed", "Changed", "mutated", new HistorySeries([]));
        Assert.Equal("temperature", source.Axes[0].Id);
        Assert.Equal("a", source.Series[0].Id);
        Assert.Same(first, source.Series[0].Source);
        Assert.Equal(3L, source.Count);
        Assert.Equal(new TimeWindow(5, 101), source.Extent);
        Assert.Equal(88, source.DatasetVersion);
        Assert.Equal(88, source.SourceVersion);
        var view = source.Query(source.Extent!.Value, 3);
        Assert.Same(source, view.Source);
        Assert.Same(DualAxisChartOptions.Default, view.Options);
        Assert.Equal(88, view.DatasetVersion);
        Assert.Equal(12, view.Series[0].SourceVersion);
        Assert.Equal(3, view.Series[1].SourceVersion);
        Assert.Equal(2, view.PointCount);
    }

    [Fact]
    public void OptionsAreImmutableAndHiddenAxisSuppressesCurvesWithoutChangingSeriesIntent()
    {
        var source = Source(("a", "left", new double?[] { 1, 2, 3 }), ("b", "right", new double?[] { 400, 500, 600 }));
        var original = new DualAxisChartOptions();
        var hidden = original.WithAxisVisibility("right", false);
        var view = source.Query(source.Extent!.Value, 2, hidden);
        Assert.Same(hidden, view.Options);
        Assert.True(original.IsAxisVisible("right"));
        Assert.True(hidden.IsSeriesVisible("b"));
        Assert.False(view.Axes[1].IsVisible);
        Assert.Empty(view.Axes[1].Ticks);
        Assert.Null(view.Axes[1].Domain);
        Assert.False(view.Series[1].IsVisible);
        Assert.Empty(view.Series[1].Segments);
        Assert.Equal(0, view.Series[1].AllocatedPointBudget);
        Assert.Equal(ChartBudgetStatus.Satisfied, view.BudgetStatus);
        Assert.Equal(2, view.PointCount);
        var restored = source.Query(source.Extent.Value, 4, hidden.WithAxisVisibility("right", true));
        Assert.True(restored.Series[1].IsVisible);
        Assert.Equal(new HistoryValueDomain(400, 600), restored.Axes[1].Domain);
        var noSeries = source.Query(source.Extent.Value, 0,
            original.WithSeriesVisibility("a", false).WithSeriesVisibility("b", false));
        Assert.All(noSeries.Axes, axis => Assert.True(axis.IsVisible));
        Assert.All(noSeries.Axes, axis => Assert.Null(axis.Domain));
        Assert.Equal(ChartBudgetStatus.Satisfied, noSeries.BudgetStatus);
    }

    [Fact]
    public void AutoDomainUnionsOnlyVisibleMappedRawValuesAndFixedDomainIsImmutable()
    {
        var source = Source(("a", "left", new double?[] { 1, 30, -7, 2 }),
            ("b", "left", new double?[] { -10, 400 }), ("c", "right", new double?[] { null, double.NaN }));
        var view = source.Query(source.Extent!.Value, 20);
        Assert.Equal(new HistoryValueDomain(-10, 400), view.Axes[0].Domain);
        Assert.Null(view.Axes[1].Domain);
        var hidden = source.Query(source.Extent.Value, 20, DualAxisChartOptions.Default.WithSeriesVisibility("b", false));
        Assert.Equal(new HistoryValueDomain(-7, 30), hidden.Axes[0].Domain);
        var options = new DualAxisChartOptions().WithAxisDomain("left", new HistoryValueDomain(10, 10));
        var fixedView = source.Query(source.Extent.Value, 20, options);
        Assert.Equal(new HistoryValueDomain(10, 10), fixedView.Axes[0].Domain);
        Assert.Equal(new HistoryAxisTick(10, 0.5), Assert.Single(fixedView.Axes[0].Ticks));
        Assert.Equal(new HistoryValueDomain(-10, 400),
            source.Query(source.Extent.Value, 20, options.WithAxisDomain("left", null)).Axes[0].Domain);
    }

    [Fact]
    public void EntireDatasetRefusesPartialCurvesWhenCombinedMandatoryBudgetDoesNotFit()
    {
        var source = Source(("a", "left", new double?[] { 2, 10, -8, 4, null, 3 }),
            ("b", "right", new double?[] { 7, 5 }));
        var view = source.Query(source.Extent!.Value, 6);
        Assert.Equal(7, view.RequiredPointBudget);
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, view.BudgetStatus);
        Assert.Equal(0, view.PointCount);
        Assert.All(view.Series, series => Assert.Empty(series.Segments));
        Assert.All(view.Series, series => Assert.Equal(0, series.AllocatedPointBudget));
        Assert.Single(view.Series[0].Gaps);
        Assert.Equal(new HistoryValueDomain(-8, 10), view.Axes[0].Domain);
        Assert.Equal(7, source.Query(source.Extent.Value, 7).PointCount);
    }

    [Fact]
    public void ProportionalRemainderUsesUnsampledPopulationAndOrdinalIdTies()
    {
        var source = Source(("z", "left", Enumerable.Range(0, 6).Select(i => (double?)i).ToArray()),
            ("a", "right", Enumerable.Range(0, 4).Select(i => (double?)i).ToArray()));
        // Mandatory: two each. Remaining capacity 4:2; three extras allocate 2:1.
        var view = source.Query(source.Extent!.Value, 7);
        Assert.Equal(new[] { 4, 3 }, view.Series.Select(series => series.AllocatedPointBudget));
        var tied = Source(("z", "left", new double?[] { 0, 1, 2, 3 }),
            ("a", "right", new double?[] { 0, 1, 2, 3 }));
        Assert.Equal(new[] { 2, 3 }, tied.Query(tied.Extent!.Value, 5).Series.Select(series => series.PointCount));
        Assert.Equal(8, tied.Query(tied.Extent.Value, int.MaxValue).PointCount);
    }

    [Fact]
    public void ExactHitIsRawAndQualifiedByDatasetSeriesAndRawSourceVersions()
    {
        var rawA = new HistorySeries(Enumerable.Range(0, 20).Select(i => new HistorySample(i, i, i)), 7);
        var rawB = new HistorySeries([new HistorySample(10, 10, 900), new HistorySample(11, 11, null)], 9);
        var source = new DualAxisHistorySource([Axis("left", HistoryAxisSide.Left)],
            [new HistorySeriesDefinition("a", "A", "left", rawA), new HistorySeriesDefinition("b", "B", "left", rawB)], 40);
        var view = source.Query(source.Extent!.Value, 3);
        Assert.DoesNotContain(view.Series[0].Segments.SelectMany(segment => segment.Points), point => point.Key == 10);
        var hit = source.FindNearest("a", 10)!.Value;
        Assert.Equal(40, hit.DatasetVersion);
        Assert.Equal(40, hit.SourceVersion);
        Assert.Equal(7, hit.SeriesSourceVersion);
        Assert.Equal("a", hit.SeriesId);
        Assert.Equal(new HistorySample(10, 10, 10), hit.Sample);
        Assert.Equal(10, hit.Key);
        Assert.Equal(10, hit.UtcTicks);
        Assert.Equal(10d, hit.Value);
        Assert.Equal(900d, source.FindNearest("b", 10)!.Value.Value);
        Assert.Null(source.FindNearest("b", 11, new TimeWindow(11, 12)));
        Assert.Null(source.FindNearest("b", 11, includeGaps: true)!.Value.Value);
        Assert.Throws<ArgumentException>(() => source.FindNearest("missing", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.FindNearest("a", -1));
    }

    [Fact]
    public void DuplicateTimesAndKeysAcrossSeriesRemainDistinctWithHalfOpenSharedWindow()
    {
        var a = new HistorySeries([new HistorySample(4, 10, 1), new HistorySample(2, 10, 2), new HistorySample(1, 20, 3)], 2);
        var b = new HistorySeries([new HistorySample(2, 10, 4), new HistorySample(1, 20, 5)], 6);
        var source = new DualAxisHistorySource([Axis("left", HistoryAxisSide.Left)],
            [new HistorySeriesDefinition("a", "A", "left", a), new HistorySeriesDefinition("b", "B", "left", b)], 10);
        var view = source.Query(new TimeWindow(10, 20), 10);
        Assert.Equal(3, view.PointCount);
        Assert.Equal(new long[] { 2, 4 }, view.Series[0].Segments[0].Points.Select(point => point.Key));
        Assert.All(view.Series.SelectMany(series => series.Segments).SelectMany(segment => segment.Points),
            point => Assert.Equal(10, point.UtcTicks));
        Assert.Equal(2, source.FindNearest("a", 15)!.Value.Key);
    }

    [Fact]
    public void EmptyAllGapConstantAndLastUtcTickRemainExplicit()
    {
        var empty = new DualAxisHistorySource([Axis("left", HistoryAxisSide.Left)], [], 1);
        Assert.Null(empty.Extent);
        Assert.Equal(0, empty.Count);
        Assert.Empty(empty.Query(default, 0).Series);
        Assert.Null(empty.Query(default, 0).Axes[0].Domain);
        var source = Source(("a", "left", new double?[] { null, double.NaN, double.PositiveInfinity, double.NegativeInfinity }),
            ("b", "right", new double?[] { double.MaxValue, double.MaxValue }));
        var view = source.Query(source.Extent!.Value, 2);
        Assert.Equal(2, view.PointCount);
        Assert.Null(view.Axes[0].Domain);
        Assert.Equal(HistoryGapReason.Missing | HistoryGapReason.NotANumber | HistoryGapReason.Infinite,
            Assert.Single(view.Series[0].Gaps).Reasons);
        Assert.Equal(new HistoryAxisTick(double.MaxValue, 0.5), Assert.Single(view.Axes[1].Ticks));
        var last = new DualAxisHistorySource([Axis("left", HistoryAxisSide.Left)],
            [new HistorySeriesDefinition("a", "A", "left", new HistorySeries([new HistorySample(1, DateTime.MaxValue.Ticks, 3)]))]);
        Assert.Equal(TimeWindow.MaximumExclusiveTicks, last.Extent!.Value.EndTicks);
        Assert.Equal(1, last.Query(last.Extent.Value, 1).PointCount);
        Assert.Equal(0, last.Query(new TimeWindow(DateTime.MaxValue.Ticks, DateTime.MaxValue.Ticks), 0).PointCount);
    }

    [Fact]
    public void InvalidDefinitionsMappingsBoundsOptionsAndCancellationAreRejected()
    {
        var left = Axis("left", HistoryAxisSide.Left);
        var raw = new HistorySeries([]);
        var series = new HistorySeriesDefinition("a", "A", "left", raw);
        Assert.Throws<ArgumentException>(() => new HistoryAxisDefinition(" ", "A", "V", HistoryAxisSide.Left));
        Assert.Throws<ArgumentException>(() => new HistoryAxisDefinition("a", "A", "", HistoryAxisSide.Left));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryAxisDefinition("a", "A", "V", (HistoryAxisSide)8));
        Assert.Throws<ArgumentException>(() => new DualAxisHistorySource([], []));
        Assert.Throws<ArgumentException>(() => new DualAxisHistorySource([left, left], []));
        Assert.Throws<ArgumentException>(() => new DualAxisHistorySource([left, Axis("second", HistoryAxisSide.Left)], []));
        Assert.Throws<ArgumentException>(() => new DualAxisHistorySource([left], [series, series]));
        Assert.Throws<ArgumentException>(() => new DualAxisHistorySource([left],
            [new HistorySeriesDefinition("a", "A", "absent", raw)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DualAxisHistorySource([left], [], -1));
        Assert.Throws<ArgumentException>(() => new DualAxisHistorySource([left],
            Enumerable.Range(0, 9).Select(i => new HistorySeriesDefinition(i.ToString(), "A", "left", raw))));
        Assert.Throws<ArgumentNullException>(() => new DualAxisHistorySource([left], [null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryValueDomain(10, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryValueDomain(double.NaN, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryValueDomain(0, double.PositiveInfinity));
        var source = new DualAxisHistorySource([left], [series]);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Query(default, -1));
        Assert.Throws<ArgumentException>(() => source.Query(default, 0, new DualAxisChartOptions().WithAxisVisibility("LEFT", false)));
        Assert.Throws<ArgumentException>(() => source.Query(default, 0, new DualAxisChartOptions().WithSeriesVisibility("missing", true)));
        Assert.Throws<ArgumentException>(() => source.Query(default, 0, new DualAxisChartOptions().WithAxisDomain("missing", default(HistoryValueDomain))));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => source.Query(default, 0, cancellationToken: canceled.Token));
    }

    [Fact]
    public void SeededSnapshotsMatchIndependentRunAndAllocationOracle()
    {
        var random = new Random(61043);
        for (var trial = 0; trial < 150; trial++)
        {
            var definitions = Enumerable.Range(0, random.Next(1, 9)).Select(seriesIndex =>
                new HistorySeriesDefinition($"s{seriesIndex}", $"Series {seriesIndex}", seriesIndex % 2 == 0 ? "left" : "right",
                    new HistorySeries(Enumerable.Range(0, random.Next(0, 110)).Select(index =>
                        new HistorySample(index, random.Next(0, 100), random.Next(6) switch
                        { 0 => null, 1 => double.NaN, _ => random.Next(-40, 40) })), seriesIndex + 3))).ToArray();
            var source = new DualAxisHistorySource([Axis("left", HistoryAxisSide.Left), Axis("right", HistoryAxisSide.Right)], definitions, trial);
            var options = new DualAxisChartOptions();
            foreach (var definition in definitions)
                if (random.Next(4) == 0) options = options.WithSeriesVisibility(definition.Id, false);
            if (random.Next(5) == 0) options = options.WithAxisVisibility("right", false);
            var start = random.Next(0, 101);
            var window = new TimeWindow(start, random.Next(start, 102));
            var budget = random.Next(0, 180);
            var view = source.Query(window, budget, options);
            var repeated = source.Query(window, budget, options);
            var required = new int[definitions.Length];
            var finite = new int[definitions.Length];
            for (var s = 0; s < definitions.Length; s++)
            {
                var effective = options.IsSeriesVisible(definitions[s].Id) && options.IsAxisVisible(definitions[s].AxisId);
                Assert.Equal(effective, view.Series[s].IsVisible);
                var raw = definitions[s].Source.Samples.Select((sample, index) => (sample, index))
                    .Where(item => effective && window.Contains(item.sample.UtcTicks)).ToArray();
                finite[s] = raw.Count(item => !item.sample.IsGap);
                var runs = new List<(HistorySample sample, int index)[]>();
                var current = new List<(HistorySample sample, int index)>();
                foreach (var item in raw)
                {
                    if (!item.sample.IsGap) current.Add(item);
                    else if (current.Count > 0) { runs.Add(current.ToArray()); current.Clear(); }
                }
                if (current.Count > 0) runs.Add(current.ToArray());
                var mandatory = runs.Select(run => new HashSet<int>
                {
                    run[0].index, run[^1].index,
                    run.OrderBy(item => item.sample.Value).First().index,
                    run.OrderByDescending(item => item.sample.Value).First().index
                }).ToArray();
                required[s] = mandatory.Sum(set => set.Count);
                Assert.Equal(required[s], view.Series[s].RequiredPointBudget);
                if (view.BudgetStatus == ChartBudgetStatus.Satisfied)
                {
                    Assert.Equal(runs.Count, view.Series[s].Segments.Length);
                    for (var r = 0; r < runs.Count; r++)
                    {
                        var actual = view.Series[s].Segments[r];
                        Assert.All(mandatory[r], index => Assert.Contains(actual.Points, point => point.RawIndex == index));
                        Assert.Equal(runs[r][0].index, actual.RawStartIndex);
                        Assert.Equal(runs[r][^1].index + 1, actual.RawEndIndexExclusive);
                        Assert.All(actual.Points, point => Assert.Equal(definitions[s].Source.Samples[point.RawIndex], point.Sample));
                    }
                }
                Assert.Equal(view.Series[s].Segments.SelectMany(segment => segment.Points),
                    repeated.Series[s].Segments.SelectMany(segment => segment.Points));
            }
            Assert.Equal(required.Sum(), view.RequiredPointBudget);
            Assert.InRange(view.PointCount, 0, budget);
            if (required.Sum() > budget)
            {
                Assert.Equal(ChartBudgetStatus.InsufficientBudget, view.BudgetStatus);
                Assert.All(view.Series, series => Assert.Empty(series.Segments));
                continue;
            }
            Assert.Equal(ChartBudgetStatus.Satisfied, view.BudgetStatus);
            Assert.Equal(Math.Min(budget, finite.Sum()), view.PointCount);
            var surplus = finite.Zip(required, (count, minimum) => count - minimum).ToArray();
            var remaining = Math.Min(budget - required.Sum(), surplus.Sum());
            var expected = required.ToArray();
            if (surplus.Sum() > 0)
            {
                var quotas = surplus.Select(count => (decimal)remaining * count / surplus.Sum()).ToArray();
                for (var s = 0; s < expected.Length; s++) expected[s] += (int)decimal.Floor(quotas[s]);
                foreach (var s in Enumerable.Range(0, expected.Length)
                             .OrderByDescending(s => quotas[s] - decimal.Floor(quotas[s]))
                             .ThenBy(s => definitions[s].Id, StringComparer.Ordinal)
                             .Take(Math.Min(budget, finite.Sum()) - expected.Sum())) expected[s]++;
            }
            Assert.Equal(expected, view.Series.Select(series => series.PointCount));
            foreach (var axis in view.Axes)
            {
                var values = definitions.Where(definition => definition.AxisId == axis.Definition.Id && axis.IsVisible &&
                        options.IsSeriesVisible(definition.Id)).SelectMany(definition => definition.Source.Samples)
                    .Where(sample => window.Contains(sample.UtcTicks) && !sample.IsGap).Select(sample => sample.Value!.Value).ToArray();
                Assert.Equal(values.Length == 0 ? (HistoryValueDomain?)null : new HistoryValueDomain(values.Min(), values.Max()), axis.Domain);
            }
        }
    }

    private static HistoryAxisDefinition Axis(string id, HistoryAxisSide side) => new(id, id, side == HistoryAxisSide.Left ? "°C" : "kPa", side);

    private static DualAxisHistorySource Source(params (string Id, string Axis, double?[] Values)[] input) => new(
        [Axis("left", HistoryAxisSide.Left), Axis("right", HistoryAxisSide.Right)],
        input.Select(item => new HistorySeriesDefinition(item.Id, item.Id, item.Axis,
            new HistorySeries(item.Values.Select((value, index) => new HistorySample(index, index, value))))));
}
