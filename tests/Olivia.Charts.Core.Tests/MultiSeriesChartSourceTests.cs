using Olivia.Core.Charts;
using Olivia.Core.Workspaces;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class MultiSeriesChartSourceTests
{
    private static MultiSeriesData Data(string id, params MultiSeriesSample[] samples) => new(id, $"Label {id}", samples);

    [Fact]
    public void CustomSourcePreservesOrdinalIdentitiesLongKeysAndSparseCoordinatesAcrossAdapters()
    {
        var source = new MultiSeriesChartSource([
            Data("sensor/north", new MultiSeriesSample(long.MinValue, int.MinValue, -3), new MultiSeriesSample(8_000_000_001, -23, null), new MultiSeriesSample(long.MaxValue, int.MaxValue, 4)),
            Data("Sensor/north", new MultiSeriesSample(long.MinValue, -23, 9), new MultiSeriesSample(-47, 987_654_321, -2))
        ], 91);
        using var session = new MultiSeriesChartSession(source);
        Assert.Equal(5, source.SampleCount);
        Assert.Equal(new[] { int.MinValue, -23, 987_654_321, int.MaxValue }, source.Coordinates);
        Assert.Equal(new[] { "sensor/north", "Sensor/north" }, session.VisibleSeries);
        var p = session.Publication;
        Assert.Equal(new HistoryValueDomain(int.MinValue, int.MaxValue), p.XDomain);
        Assert.Equal(new HistoryValueDomain(-3, 9), p.YDomain);
        Assert.Equal(91L, p.DataVersion);
        Assert.Equal(91L, p.Area.DatasetVersion);
        Assert.Equal(91L, p.Scatter.DatasetVersion);
        Assert.Equal(91L, p.Bars.DatasetVersion);
        Assert.Equal(5, p.Area.RawEntryCount);
        Assert.Equal(5, p.Scatter.RawEntryCount);
        Assert.Equal(5, p.Bars.RawEntryCount);
        Assert.Equal(new[] { "-2147483648", "-23", "987654321", "2147483647" }, p.Bars.Categories.Select(c => c.Key));
        Assert.Equal(0, p.BarCategoryStart);
        Assert.Equal(4, p.BarCategoryCount);
        for (var i = 0; i < session.Series.Count; i++)
        {
            var expected = session.Series[i];
            Assert.Equal(expected.Id, p.Area.Series[i].Id);
            Assert.Equal(expected.Id, p.Scatter.Series[i].Id);
            Assert.Equal(expected.Id, p.Bars.Series[i].Id);
            Assert.Equal(expected.Label, p.Bars.Series[i].Label);
            Assert.Equal(91L, p.Area.Series[i].SourceVersion);
            Assert.Equal(91L, p.Scatter.Series[i].SourceVersion);
            Assert.Equal(91L, p.Bars.Series[i].SourceVersion);
            Assert.Equal(expected.Samples.Select(s => s.Key), p.Area.Series[i].Samples.Select(s => s.Key));
            Assert.Equal(expected.Samples.Select(s => s.Key), p.Scatter.Series[i].Samples.Select(s => s.Key));
            Assert.Equal(expected.Samples.Select(s => s.Key), p.Bars.Series[i].Samples.Select(s => s.Key));
            Assert.Equal(expected.Samples.Select(s => (double?)s.X), p.Area.Series[i].Samples.Select(s => s.X));
            Assert.Equal(expected.Samples.Select(s => (double?)s.X), p.Scatter.Series[i].Samples.Select(s => s.X));
            Assert.Equal(expected.Samples.Select(s => s.Value), p.Area.Series[i].Samples.Select(s => s.Y));
            Assert.Equal(expected.Samples.Select(s => s.Value), p.Scatter.Series[i].Samples.Select(s => s.Y));
            Assert.Equal(expected.Samples.Select(s => s.Value), p.Bars.Series[i].Samples.Select(s => s.Value));
        }
        Assert.True(session.Select("sensor/north", long.MinValue, p));
        Assert.True(session.Select("Sensor/north", long.MinValue, p));
        Assert.Equal(new MultiSeriesSelection("Sensor/north", long.MinValue), session.Selection);
        Assert.False(session.Select("SENSOR/NORTH", long.MinValue, p));
    }

    [Fact]
    public void SourceCopiesCallerOwnedListsAndOldPublicationRemainsAnImmutableSnapshot()
    {
        var samples = new List<MultiSeriesSample> { new MultiSeriesSample(100, -12, 2), new MultiSeriesSample(200, 19, null) };
        var series = new List<MultiSeriesData> { new("custom", "Original", samples) };
        var source = new MultiSeriesChartSource(series, 42);
        using var session = new MultiSeriesChartSession(source);
        var before = session.Publication;
        samples[0] = new MultiSeriesSample(999, -100, 777);
        samples.Clear();
        series[0] = Data("replacement", new MultiSeriesSample(1, 0, 0));
        series.Clear();
        Assert.Single(source.Series);
        Assert.Equal("custom", source.Series[0].Id);
        Assert.Equal("Original", source.Series[0].Label);
        Assert.Equal(new[] { new MultiSeriesSample(100, -12, 2), new MultiSeriesSample(200, 19, null) }, source.Series[0].Samples);
        Assert.Equal(new[] { -12, 19 }, source.Coordinates);
        Assert.Same(before, session.Publication);
        Assert.True(session.ReplaceData(new MultiSeriesChartSource([Data("new", new MultiSeriesSample(300, 71, -1))], 43)));
        Assert.Equal(42L, before.DataVersion);
        Assert.Equal("custom", before.Area.Series[0].Id);
        Assert.Equal(new long[] { 100, 200 }, before.Bars.Series[0].Samples.Select(s => s.Key));
        Assert.Equal(new double?[] { 2, null }, before.Scatter.Series[0].Samples.Select(s => s.Y));
        Assert.Equal(new HistoryValueDomain(-12, 19), before.XDomain);
    }

    [Fact]
    public void BarWindowUsesSortedDistinctCoordinatesEvenWhenOwnersAreHidden()
    {
        using var session = new MultiSeriesChartSession(new MultiSeriesChartSource([
            Data("shown", new MultiSeriesSample(1, -100, 2), new MultiSeriesSample(2, 50, null), new MultiSeriesSample(3, 1_000_000, 5)),
            Data("hidden", new MultiSeriesSample(7, -20, 90), new MultiSeriesSample(8, 70, 100))
        ]));
        session.Apply(MultiSeriesChartKind.Bar, ["shown"], -30, 80, true, -5, 12);
        var p = session.Publication;
        Assert.Equal(new HistoryValueDomain(-30, 80), p.XDomain);
        Assert.Equal(new HistoryValueDomain(-1, 1), p.YDomain);
        Assert.Equal(1, p.BarCategoryStart);
        Assert.Equal(3, p.BarCategoryCount);
        Assert.Equal(new[] { "-100", "-20", "50", "70", "1000000" }, p.Bars.Categories.Select(c => c.Key));
        var bars = p.Bars.Query(new(categoryStart: p.BarCategoryStart, categoryCount: p.BarCategoryCount, fixedDomain: p.YDomain));
        Assert.Equal(1, bars.CategoryStart);
        Assert.Equal(3, bars.CategoryCount);
        session.Apply(MultiSeriesChartKind.Bar, ["shown"], 51, 69, true, -5, 12);
        Assert.Equal(3, session.Publication.BarCategoryStart);
        Assert.Equal(0, session.Publication.BarCategoryCount);
    }

    [Fact]
    public void EmptySourcesUseNeutralExtentWithoutInventingCategoriesOrSamples()
    {
        using var session = new MultiSeriesChartSession(new MultiSeriesChartSource([], 0));
        Assert.Empty(session.Series);
        Assert.Empty(session.VisibleSeries);
        Assert.Equal(new HistoryValueDomain(0, 1), session.Publication.XDomain);
        Assert.Equal(new HistoryValueDomain(-1, 1), session.Publication.YDomain);
        Assert.Empty(session.Publication.Bars.Categories);
        Assert.Equal(0, session.Publication.BarCategoryCount);
        Assert.Equal(0, session.Publication.Area.RawEntryCount);
        Assert.True(session.ReplaceData(new MultiSeriesChartSource([Data("empty")] )));
        Assert.Single(session.Series);
        Assert.Empty(session.Series[0].Samples);
        Assert.Empty(session.Publication.Bars.Categories);
        Assert.Equal(new HistoryValueDomain(0, 1), session.Publication.XDomain);
        Assert.False(session.Select("empty", 1));
        Assert.True(session.Reset());
        Assert.Equal(0, session.FirstIndex);
        Assert.Equal(1, session.LastIndex);
    }

    [Fact]
    public void SingletonSourceAllowsItsExactCoordinateAsTheInclusiveRange()
    {
        using var session = new MultiSeriesChartSession(new MultiSeriesChartSource([Data("only", new MultiSeriesSample(long.MaxValue, int.MinValue, -1_000_000))]));
        Assert.Equal(new HistoryValueDomain(int.MinValue, int.MinValue), session.Publication.XDomain);
        Assert.Equal(1, session.Publication.BarCategoryCount);
        Assert.True(session.Apply(MultiSeriesChartKind.Scatter, ["only"], int.MinValue, int.MinValue, true, -5, 12));
        Assert.True(session.Select("only", long.MaxValue));
        Assert.True(session.IsSelectionVisible);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Apply(MultiSeriesChartKind.Bar, ["only"], int.MinValue, int.MinValue + 1, true, -5, 12));
        session.Reset();
        Assert.Equal(int.MinValue, session.FirstIndex);
        Assert.Equal(int.MinValue, session.LastIndex);
    }

    [Fact]
    public void MaximumSeriesSamplesCategoriesAndValueMagnitudesAreAccepted()
    {
        var data = Enumerable.Range(0, 8).Select(i => Data($"channel-{i}", Enumerable.Range(0, 256)
            .Select(x => new MultiSeriesSample(long.MinValue + x, x - 128, x % 2 == 0 ? -1_000_000 : 1_000_000)).ToArray()));
        var source = new MultiSeriesChartSource(data, long.MaxValue);
        using var session = new MultiSeriesChartSession(source);
        Assert.Equal(8, session.Series.Count);
        Assert.Equal(2048, source.SampleCount);
        Assert.Equal(256, source.Coordinates.Length);
        Assert.Equal(2048, session.Publication.Area.RawEntryCount);
        Assert.Equal(2048, session.Publication.Scatter.RawEntryCount);
        Assert.Equal(2048, session.Publication.Bars.RawEntryCount);
        Assert.Equal(256, session.Publication.BarCategoryCount);
        Assert.Equal(new HistoryValueDomain(-1_000_000, 1_000_000), session.Publication.YDomain);
    }

    [Fact]
    public void InvalidIdentityOrderKeysNullInputsAndBudgetsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new MultiSeriesChartSource(null!));
        Assert.Throws<ArgumentNullException>(() => new MultiSeriesChartSession(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MultiSeriesChartSource([], -1));
        Assert.ThrowsAny<ArgumentException>(() => new MultiSeriesChartSource([null!]));
        Assert.ThrowsAny<ArgumentException>(() => new MultiSeriesChartSource([new("x", "x", null!)]));
        Assert.ThrowsAny<ArgumentException>(() => new MultiSeriesChartSource([Data("x", null!)]));
        Assert.ThrowsAny<ArgumentException>(() => new MultiSeriesChartSource([Data("x", new MultiSeriesSample[] { null! })]));
        Assert.ThrowsAny<ArgumentException>(() => new MultiSeriesChartSource([Data("x"), Data("x")]));
        Assert.ThrowsAny<ArgumentException>(() => new MultiSeriesChartSource([Data("")]));
        Assert.ThrowsAny<ArgumentException>(() => new MultiSeriesChartSource([Data(" ")]));
        Assert.ThrowsAny<ArgumentException>(() => new MultiSeriesChartSource([Data(null!)]));
        Assert.ThrowsAny<ArgumentException>(() => new MultiSeriesChartSource([new("x", null!, [])]));
        Assert.ThrowsAny<ArgumentException>(() => new MultiSeriesChartSource([Data("x", new MultiSeriesSample(1, 0, 1), new MultiSeriesSample(1, 1, 2))]));
        Assert.ThrowsAny<ArgumentException>(() => new MultiSeriesChartSource([Data("x", new MultiSeriesSample(1, 0, 1), new MultiSeriesSample(2, 0, null))]));
        Assert.ThrowsAny<ArgumentException>(() => new MultiSeriesChartSource([Data("x", new MultiSeriesSample(1, 0, null), new MultiSeriesSample(2, -1, 2))]));
        Assert.ThrowsAny<ArgumentException>(() => new MultiSeriesChartSource(Enumerable.Range(0, 9).Select(i => Data($"s{i}"))));
        Assert.ThrowsAny<ArgumentException>(() => new MultiSeriesChartSource([Data("x", Enumerable.Range(0, 257).Select(i => new MultiSeriesSample(i, i, null)).ToArray())]));
        Assert.ThrowsAny<ArgumentException>(() => new MultiSeriesChartSource(Enumerable.Range(0, 8).Select(i => Data($"s{i}",
            Enumerable.Range(0, i == 7 ? 257 : 256).Select(x => new MultiSeriesSample(x, x, 1)).ToArray()))));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1_000_000.01)]
    [InlineData(-1_000_000.01)]
    public void NonFiniteOrExcessiveYValuesAreRejected(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MultiSeriesChartSource([Data("x", new MultiSeriesSample(1, 0, value))]));
    }

    [Fact]
    public void FailedReplacementAndInvalidApplyKeepPublicationSelectionAndConfiguration()
    {
        using var session = new MultiSeriesChartSession(new MultiSeriesChartSource([Data("kept", new MultiSeriesSample(6, -10, 2), new MultiSeriesSample(9, 40, 4))], 17));
        session.Apply(MultiSeriesChartKind.Bar, ["kept"], -5, 30, false, -20, 20);
        session.Select("kept", 6);
        var publication = session.Publication;
        var source = session.Source;
        var visible = session.VisibleSeries;
        var changes = 0;
        session.Changed += (_, _) => changes++;
        Assert.Throws<ArgumentNullException>(() => session.ReplaceData((MultiSeriesChartSource)null!));
        Assert.ThrowsAny<ArgumentException>(() => session.ReplaceData(new MultiSeriesChartSource([Data("bad", new MultiSeriesSample(1, 0, 3), new MultiSeriesSample(2, -1, 4))])));
        Assert.ThrowsAny<ArgumentException>(() => session.Apply(MultiSeriesChartKind.Area, ["KEPT"], -5, 30, true, -5, 12));
        Assert.ThrowsAny<ArgumentException>(() => session.Apply(MultiSeriesChartKind.Area, ["kept"], -11, 30, true, -5, 12));
        Assert.Throws<InvalidOperationException>(() => session.Apply(MultiSeriesChartKind.Area, ThrowingVisibility(), -5, 30, true, -5, 12));
        Assert.Same(publication, session.Publication);
        Assert.Same(source, session.Source);
        Assert.Same(visible, session.VisibleSeries);
        Assert.Equal(new MultiSeriesSelection("kept", 6), session.Selection);
        Assert.Equal(MultiSeriesChartKind.Bar, session.Kind);
        Assert.False(session.AutoY);
        Assert.Equal(-5, session.FirstIndex);
        Assert.Equal(30, session.LastIndex);
        Assert.Equal(0, changes);

        static IEnumerable<string> ThrowingVisibility()
        {
            yield return "kept";
            throw new InvalidOperationException("Caller enumeration failed.");
        }
    }

    [Fact]
    public void ReplacementRetainsConfigurationCompatibleRangeAndNullSelectionAndShowsOnlyNewIds()
    {
        using var session = new MultiSeriesChartSession(new MultiSeriesChartSource([
            Data("retained", new MultiSeriesSample(101, -100, 5), new MultiSeriesSample(102, 100, 6)),
            Data("hidden", new MultiSeriesSample(201, 0, 7)), Data("removed", new MultiSeriesSample(301, 20, 8))
        ], 8));
        session.Select("retained", 101);
        session.Apply(MultiSeriesChartKind.Bar, ["retained"], -20, 40, false, -30, 50);
        var old = session.Publication;
        var changes = 0;
        session.Changed += (_, _) => changes++;
        Assert.True(session.ReplaceData(new MultiSeriesChartSource([
            Data("hidden", new MultiSeriesSample(201, -200, 2)),
            Data("retained", new MultiSeriesSample(101, 10, null), new MultiSeriesSample(102, 200, 9)),
            Data("newcomer", new MultiSeriesSample(401, 30, 4))
        ], 8)));
        Assert.Equal(new[] { "retained", "newcomer" }, session.VisibleSeries);
        Assert.Equal(-20, session.FirstIndex);
        Assert.Equal(40, session.LastIndex);
        Assert.Equal(MultiSeriesChartKind.Bar, session.Kind);
        Assert.False(session.AutoY);
        Assert.Equal(-30d, session.RequestedYMinimum);
        Assert.Equal(50d, session.RequestedYMaximum);
        Assert.Equal(new HistoryValueDomain(-30, 50), session.Publication.YDomain);
        Assert.Equal(new MultiSeriesSelection("retained", 101), session.Selection);
        Assert.NotNull(session.SelectedSample);
        Assert.Null(session.SelectedSample!.Value);
        Assert.False(session.IsSelectionVisible);
        Assert.True(session.Publication.Revision > old.Revision);
        Assert.Equal(8L, session.Publication.DataVersion);
        Assert.Equal(1, changes);
        Assert.False(session.Select("newcomer", 401, old));
        Assert.True(session.Select("newcomer", 401, session.Publication));
        Assert.True(session.IsSelectionVisible);
    }

    [Fact]
    public void IncompatibleReplacementResetsExtentAndRemovedIdentityClearsSelection()
    {
        using var session = new MultiSeriesChartSession(new MultiSeriesChartSource([Data("old", new MultiSeriesSample(7, -20, 3), new MultiSeriesSample(8, 50, 5))]));
        session.Select("old", 7);
        session.Apply(MultiSeriesChartKind.Scatter, ["old"], -10, 30, true, -10, 20);
        Assert.True(session.ReplaceData(new MultiSeriesChartSource([Data("old", new MultiSeriesSample(8, 500, 4), new MultiSeriesSample(9, 900, 6))], 2)));
        Assert.Null(session.Selection);
        Assert.Null(session.SelectedSample);
        Assert.Equal(500, session.FirstIndex);
        Assert.Equal(900, session.LastIndex);
        Assert.Equal(MultiSeriesChartKind.Scatter, session.Kind);
        Assert.True(session.AutoY);
        Assert.True(session.Select("old", 8));
        session.ReplaceData(new MultiSeriesChartSource([Data("OLD", new MultiSeriesSample(8, 500, 4), new MultiSeriesSample(9, 900, 6))]));
        Assert.Null(session.Selection);
        Assert.Equal(new[] { "OLD" }, session.VisibleSeries);
        session.ReplaceData(new MultiSeriesChartSource([]));
        Assert.Equal(0, session.FirstIndex);
        Assert.Equal(1, session.LastIndex);
        Assert.Equal(0, session.Publication.BarCategoryCount);
    }

    [Fact]
    public void ExpectedPublicationRejectsStaleApplyResetAndForeignSessionActions()
    {
        var source = new MultiSeriesChartSource([Data("host", new MultiSeriesSample(1, -4, 2), new MultiSeriesSample(2, 9, 3))]);
        using var session = new MultiSeriesChartSession(source);
        using var other = new MultiSeriesChartSession(source);
        var initial = session.Publication;
        Assert.False(session.Select("host", 1, other.Publication));
        session.Apply(MultiSeriesChartKind.Scatter, ["host"], -3, 8, true, -5, 12);
        Assert.False(session.Select("host", 1, initial));
        Assert.Null(session.Selection);
        var applied = session.Publication;
        session.Reset();
        Assert.False(session.Select("host", 2, applied));
        Assert.True(session.Select("host", 2, session.Publication));
        session.Dispose();
        Assert.False(session.Select("host", 1, session.Publication));
        Assert.False(session.ReplaceData(source));
    }
}
