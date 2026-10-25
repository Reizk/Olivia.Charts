using System.Globalization;
using System.Text;
using System.Text.Json;
using Olivia.Core.Export;

namespace Olivia.Core.Charts;

/// <summary>Bounded UTF-8 serializers of every retained original pair, including gaps and clipped
/// points. They never enumerate render replacements. CSV text uses one protective apostrophe.</summary>
public static class ScatterLiveExport
{
    public const int MaximumEncodedBytes = 4 * 1024 * 1024;
    public const string Scope = "retained-raw";

    public static byte[] SerializeJson(ScatterLiveFrame frame, int maximumBytes = MaximumEncodedBytes)
    {
        Validate(frame, maximumBytes);
        using var stream = new BoundedExportStream(maximumBytes, default);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("snapshotId", frame.SnapshotId); writer.WriteString("scope", Scope);
            writer.WriteNumber("datasetVersion", frame.Source.DatasetVersion);
            writer.WriteStartObject("accounting");
            writer.WriteNumber("capacity", frame.Capacity); writer.WriteNumber("totalSamples", frame.TotalSamples);
            writer.WriteNumber("acceptedCount", frame.AcceptedCount); writer.WriteNumber("evictedCount", frame.EvictedCount);
            writer.WriteNumber("remainingCount", frame.RemainingCount); writer.WriteBoolean("isPaused", frame.IsPaused);
            writer.WriteNumber("retainedCount", frame.Source.RawEntryCount); writer.WriteEndObject();
            writer.WriteStartObject("view");
            writer.WriteNumber("geometryBudget", frame.Snapshot.Options.GeometryBudget);
            writer.WriteNumber("requiredPointCount", frame.Snapshot.RequiredPointCount);
            writer.WriteString("budgetStatus", frame.Snapshot.BudgetStatus.ToString());
            Domain(writer, "fixedXDomain", frame.Snapshot.Options.FixedXDomain);
            Domain(writer, "fixedYDomain", frame.Snapshot.Options.FixedYDomain);
            Domain(writer, "xDomain", frame.Snapshot.XDomain); Domain(writer, "yDomain", frame.Snapshot.YDomain);
            writer.WriteEndObject(); writer.WriteStartArray("series");
            foreach (var series in frame.Source.Series)
            {
                writer.WriteStartObject(); writer.WriteString("id", series.Id); writer.WriteString("label", series.Label);
                writer.WriteNumber("sourceVersion", series.SourceVersion); writer.WriteStartArray("samples");
                for (var i = 0; i < series.Samples.Length; i++)
                {
                    var sample = series.Samples[i]; writer.WriteStartObject();
                    writer.WriteNumber("sourceIndex", i); writer.WriteNumber("key", sample.Key);
                    Coordinate(writer, "x", sample.X); Coordinate(writer, "y", sample.Y); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return stream.Complete();
    }

    public static byte[] SerializeCsv(ScatterLiveFrame frame, int maximumBytes = MaximumEncodedBytes)
    {
        Validate(frame, maximumBytes);
        using var stream = new BoundedExportStream(maximumBytes, default);
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true))
        {
            writer.NewLine = "\r\n";
            writer.WriteLine("record,snapshot_id,dataset_version,series_id,series_version,name,value,source_index,key,x_state,x,y_state,y");
            void Row(string record, string seriesId = "", string seriesVersion = "", string name = "", string value = "",
                string sourceIndex = "", string key = "", string xState = "", string x = "", string yState = "", string y = "")
            {
                string[] cells = [record, Text(frame.SnapshotId), Number(frame.Source.DatasetVersion), seriesId,
                    seriesVersion, name, value, sourceIndex, key, xState, x, yState, y];
                writer.WriteLine(string.Join(",", cells.Select(Quote)));
            }
            void Metadata(string name, string value) => Row("metadata", name: name, value: value);
            Metadata("schemaVersion", "1"); Metadata("scope", Text(Scope));
            Metadata("capacity", Number(frame.Capacity)); Metadata("totalSamples", Number(frame.TotalSamples));
            Metadata("acceptedCount", Number(frame.AcceptedCount)); Metadata("evictedCount", Number(frame.EvictedCount));
            Metadata("remainingCount", Number(frame.RemainingCount)); Metadata("isPaused", frame.IsPaused ? "true" : "false");
            Metadata("retainedCount", Number(frame.Source.RawEntryCount));
            Metadata("geometryBudget", Number(frame.Snapshot.Options.GeometryBudget));
            Metadata("requiredPointCount", Number(frame.Snapshot.RequiredPointCount));
            Metadata("budgetStatus", Text(frame.Snapshot.BudgetStatus.ToString()));
            void DomainRows(string name, HistoryValueDomain? domain)
            {
                Metadata(name + ".state", domain.HasValue ? "finite" : "missing");
                Metadata(name + ".minimum", Value(domain?.Minimum)); Metadata(name + ".maximum", Value(domain?.Maximum));
            }
            DomainRows("fixedXDomain", frame.Snapshot.Options.FixedXDomain); DomainRows("fixedYDomain", frame.Snapshot.Options.FixedYDomain);
            DomainRows("xDomain", frame.Snapshot.XDomain); DomainRows("yDomain", frame.Snapshot.YDomain);
            foreach (var series in frame.Source.Series)
            {
                Row("series", Text(series.Id), Number(series.SourceVersion), "label", Text(series.Label));
                for (var i = 0; i < series.Samples.Length; i++)
                {
                    var sample = series.Samples[i];
                    Row("sample", Text(series.Id), Number(series.SourceVersion), sourceIndex: Number(i), key: Number(sample.Key),
                        xState: State(sample.X), x: Value(sample.X), yState: State(sample.Y), y: Value(sample.Y));
                }
            }
        }
        return stream.Complete();
    }

    private static void Validate(ScatterLiveFrame frame, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (maximumBytes < 1 || maximumBytes > MaximumEncodedBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    }
    private static void Domain(Utf8JsonWriter writer, string name, HistoryValueDomain? domain)
    {
        if (!domain.HasValue) { writer.WriteNull(name); return; }
        writer.WriteStartObject(name); writer.WriteNumber("minimum", domain.Value.Minimum);
        writer.WriteNumber("maximum", domain.Value.Maximum); writer.WriteEndObject();
    }
    private static void Coordinate(Utf8JsonWriter writer, string name, double? value)
    {
        writer.WriteString(name + "State", State(value));
        if (value.HasValue && double.IsFinite(value.Value)) writer.WriteNumber(name, value.Value);
        else writer.WriteNull(name);
    }
    private static string State(double? value) => !value.HasValue ? "missing" : double.IsNaN(value.Value) ? "nan" :
        double.IsPositiveInfinity(value.Value) ? "positive-infinity" : double.IsNegativeInfinity(value.Value) ? "negative-infinity" : "finite";
    private static string Value(double? value) => value.HasValue && double.IsFinite(value.Value) ? value.Value.ToString("R", CultureInfo.InvariantCulture) : "";
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Text(string value) => "'" + value;
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
