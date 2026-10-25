using System.Globalization;
using System.Text;
using System.Text.Json;
using Olivia.Core.Charts;
using Olivia.Core.Export;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class ScatterLiveTests
{
    [Theory]
    [InlineData(0, 1)] [InlineData(2049, 1)] [InlineData(1, -1)] [InlineData(1, 4097)]
    public void RejectsInvalidAdmission(int capacity, int total)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ScatterLiveSession(capacity, total));

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(65)]
    public void InvalidBatchIsAtomic(int batch)
    {
        var session = new ScatterLiveSession(); var original = session.Source;
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Append(batch));
        Assert.Same(original, session.Source); Assert.Equal(0, session.AcceptedCount);
    }

    [Theory]
    [InlineData(1, 4096)] [InlineData(64, 4096)] [InlineData(2048, 4096)] [InlineData(64, 0)]
    public void FiniteInputRetainsExactSuffixAndVersions(int capacity, int total)
    {
        var session = new ScatterLiveSession(capacity, total);
        for (var expected = 0; expected < total;)
        {
            var before = session.Source;
            expected += Math.Min(31, total - expected);
            session.Append(31);
            Assert.Equal(expected, session.AcceptedCount); Assert.Equal(total - expected, session.RemainingCount);
            Assert.Equal(Math.Max(0, expected - capacity), session.EvictedCount);
            Assert.Equal(expected, session.Source.DatasetVersion);
            Assert.Equal(expected, session.Source.Series[0].SourceVersion);
            Assert.Equal(Enumerable.Range(Math.Max(1, expected - capacity + 1), Math.Min(capacity, expected)).Select(n => (long)n),
                session.Source.Series[0].Samples.Select(s => s.Key));
            Assert.NotSame(before, session.Source); Assert.Same(session.Source, session.ShownSource);
        }
        var final = session.Source; Assert.Equal(0, session.Append(64)); Assert.Same(final, session.Source);
    }

    [Fact]
    public void PauseAndResumeKeepFrozenViewAndRawImmutable()
    {
        var session = new ScatterLiveSession(8, 20); session.Append(8);
        var options = new ScatterChartOptions(0, new(0, 1), new(0, 1));
        var shown = session.ShownSource.Query(options); session.Pause(); session.Append(8);
        Assert.Same(shown.Source, session.ShownSource); Assert.NotSame(session.Source, session.ShownSource);
        var frame = session.Freeze(shown); var bytes = ScatterLiveExport.SerializeJson(frame);
        Assert.Equal(8, frame.Source.DatasetVersion); Assert.Equal(16, frame.AcceptedCount);
        Assert.Equal(8, frame.EvictedCount); Assert.Equal(4, frame.RemainingCount); Assert.True(frame.IsPaused);
        Assert.Same(options, frame.Snapshot.Options); Assert.Equal(8, frame.Source.RawEntryCount);
        Assert.Empty(frame.Snapshot.Points); Assert.NotEqual(0, frame.Snapshot.RequiredPointCount);
        Assert.NotEqual(frame.SnapshotId, session.Freeze(shown).SnapshotId);
        session.Resume(); session.Append(8);
        Assert.Equal(bytes, ScatterLiveExport.SerializeJson(frame));
        Assert.Equal(Enumerable.Range(1, 8).Select(n => (long)n), frame.Source.Series[0].Samples.Select(s => s.Key));
        Assert.Throws<InvalidOperationException>(() => session.Freeze(shown));
        Assert.Throws<InvalidOperationException>(() => session.Freeze(new ScatterChartSource([]).Query()));
    }

    [Fact]
    public void GeneratedRawContainsDistinctGapsDuplicatesAndSignedZero()
    {
        var session = new ScatterLiveSession(); session.Append(16); var samples = session.Source.Series[0].Samples;
        Assert.Equal(samples[0].X, samples[1].X); Assert.Equal(samples[0].Y, samples[1].Y);
        Assert.NotEqual(samples[0].Key, samples[1].Key);
        Assert.Null(samples[4].X); Assert.True(double.IsNaN(samples[5].Y!.Value));
        Assert.Equal(double.PositiveInfinity, samples[6].X); Assert.Equal(double.NegativeInfinity, samples[7].Y);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(samples[8].X!.Value));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(samples[8].Y!.Value));
    }

    [Fact]
    public void JsonContainsAllRawRegardlessOfGapClippingOrGeometryBudget()
    {
        var session = new ScatterLiveSession(); session.Append(16);
        var frame = session.Freeze(session.ShownSource.Query(new(0, new(0, 0), new(0, 0))));
        using var json = JsonDocument.Parse(ScatterLiveExport.SerializeJson(frame));
        var root = json.RootElement; Assert.Equal(frame.SnapshotId, root.GetProperty("snapshotId").GetString());
        Assert.Equal("retained-raw", root.GetProperty("scope").GetString());
        var rows = root.GetProperty("series")[0].GetProperty("samples"); Assert.Equal(16, rows.GetArrayLength());
        for (var i = 0; i < rows.GetArrayLength(); i++)
        {
            Assert.Equal(i, rows[i].GetProperty("sourceIndex").GetInt32());
            Assert.Equal(i + 1, rows[i].GetProperty("key").GetInt64());
        }
        Assert.Equal("missing", rows[4].GetProperty("xState").GetString());
        Assert.Equal("nan", rows[5].GetProperty("yState").GetString());
        Assert.Equal("positive-infinity", rows[6].GetProperty("xState").GetString());
        Assert.Equal("negative-infinity", rows[7].GetProperty("yState").GetString());
        Assert.Equal(JsonValueKind.Null, rows[5].GetProperty("y").ValueKind);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(rows[8].GetProperty("x").GetDouble()));
    }

    [Fact]
    public void SerializersAreInvariantBoundedAndSupportEmptyFrame()
    {
        var session = new ScatterLiveSession(totalSamples: 0); var frame = session.Freeze(session.ShownSource.Query());
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var json = ScatterLiveExport.SerializeJson(frame); var csv = ScatterLiveExport.SerializeCsv(frame);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal(json, ScatterLiveExport.SerializeJson(frame)); Assert.Equal(csv, ScatterLiveExport.SerializeCsv(frame));
            Assert.Equal(json, ScatterLiveExport.SerializeJson(frame, json.Length));
            Assert.Equal(csv, ScatterLiveExport.SerializeCsv(frame, csv.Length));
            Assert.Throws<SnapshotExportLimitException>(() => ScatterLiveExport.SerializeJson(frame, json.Length - 1));
            Assert.Throws<SnapshotExportLimitException>(() => ScatterLiveExport.SerializeCsv(frame, csv.Length - 1));
            var text = Encoding.UTF8.GetString(csv);
            Assert.StartsWith("record,snapshot_id,", text); Assert.Contains("\r\n", text);
            Assert.Contains("\"series\"", text); Assert.DoesNotContain("\"sample\"", text);
            Assert.Contains("'Synthetic XY", text);
            Assert.Throws<ArgumentOutOfRangeException>(() => ScatterLiveExport.SerializeJson(frame, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ScatterLiveExport.SerializeCsv(frame, ScatterLiveExport.MaximumEncodedBytes + 1));
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }
}
