using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class RadarChartTests
{
    [Fact]
    public void FourAxesCopyInputsAndUseLiteralClockwiseCoordinatesIncludingHalfRadius()
    {
        var axes = Axes();
        double?[] values = [100, 50, 100, 50];
        var series = new[] { new RadarSeries("s", "Same label", values) };
        var source = new RadarSource(axes, series, 7);
        axes[0] = new("changed", "Changed"); values[0] = -12; series[0] = new("changed", "Changed", [1, 2, 3, 4]);
        using var session = new RadarSession(source);

        Assert.Same(source, session.Source); Assert.Equal(7, source.Version);
        Assert.Equal(new[] { "north", "east", "south", "west" }, source.Axes.Select(a => a.Id));
        Assert.Equal(new double?[] { 100, 50, 100, 50 }, source.Series[0].Values);
        var expected = new[] { (X: 0d, Y: -1d), (X: .5d, Y: 0d), (X: 0d, Y: 1d), (X: -.5d, Y: 0d) };
        Assert.Equal(4, session.Geometry.Points.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            var point = session.Geometry.Points[i];
            Assert.Equal(new RadarKey("s", source.Axes[i].Id), point.Key);
            Assert.Equal(expected[i].X, point.X, 12); Assert.Equal(expected[i].Y, point.Y, 12);
            Assert.Equal(source.Series[0].Values[i], point.Value);
        }
    }

    [Fact]
    public void EdgesCloseOnlyDeclaredAdjacencyAndNeverBridgeMissingOrOutsideValues()
    {
        using var session = new RadarSession(Source(100, 50, 75, 25));
        Assert.Equal(new[] { ("north", "east"), ("east", "south"), ("south", "west"), ("west", "north") },
            EdgeIds(session.Geometry));
        Assert.All(session.Geometry.Edges, edge =>
        {
            Assert.Contains(edge.Start, session.Geometry.Points); Assert.Contains(edge.End, session.Geometry.Points);
        });

        session.ReplaceSource(Source(100, null, 75, 25));
        Assert.Equal(new[] { "north", "south", "west" }, session.Geometry.Points.Select(p => p.Key.AxisId));
        Assert.Equal(new[] { ("south", "west"), ("west", "north") }, EdgeIds(session.Geometry));
        session.ReplaceSource(Source(100, -1, 101, 25));
        Assert.Equal(new[] { "north", "west" }, session.Geometry.Points.Select(p => p.Key.AxisId));
        Assert.Equal(new[] { ("west", "north") }, EdgeIds(session.Geometry));
        Assert.Equal(new double?[] { 100, -1, 101, 25 }, session.Source.Series[0].Values);
        session.ReplaceSource(Source(null, 20, 30, 40));
        Assert.Equal(new[] { ("east", "south"), ("south", "west") }, EdgeIds(session.Geometry));
        session.SetVisible("s", false);
        Assert.Empty(session.Geometry.Points); Assert.Empty(session.Geometry.Edges); Assert.Equal(100, session.Maximum);
    }

    [Fact]
    public void ValidationRejectsMalformedSourcesAndMaximumChangesBeforeAnyPublication()
    {
        var axes = Axes();
        Assert.Throws<ArgumentException>(() => new RadarSource(axes.Take(2), []));
        Assert.Throws<ArgumentException>(() => new RadarSource(Enumerable.Range(0, 13).Select(i => new RadarAxis($"a{i}", "Axis")), []));
        Assert.Throws<ArgumentException>(() => new RadarSource([axes[0], axes[0], axes[2]], []));
        Assert.Throws<ArgumentException>(() => new RadarSource(axes, [new("s", "S", [1, 2, 3])]));
        Assert.Throws<ArgumentException>(() => new RadarSource(axes, [new("s", "S", [1, 2, 3, 4, 5])]));
        Assert.Throws<ArgumentException>(() => new RadarSource(axes, [new("s", "S", [1, 2, 3, 4]), new("s", "Other", [4, 3, 2, 1])]));
        Assert.Throws<ArgumentException>(() => new RadarSource(axes,
            Enumerable.Range(0, 9).Select(i => new RadarSeries($"s{i}", "Series", [1, 2, 3, 4]))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadarSource(axes, [], -1));
        Assert.Throws<ArgumentNullException>(() => new RadarSource(null!, []));
        Assert.Throws<ArgumentNullException>(() => new RadarSource(axes, null!));
        Assert.Throws<ArgumentNullException>(() => new RadarSource([axes[0], axes[1], null!], []));
        Assert.Throws<ArgumentNullException>(() => new RadarSource(axes, [null!]));
        Assert.Throws<ArgumentException>(() => new RadarAxis(" ", "Axis"));
        Assert.Throws<ArgumentException>(() => new RadarAxis("a", "\ud800"));
        Assert.Throws<ArgumentException>(() => new RadarSeries("s", new string('x', 257), [1, 2, 3, 4]));
        Assert.Throws<ArgumentException>(() => new RadarSeries("s", "S", Enumerable.Repeat<double?>(1, 13)));
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1_000_001d, -1_000_001d })
            Assert.Throws<ArgumentOutOfRangeException>(() => new RadarSeries("s", "S", [invalid, 0, 0, 0]));
        Assert.Equal(3, new RadarSource([new("a", "Same"), new("A", "Same"), new("b", "Same")], []).Axes.Length);
        Assert.Equal(new double?[] { -1_000_000, 1_000_000, null, 0 }, Source(-1_000_000, 1_000_000, null, 0).Series[0].Values);
        var largest = new RadarSource(Enumerable.Range(0, 12).Select(i => new RadarAxis($"a{i}", "Axis")),
            Enumerable.Range(0, 8).Select(i => new RadarSeries($"s{i}", "Series", Enumerable.Repeat<double?>(100, 12))));
        using (var admitted = new RadarSession(largest))
        {
            Assert.Equal(12, largest.Axes.Length); Assert.Equal(8, largest.Series.Length);
            Assert.Equal(96, admitted.Geometry.Points.Length); Assert.Equal(96, admitted.Geometry.Edges.Length);
        }

        var source = Source(20, 40, 60, 80);
        using var session = new RadarSession(source);
        session.Select(new("s", "east"));
        var geometry = session.Geometry; var hidden = session.HiddenSeries; var changes = 0;
        session.Changed += (_, _) => changes++;
        foreach (var invalid in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1_000_001d })
            Assert.Throws<ArgumentOutOfRangeException>(() => session.SetMaximum(invalid));
        Assert.Throws<ArgumentNullException>(() => session.ReplaceSource(null!));
        Assert.Throws<ArgumentException>(() => session.ReplaceSource(new(axes, [new("s", "S", [1, 2, 3])], 8)));
        Assert.Throws<ArgumentException>(() => session.Select(new("absent", "east")));
        Assert.Throws<ArgumentException>(() => session.Select(new("s", "absent")));
        Assert.Throws<ArgumentException>(() => session.Select(default));
        Assert.Throws<ArgumentException>(() => session.SetVisible("absent", false));
        Assert.Same(source, session.Source); Assert.Same(geometry, session.Geometry); Assert.Same(hidden, session.HiddenSeries);
        Assert.Equal(100, session.Maximum); Assert.Equal(new RadarKey("s", "east"), session.SelectedKey);
        Assert.Equal(40, session.SelectedValue); Assert.Equal(0, changes);
        Assert.False(session.SetMaximum(100)); Assert.False(session.SetVisible("s", true));
        Assert.True(session.SetMaximum(200)); Assert.Equal(1, changes); Assert.Equal(200, session.Maximum);
        Assert.Equal(40, session.SelectedValue); Assert.Equal(.2, session.Geometry.Points[1].X, 12);
    }

    [Fact]
    public void StablePairSelectionSurvivesHiddenOutsideMissingAndReorderedSourceVersions()
    {
        var axes = Axes();
        using var session = new RadarSession(new(axes,
            [new("first", "Same", [25, 25, null, -1]), new("second", "Same", [25, 25, null, -1])], 1));
        var selected = new RadarKey("second", "east");
        session.Select(selected);
        Assert.Equal(25, session.SelectedValue); Assert.Contains("second", session.SelectionDescription);
        Assert.Contains("east", session.SelectionDescription); Assert.Contains("source version 1", session.SelectionDescription);
        session.SetVisible("second", false);
        var hidden = session.HiddenSeries;
        Assert.Equal(selected, session.SelectedKey); Assert.Equal(25, session.SelectedValue);
        Assert.Contains("hidden", session.SelectionDescription);
        Assert.DoesNotContain(session.Geometry.Points, p => p.Key.SeriesId == "second");
        session.SetMaximum(20);
        Assert.Equal(selected, session.SelectedKey); Assert.Contains("outside", session.SelectionDescription);
        Assert.Contains("hidden", session.SelectionDescription); Assert.Empty(session.Geometry.Points);
        session.SetVisible("second", true); session.SetMaximum(100);
        Assert.Contains("second", hidden); Assert.Empty(session.HiddenSeries);
        Assert.Equal(selected, session.Geometry.Points.Single(p => p.Key == selected).Key);

        RadarAxis[] reordered = [axes[1], axes[3], axes[0], axes[2]];
        session.ReplaceSource(new(reordered,
            [new("second", "Renamed", [75, 25, 25, null]), new("first", "Same", [25, 25, 25, null])], 2));
        Assert.Equal(selected, session.SelectedKey); Assert.Equal(75, session.SelectedValue);
        Assert.Contains("source version 2", session.SelectionDescription); Assert.Contains("Renamed", session.SelectionDescription);
        var moved = session.Geometry.Points.Single(p => p.Key == selected);
        Assert.Equal(0, moved.X, 12); Assert.Equal(-.75, moved.Y, 12);
        session.SetVisible("second", false);
        session.ReplaceSource(new(reordered, [new("second", "Same", [null, 25, 25, 25])], 3));
        Assert.Equal(selected, session.SelectedKey); Assert.Null(session.SelectedValue);
        Assert.Contains("missing", session.SelectionDescription); Assert.Contains("hidden", session.SelectionDescription);
        Assert.Contains("source version 3", session.SelectionDescription); Assert.Contains("second", session.HiddenSeries);
        session.SetVisible("second", true);
        session.ReplaceSource(new([axes[0], axes[2], axes[3]], [new("second", "Same", [25, 25, 25])], 4));
        Assert.Null(session.SelectedKey); Assert.Null(session.SelectedValue); Assert.Equal("No selection.", session.SelectionDescription);
        session.Select(new("second", "north")); session.SetVisible("second", false);
        session.ReplaceSource(new(axes, [new("first", "Same", [25, 25, 25, 25])], 5));
        Assert.Null(session.SelectedKey); Assert.Empty(session.HiddenSeries);
        session.Select(new("first", "west")); Assert.True(session.ClearSelection()); Assert.False(session.ClearSelection());
    }

    [Fact]
    public void ZeroMarkersEmptySeriesAndSixTicksRemainExplicitAndCloseIsPermanent()
    {
        using var session = new RadarSession(Source(0, -0d, null, 0));
        Assert.Equal(3, session.Geometry.Points.Length);
        Assert.All(session.Geometry.Points, point => { Assert.Equal(0, point.X); Assert.Equal(0, point.Y); });
        Assert.Equal(new[] { ("north", "east"), ("west", "north") }, EdgeIds(session.Geometry));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0d), BitConverter.DoubleToInt64Bits(session.Geometry.Points[1].Value));
        Assert.Equal(new[] { 0d, 20, 40, 60, 80, 100 }, session.Geometry.Ticks);
        session.Select(new("s", "east")); Assert.Equal(0, session.SelectedValue);
        session.ReplaceSource(new(Axes(), [], 6));
        Assert.Empty(session.Source.Series); Assert.Empty(session.Geometry.Points); Assert.Empty(session.Geometry.Edges);
        Assert.Null(session.SelectedKey); Assert.Equal(new[] { 0d, 20, 40, 60, 80, 100 }, session.Geometry.Ticks);
        session.SetMaximum(250); Assert.Equal(new[] { 0d, 50, 100, 150, 200, 250 }, session.Geometry.Ticks);
        session.SetMaximum(1_000_000); Assert.Equal(new[] { 0d, 200_000, 400_000, 600_000, 800_000, 1_000_000 }, session.Geometry.Ticks);
        session.ReplaceSource(Source(0, null, 0, 0)); session.Select(new("s", "north"));
        var changes = 0; session.Changed += (_, _) => changes++;
        var source = session.Source; var geometry = session.Geometry;
        session.Close(); session.Close();
        Assert.True(session.IsClosed); Assert.Null(session.SelectedKey);
        Assert.False(session.SetMaximum(100)); Assert.False(session.SetVisible("s", false));
        Assert.False(session.Select(new("s", "north"))); Assert.False(session.ReplaceSource(Source(1, 2, 3, 4)));
        Assert.False(session.ClearSelection()); Assert.Same(source, session.Source); Assert.Same(geometry, session.Geometry);
        Assert.Equal(0, changes);
    }

    private static RadarAxis[] Axes() => [new("north", "Same"), new("east", "Same"), new("south", "Same"), new("west", "Same")];
    private static RadarSource Source(params double?[] values) => new(Axes(), [new("s", "Series", values)]);
    private static IEnumerable<(string Start, string End)> EdgeIds(RadarGeometry geometry) =>
        geometry.Edges.Select(edge => (edge.Start.Key.AxisId, edge.End.Key.AxisId));
}
