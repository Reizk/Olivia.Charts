using System.Text.Json;
using Olivia.Core.Charts;

namespace Olivia.Core.Export;

public static partial class HistorySnapshotExport
{
    /// <summary>
    /// Native single-axis schema 1, UTF-8 without BOM. Every raw value, tick and domain bound has an
    /// explicit state. Nonfinite domains stay present, Int64 values stay integer, and negative zero
    /// stays a signed finite number. No presentation text is recomputed during serialization.
    /// </summary>
    public static byte[] WriteJson(HistoryExportSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new BoundedExportStream(snapshot.Limits.MaxEncodedBytes, cancellationToken);
        using var json = new Utf8JsonWriter(output);
        json.WriteStartObject();
        json.WriteNumber("schemaVersion", snapshot.SchemaVersion);
        json.WriteString("kind", snapshot.Kind);
        json.WriteString("scope", snapshot.Scope);
        json.WriteString("description", snapshot.Description);
        json.WriteNumber("sourceVersion", snapshot.SourceVersion);
        json.WriteNumber("sampleCount", snapshot.SampleCount);
        json.WriteNumber("rawStartIndex", snapshot.RawStartIndex);
        json.WriteNumber("rawEndIndexExclusive", snapshot.RawEndIndexExclusive);
        json.WriteStartObject("window");
        json.WriteNumber("startUtcTicks", snapshot.Window.StartTicks);
        json.WriteNumber("endUtcTicksExclusive", snapshot.Window.EndTicks);
        json.WriteEndObject();
        json.WriteStartObject("budget");
        json.WriteNumber("pointBudget", snapshot.PointBudget);
        json.WriteNumber("requiredPointBudget", snapshot.RequiredPointBudget);
        json.WriteNumber("pointCount", snapshot.PointCount);
        json.WriteString("status", snapshot.BudgetStatus.ToString());
        json.WriteEndObject();
        json.WriteStartObject("format");
        json.WriteString("timeZoneId", snapshot.Format.TimeZoneId);
        json.WriteString("cultureName", snapshot.Format.CultureName);
        json.WriteString("timeFormat", snapshot.Format.TimeFormat);
        json.WriteString("numberFormat", snapshot.Format.NumberFormat);
        json.WriteEndObject();
        WriteDomain(json, "rawDomain", snapshot.RawDomain);
        WriteDomain(json, "displayDomain", snapshot.DisplayDomain);
        json.Flush();

        var presentation = snapshot.Presentation;
        json.WriteStartObject("presentation");
        json.WriteBoolean("axesDrawn", presentation.AxesDrawn);
        json.WriteString("xStartLabel", presentation.XStartLabel);
        json.WriteString("xEndLabel", presentation.XEndLabel);
        json.WriteStartArray("yTicks");
        foreach (var tick in presentation.YTicks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            json.WriteStartObject();
            WriteScalar(json, tick.Value);
            json.WriteNumber("position", tick.Position);
            json.WriteString("text", tick.Text);
            json.WriteEndObject();
            json.Flush();
        }
        json.WriteEndArray();
        json.WriteString("status", presentation.Status);
        if (presentation.Selection is { } selection)
        {
            json.WriteStartObject("selection");
            WriteSample(json, selection);
            json.WriteEndObject();
        }
        else json.WriteNull("selection");
        json.WriteString("selectedDisplayText", presentation.SelectedDisplayText);
        json.WriteString("detailText", presentation.DetailText);
        json.WriteEndObject();
        json.Flush();
        json.WriteStartArray("samples");
        for (var index = 0; index < snapshot.Samples.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            json.WriteStartObject();
            json.WriteNumber("sourceIndex", snapshot.RawStartIndex + index);
            WriteSample(json, snapshot.Samples[index]);
            json.WriteEndObject();
            // Flush each bounded row so Utf8JsonWriter cannot buffer an entire unbounded export.
            json.Flush();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
        return output.Complete();
    }

    private static void WriteSample(Utf8JsonWriter json, HistorySample sample)
    {
        json.WriteNumber("key", sample.Key);
        json.WriteNumber("utcTicks", sample.UtcTicks);
        WriteScalar(json, sample.Value);
    }

    private static void WriteScalar(Utf8JsonWriter json, double? value)
    {
        json.WriteString("valueState", ValueState(value));
        if (value is { } finite && double.IsFinite(finite)) json.WriteNumber("value", finite);
        else json.WriteNull("value");
    }

    private static void WriteDomain(Utf8JsonWriter json, string name, HistoryPlotValueDomain? domain)
    {
        if (domain is not { } value) { json.WriteNull(name); return; }
        json.WriteStartObject(name);
        json.WriteStartObject("minimum");
        WriteScalar(json, value.Minimum);
        json.WriteEndObject();
        json.WriteStartObject("maximum");
        WriteScalar(json, value.Maximum);
        json.WriteEndObject();
        json.WriteEndObject();
    }
}
