using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class ChartWorkspaceTests
{
    private static ScatterChartSource Source(long version = 7) => new([
        new ScatterSeries("alpha", "Same", [new(11, 1, 2), new(12, 4, 5), new(13, null, 6)], 19),
        new ScatterSeries("beta", "Same", [new(11, -4, -3)], 23)], version);
    private static ChartWorkspaceConfiguration Config(bool alpha = true) => new([
        new ChartWorkspaceSeries("beta", true, "purple", 8), new ChartWorkspaceSeries("alpha", alpha, "orange", 2)],
        new(-10, 10), new(-20, 20), new(-5, 5), new(-6, 6));

    [Fact]
    public void RoundTripHasOnlyFinitePresentationIntentAndDeterministicJson()
    {
        var config = Config(); var json = config.ToJson(); var restored = ChartWorkspaceConfiguration.Parse(json);
        Assert.Equal(json, restored.ToJson()); Assert.Equal(1, restored.Version); Assert.Equal("scatter", restored.Kind);
        Assert.Equal(new HistoryValueDomain(-10, 10), restored.FixedXDomain);
        Assert.Equal(new HistoryValueDomain(-20, 20), restored.FixedYDomain);
        Assert.Equal(new HistoryValueDomain(-5, 5), restored.ViewportX);
        Assert.Equal(new HistoryValueDomain(-6, 6), restored.ViewportY);
        Assert.DoesNotContain("samples", json); Assert.DoesNotContain("datasetVersion", json); Assert.DoesNotContain("sourceVersion", json);
        Assert.Equal("beta", restored.Series[0].Id); Assert.Equal(8, restored.Series[0].MarkerRadius);
    }

    [Fact]
    public void CopiesConfigurationAndMapsReorderedSameLabelSeriesByOrdinalId()
    {
        var settings = new[] { new ChartWorkspaceSeries("beta", true, "purple"), new ChartWorkspaceSeries("alpha", false, "blue") };
        var config = new ChartWorkspaceConfiguration(settings); settings[0] = new("alien", false, "green");
        var source = Source(); var view = config.Query(source);
        Assert.Single(view.Points); Assert.Equal("beta", view.Points[0].Hit.SeriesId);
        Assert.Equal(11, view.Points[0].Hit.Key); Assert.Equal(23, view.Points[0].Hit.SeriesSourceVersion);
        Assert.Equal(7, view.DatasetVersion); Assert.Same(source.Series[1], view.Source.Series[0]);
        Assert.Equal(4, source.RawEntryCount); Assert.Equal(3, source.Series[0].Samples.Length);
        Assert.Equal("beta", config.Series[0].Id);
    }

    [Fact]
    public void SourceOrderIsStableAndViewportOverridesEachDomain()
    {
        var config = Config(); var snapshot = config.Query(Source());
        Assert.Equal(new[] { "alpha", "alpha", "beta" }, snapshot.Points.Select(p => p.Hit.SeriesId));
        Assert.Equal(config.ViewportX, snapshot.XDomain); Assert.Equal(config.ViewportY, snapshot.YDomain);
        var clipped = new ChartWorkspaceConfiguration(config.Series, new(-100, 100), new(-100, 100), new(0, 2)).Query(Source());
        Assert.Single(clipped.Points); Assert.Equal(11, clipped.Points[0].Hit.Key);
        Assert.Equal(new HistoryValueDomain(-100, 100), clipped.YDomain);
    }

    [Fact]
    public void EmptyAndAllHiddenSourcesAreWellDefined()
    {
        var empty = ChartWorkspaceConfiguration.CreateDefault(new([]));
        Assert.Empty(ChartWorkspaceConfiguration.Parse(empty.ToJson()).Query(new([])).Points);
        var hidden = new ChartWorkspaceConfiguration([new("alpha", false, "blue"), new("beta", false, "green")]);
        var view = hidden.Query(Source()); Assert.Empty(view.Points); Assert.Empty(view.Gaps); Assert.Null(view.XDomain);
    }

    [Theory]
    [InlineData("\"version\":1", "\"version\":2")]
    [InlineData("\"version\":1", "\"version\":\"1\"")]
    [InlineData("\"version\":1", "\"version\":1.0")]
    [InlineData("\"kind\":\"scatter\"", "\"kind\":\"bar\"")]
    [InlineData("\"kind\":\"scatter\"", "\"kind\":null")]
    [InlineData("\"visible\":true", "\"visible\":1")]
    [InlineData("\"palette\":\"purple\"", "\"palette\":\"#FFFFFF\"")]
    [InlineData("\"markerRadius\":8", "\"markerRadius\":0")]
    [InlineData("\"markerRadius\":8", "\"markerRadius\":13")]
    [InlineData("\"markerRadius\":8", "\"markerRadius\":1e400")]
    [InlineData("\"markerRadius\":8", "\"markerRadius\":\"8\"")]
    [InlineData("\"id\":\"beta\"", "\"id\":\"alpha\"")]
    [InlineData("\"id\":\"beta\"", "\"id\":\" \"")]
    [InlineData("\"id\":\"beta\"", "\"id\":null")]
    [InlineData("\"minimum\":-10", "\"minimum\":11")]
    [InlineData("\"minimum\":-10", "\"minimum\":1e400")]
    [InlineData("\"minimum\":-10", "\"minimum\":\"-10\"")]
    public void CorruptScalarRejects(string oldValue, string newValue)
        => Assert.Throws<FormatException>(() => ChartWorkspaceConfiguration.Parse(Config().ToJson().Replace(oldValue, newValue, StringComparison.Ordinal)));

    [Fact]
    public void RejectsUnknownDuplicateMissingFieldsAtEveryLevelAndMalformedDocuments()
    {
        var json = Config().ToJson();
        string[] corrupt = ["null", "[]", "{}", json + "{}", "/*x*/" + json, json[..^1] + ",}",
            json.Replace("\"version\":1", "\"version\":1,\"version\":1"),
            json.Replace("\"version\":1", "\"version\":1,\"code\":\"run\""),
            json.Replace("\"kind\":\"scatter\",", ""),
            json.Replace("\"minimum\":-10", "\"minimum\":-10,\"minimum\":-10"),
            json.Replace("\"minimum\":-10", "\"minimum\":-10,\"extra\":0"),
            json.Replace("\"minimum\":-10,", ""),
            json.Replace("\"id\":\"beta\"", "\"id\":\"beta\",\"id\":\"beta\""),
            json.Replace("\"id\":\"beta\"", "\"id\":\"beta\",\"samples\":[]"),
            json.Replace("\"id\":\"beta\",", ""),
            json.Replace("\"id\":\"beta\"", "\"id\":\"\\uD800\"")];
        foreach (var value in corrupt) Assert.Throws<FormatException>(() => ChartWorkspaceConfiguration.Parse(value));
    }

    [Fact]
    public void EnforcesBoundsAndUtf16WithoutUnboundedEnumeration()
    {
        var json = Config().ToJson();
        Assert.Throws<FormatException>(() => ChartWorkspaceConfiguration.Parse(new string(' ', ChartWorkspaceConfiguration.MaximumJsonBytes) + json));
        Assert.Throws<FormatException>(() => ChartWorkspaceConfiguration.Parse(new string('界', 12000)));
        Assert.Throws<FormatException>(() => ChartWorkspaceConfiguration.Parse("[" + new string('[', 9) + new string(']', 9) + "]"));
        Assert.Throws<FormatException>(() => ChartWorkspaceConfiguration.Parse(json.Replace("beta", new string('x', 257))));
        Assert.Throws<FormatException>(() => ChartWorkspaceConfiguration.Parse(json.Replace("beta", "\ud800")));
        Assert.Throws<ArgumentException>(() => new ChartWorkspaceSeries("\ud800", true, "blue"));
        Assert.Throws<ArgumentException>(() => new ChartWorkspaceConfiguration(Enumerable.Range(0, 9).Select(i => new ChartWorkspaceSeries($"s{i}", true, "blue"))));
        Assert.Throws<ArgumentException>(() => new ChartWorkspaceConfiguration([new("same", true, "blue"), new("same", true, "green")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChartWorkspaceSeries("id", true, "blue", double.NaN));
        var max = new ChartWorkspaceConfiguration(Enumerable.Range(0, 8).Select(i => new ChartWorkspaceSeries(new string('界', 255) + i, true, "green", 12)));
        Assert.Equal(max.ToJson(), ChartWorkspaceConfiguration.Parse(max.ToJson()).ToJson());
    }

    [Fact]
    public void StrictIdentityPolicyRejectsNewMissingRenamedAndCaseChangedIds()
    {
        var source = Source();
        foreach (var series in new ChartWorkspaceSeries[][] {
            [new("alpha", true, "blue")], [new("alpha", true, "blue"), new("Beta", true, "green")],
            [new("alpha", true, "blue"), new("beta", true, "green"), new("new", true, "purple")] })
            Assert.Throws<ArgumentException>(() => new ChartWorkspaceConfiguration(series).Query(source));
    }

    [Fact]
    public void PreviewApplyCancelAndErrorAreAtomic()
    {
        using var session = new ChartWorkspaceSession(Source());
        var original = session.Current; var originalSnapshot = session.Snapshot;
        var preview = session.Preview(Config(false));
        Assert.Same(original, session.Current); Assert.Same(originalSnapshot, session.Snapshot);
        Assert.Same(session.Source, preview.Source); Assert.Single(preview.Snapshot.Points);
        var revision = session.Revision;
        Assert.Throws<FormatException>(() => session.PreviewJson("{}"));
        Assert.Throws<ArgumentException>(() => session.Preview(new([])));
        Assert.Equal(revision, session.Revision); Assert.Same(preview, session.Pending);
        session.Cancel(); Assert.False(session.Apply(preview)); Assert.Same(original, session.Current);
        var accepted = session.PreviewJson(Config(false).ToJson());
        Assert.True(session.Apply(accepted)); Assert.Same(accepted.Configuration, session.Current);
        Assert.Same(accepted.Snapshot, session.Snapshot); Assert.Null(session.Pending);
        Assert.False(session.Apply(accepted));
    }

    [Fact]
    public void NewerPreviewOtherSessionAndSourceReplacementInvalidateTokensEvenSameVersion()
    {
        using var session = new ChartWorkspaceSession(Source());
        using var other = new ChartWorkspaceSession(session.Source);
        var stale = session.Preview(Config()); var latest = session.Preview(Config(false));
        Assert.False(session.Apply(stale)); Assert.False(other.Apply(latest)); Assert.Same(latest, session.Pending);
        var replacement = Source(session.Source.DatasetVersion);
        session.ReplaceSource(replacement); Assert.False(session.Apply(latest)); Assert.Same(replacement, session.Source);
        var next = session.Preview(Config(false)); session.ReplaceSource(Source(8)); Assert.False(session.Apply(next));
        Assert.All(session.Current.Series, s => Assert.True(s.Visible));
    }

    [Fact]
    public void CallbackReentryCannotCommitStaleResults()
    {
        using var session = new ChartWorkspaceSession(Source()); var original = session.Current;
        var preview = session.Preview(Config(false));
        Assert.False(session.Apply(preview, p => Assert.False(session.Apply(p)))); Assert.Same(original, session.Current);
        preview = session.Preview(Config(false)); Assert.False(session.Apply(preview, _ => session.Cancel())); Assert.Same(original, session.Current);
        preview = session.Preview(Config(false)); Assert.False(session.Apply(preview, _ => session.Preview(Config()))); Assert.Same(original, session.Current);
        Assert.NotNull(session.Pending); Assert.True(session.Apply(session.Pending!));
        preview = session.Preview(Config(false));
        Assert.False(session.Apply(preview, _ => session.ReplaceSource(Source()))); Assert.All(session.Current.Series, s => Assert.True(s.Visible));
    }

    [Fact]
    public void ThrowingCallbackAndDisposalCannotPublishPreparedState()
    {
        var session = new ChartWorkspaceSession(Source()); var original = session.Current;
        var preview = session.Preview(Config(false)); var revision = session.Revision;
        Assert.Throws<InvalidOperationException>(() => session.Apply(preview, _ => throw new InvalidOperationException("test")));
        Assert.Same(original, session.Current); Assert.Same(preview, session.Pending); Assert.Equal(revision, session.Revision);
        Assert.False(session.Apply(preview, _ => session.Dispose())); Assert.True(session.IsDisposed);
        Assert.False(session.Apply(preview)); Assert.Throws<ObjectDisposedException>(() => session.Preview(Config()));
        Assert.Throws<ObjectDisposedException>(() => session.ReplaceSource(Source())); Assert.Throws<ObjectDisposedException>(() => session.Cancel());
        session.Dispose(); Assert.Same(original, session.Current);
    }
}
