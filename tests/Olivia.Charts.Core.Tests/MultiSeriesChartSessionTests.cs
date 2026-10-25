using Olivia.Core.Charts;
using Olivia.Core.Workspaces;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class MultiSeriesChartSessionTests
{
    [Fact]
    public void AdaptersPreserveOriginalIdentityGapsAndSharedDomains()
    {
        using var session = new MultiSeriesChartSession();
        Assert.Equal(new[] { "A", "B", "C" }, session.Series.Select(s => s.Id));
        Assert.Equal(new[] { "Observed", "Forecast", "Reference" }, session.Series.Select(s => s.Label));
        Assert.Equal(new double?[] { 2, 5, null, 7, -3, 5, 10, 4 }, session.Series[0].Samples.Select(s => s.Value));
        session.Apply(MultiSeriesChartKind.Scatter, ["C", "A", "B"], 1, 6, false, -4, 11);
        var p = session.Publication;
        Assert.Equal(new HistoryValueDomain(1, 6), p.XDomain);
        Assert.Equal(new HistoryValueDomain(-4, 11), p.YDomain);
        Assert.Equal(24, p.Area.RawEntryCount);
        Assert.Equal(24, p.Scatter.RawEntryCount);
        Assert.Equal(24, p.Bars.RawEntryCount);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(session.Series[i].Id, p.Area.Series[i].Id);
            Assert.Equal(session.Series[i].Id, p.Scatter.Series[i].Id);
            Assert.Equal(session.Series[i].Id, p.Bars.Series[i].Id);
            Assert.Equal(1L, p.Area.Series[i].SourceVersion);
            Assert.Equal(1L, p.Scatter.Series[i].SourceVersion);
            Assert.Equal(1L, p.Bars.Series[i].SourceVersion);
            for (var j = 0; j < 8; j++)
            {
                var sample = session.Series[i].Samples[j];
                Assert.Equal(sample.Key, p.Area.Series[i].Samples[j].Key);
                Assert.Equal(sample.Key, p.Scatter.Series[i].Samples[j].Key);
                Assert.Equal(sample.Key, p.Bars.Series[i].Samples[j].Key);
                Assert.Equal(sample.Value, p.Area.Series[i].Samples[j].Y);
                Assert.Equal(sample.Value, p.Scatter.Series[i].Samples[j].Y);
                Assert.Equal(sample.Value, p.Bars.Series[i].Samples[j].Value);
            }
        }
        var area = p.Area.Query(new(fixedXDomain: p.XDomain, fixedYDomain: p.YDomain));
        var scatter = p.Scatter.Query(new(fixedXDomain: p.XDomain, fixedYDomain: p.YDomain));
        var bars = p.Bars.Query(new(categoryStart: p.FirstIndex, categoryCount: p.LastIndex - p.FirstIndex + 1, fixedDomain: p.YDomain));
        Assert.Equal(p.XDomain, area.XDomain);
        Assert.Equal(p.XDomain, scatter.XDomain);
        Assert.Equal(p.YDomain, area.YDomain);
        Assert.Equal(p.YDomain, scatter.YDomain);
        Assert.Equal(p.YDomain, bars.Domain);
        Assert.Equal(1, bars.CategoryStart);
        Assert.Equal(6, bars.CategoryCount);
        Assert.Equal(3, area.Gaps.Length);
        Assert.Equal(3, scatter.Gaps.Length);
        Assert.Equal(3, bars.Gaps.Length);
        Assert.Equal(2, scatter.Points.Count(x => x.Hit.X == 1 && x.Hit.Y == 5));
    }

    [Fact]
    public void InvalidApplyLeavesEveryPublishedStateAndSelectionUntouched()
    {
        using var session = new MultiSeriesChartSession();
        session.Select("B", 2);
        var publication = session.Publication;
        var series = session.Series;
        var visible = session.VisibleSeries;
        var changes = 0;
        session.Changed += (_, _) => changes++;
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Apply((MultiSeriesChartKind)99, ["A"], 0, 7, true, -5, 12));
        Assert.Throws<ArgumentException>(() => session.Apply(MultiSeriesChartKind.Bar, ["missing"], 0, 7, true, -5, 12));
        Assert.Throws<ArgumentException>(() => session.Apply(MultiSeriesChartKind.Bar, ["A", "A"], 0, 7, true, -5, 12));
        Assert.Throws<ArgumentNullException>(() => session.Apply(MultiSeriesChartKind.Bar, null!, 0, 7, true, -5, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Apply(MultiSeriesChartKind.Bar, ["A"], 2, 2, true, -5, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Apply(MultiSeriesChartKind.Bar, ["A"], 0, 8, true, -5, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Apply(MultiSeriesChartKind.Bar, ["A"], 0, 7, true, double.NaN, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Apply(MultiSeriesChartKind.Bar, ["A"], 0, 7, false, -5, 1_000_001));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Apply(MultiSeriesChartKind.Bar, ["A"], 0, 7, false, 12, 12));
        Assert.Same(publication, session.Publication);
        Assert.Same(series, session.Series);
        Assert.Same(visible, session.VisibleSeries);
        Assert.Equal(MultiSeriesChartKind.Area, session.Kind);
        Assert.Equal(new MultiSeriesSelection("B", 2), session.Selection);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void SelectionSurvivesHiddenSeriesRangeAndKindsButDeletedKeysClear()
    {
        using var session = new MultiSeriesChartSession();
        Assert.False(session.Select("A", 3));
        Assert.False(session.Select("missing", 1));
        Assert.False(session.Select("A", 99));
        Assert.True(session.Select("A", 4));
        session.Apply(MultiSeriesChartKind.Bar, ["B"], 0, 7, true, -5, 12);
        Assert.Equal(new MultiSeriesSelection("A", 4), session.Selection);
        Assert.False(session.IsSelectionVisible);
        session.Apply(MultiSeriesChartKind.Scatter, ["A"], 5, 7, true, -5, 12);
        Assert.False(session.IsSelectionVisible);
        session.Apply(MultiSeriesChartKind.Area, ["A"], 0, 7, true, -5, 12);
        Assert.True(session.IsSelectionVisible);
        session.ReplaceData(true);
        Assert.Null(session.Selection);
        Assert.Null(session.SelectedSample);
        Assert.Equal(2L, session.Publication.DataVersion);
        Assert.Equal(2L, session.Publication.Area.DatasetVersion);
        Assert.Equal(2L, session.Publication.Scatter.DatasetVersion);
        Assert.Equal(2L, session.Publication.Bars.DatasetVersion);
        Assert.Equal(new long[] { 1, 2, 3, 5, 6, 7, 8 }, session.Series[0].Samples.Select(s => s.Key));
        Assert.Equal(new double?[] { 3, 6, null, -2, 6, 11, 5 }, session.Series[0].Samples.Select(s => s.Value));
        Assert.True(session.Select("B", 2));
        session.ReplaceData(false);
        Assert.Equal(new MultiSeriesSelection("B", 2), session.Selection);
        Assert.Equal(5d, session.SelectedSample!.Value);
        Assert.True(session.ClearSelection());
    }

    [Fact]
    public void AutoYUsesOnlyVisibleInRangeValuesIncludesZeroAndHasEmptyFallback()
    {
        using var session = new MultiSeriesChartSession();
        Assert.Equal(new HistoryValueDomain(-3, 10), session.Publication.YDomain);
        session.Apply(MultiSeriesChartKind.Area, ["B"], 0, 2, true, -20, 30);
        Assert.Equal(new HistoryValueDomain(0, 6), session.Publication.YDomain);
        session.Apply(MultiSeriesChartKind.Bar, [], 0, 7, true, -20, 30);
        Assert.Equal(new HistoryValueDomain(-1, 1), session.Publication.YDomain);
        Assert.Empty(session.Publication.Area.Series);
        session.Apply(MultiSeriesChartKind.Scatter, ["A"], 0, 7, false, 2, 9);
        session.Apply(MultiSeriesChartKind.Bar, ["C"], 3, 6, false, session.RequestedYMinimum, session.RequestedYMaximum);
        Assert.Equal(new HistoryValueDomain(2, 9), session.Publication.YDomain);
        Assert.Equal(2d, session.RequestedYMinimum);
        Assert.Equal(9d, session.RequestedYMaximum);
    }

    [Fact]
    public void ResetPreservesDataVersionAndCloseStopsChangesAndReentrantCallbacks()
    {
        using var session = new MultiSeriesChartSession();
        var calls = 0;
        session.Changed += (_, _) => { calls++; Assert.False(session.Reset()); };
        session.ReplaceData(true);
        session.Select("B", 1);
        var revision = session.Publication.Revision;
        session.Reset();
        Assert.True(session.Publication.Revision > revision);
        Assert.Equal(2L, session.Publication.DataVersion);
        Assert.Equal(3d, session.Series[0].Samples[0].Value);
        Assert.Equal(MultiSeriesChartKind.Area, session.Kind);
        Assert.Equal(3, session.VisibleSeries.Count);
        Assert.Equal(0, session.FirstIndex);
        Assert.Equal(7, session.LastIndex);
        Assert.True(session.AutoY);
        Assert.Null(session.Selection);
        Assert.Equal(3, calls);
        session.Dispose();
        Assert.True(session.IsClosed);
        Assert.False(session.Reset());
        Assert.False(session.ReplaceData(true));
        Assert.False(session.Select("A", 1));
        Assert.Equal(3, calls);
    }
}
