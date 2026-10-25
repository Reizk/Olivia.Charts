using System.Globalization;
using System.Text;
using Olivia.Core.Charts;

namespace Olivia.Core.Export;

public static partial class HistorySnapshotExport
{
    /// <summary>
    /// UTF-8, no BOM, RFC 4180 CRLF, fixed native single-axis eleven-column schema. Every present
    /// textValue has exactly one added leading apostrophe; remove that one prefix after CSV parsing.
    /// Metadata/domain/presentation properties, Y ticks, selection and raw rows have distinct kinds.
    /// Neither axis nor series identity is manufactured. Numeric cells are invariant and unprefixed.
    /// </summary>
    public static byte[] WriteCsv(HistoryExportSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new BoundedExportStream(snapshot.Limits.MaxEncodedBytes, cancellationToken);
        using var writer = new StreamWriter(output, new UTF8Encoding(false, true), 1_024, leaveOpen: true);
        var csv = new HistoryCsvRecords(writer, cancellationToken);
        writer.Write("recordKind,name,textValue,integerValue,numberValue,booleanValue,sourceIndex,key,utcTicks,valueState,value\r\n");
        csv.Record("metadata", "schemaVersion", integer: snapshot.SchemaVersion);
        csv.Record("metadata", "kind", text: snapshot.Kind);
        csv.Record("metadata", "scope", text: snapshot.Scope);
        csv.Record("metadata", "description", text: snapshot.Description);
        csv.Record("metadata", "sourceVersion", integer: snapshot.SourceVersion);
        csv.Record("metadata", "sampleCount", integer: snapshot.SampleCount);
        csv.Record("metadata", "startUtcTicks", integer: snapshot.Window.StartTicks);
        csv.Record("metadata", "endUtcTicksExclusive", integer: snapshot.Window.EndTicks);
        csv.Record("metadata", "rawStartIndex", integer: snapshot.RawStartIndex);
        csv.Record("metadata", "rawEndIndexExclusive", integer: snapshot.RawEndIndexExclusive);
        csv.Record("metadata", "pointBudget", integer: snapshot.PointBudget);
        csv.Record("metadata", "requiredPointBudget", integer: snapshot.RequiredPointBudget);
        csv.Record("metadata", "pointCount", integer: snapshot.PointCount);
        csv.Record("metadata", "budgetStatus", text: snapshot.BudgetStatus.ToString());
        csv.Record("metadata", "timeZoneId", text: snapshot.Format.TimeZoneId);
        csv.Record("metadata", "cultureName", text: snapshot.Format.CultureName);
        csv.Record("metadata", "timeFormat", text: snapshot.Format.TimeFormat);
        csv.Record("metadata", "numberFormat", text: snapshot.Format.NumberFormat);
        WriteCsvDomain(csv, "rawDomain", snapshot.RawDomain);
        WriteCsvDomain(csv, "displayDomain", snapshot.DisplayDomain);
        var presentation = snapshot.Presentation;
        csv.Record("presentation", "axesDrawn", boolean: presentation.AxesDrawn);
        csv.Record("presentation", "xStartLabel", text: presentation.XStartLabel);
        csv.Record("presentation", "xEndLabel", text: presentation.XEndLabel);
        csv.Record("presentation", "status", text: presentation.Status);
        csv.Record("presentation", "selectedDisplayText", text: presentation.SelectedDisplayText);
        csv.Record("presentation", "detailText", text: presentation.DetailText);
        csv.Record("presentation", "selectionPresent", boolean: presentation.Selection.HasValue);
        for (var index = 0; index < presentation.YTicks.Length; index++)
        {
            var tick = presentation.YTicks[index];
            csv.Record("yTick", text: tick.Text, integer: index, number: tick.Position,
                state: ValueState(tick.Value), value: Finite(tick.Value));
        }
        if (presentation.Selection is { } selection)
            csv.Sample("selection", null, selection);
        for (var index = 0; index < snapshot.Samples.Length; index++)
            csv.Sample("sample", snapshot.RawStartIndex + index, snapshot.Samples[index]);
        writer.Flush();
        return output.Complete();
    }

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;

    private static void WriteCsvDomain(HistoryCsvRecords csv, string name, HistoryPlotValueDomain? domain)
    {
        csv.Record("domain", name + "Present", boolean: domain.HasValue);
        if (domain is not { } value) return;
        csv.Record("domain", name + "Minimum", state: ValueState(value.Minimum), value: Finite(value.Minimum));
        csv.Record("domain", name + "Maximum", state: ValueState(value.Maximum), value: Finite(value.Maximum));
    }

    // Small original native-schema writer; the established dual-axis CSV writer remains untouched.
    private sealed class HistoryCsvRecords(StreamWriter writer, CancellationToken token)
    {
        public void Sample(string kind, int? sourceIndex, HistorySample sample) =>
            Record(kind, sourceIndex: sourceIndex, key: sample.Key, ticks: sample.UtcTicks,
                state: ValueState(sample.Value), value: Finite(sample.Value));

        public void Record(string kind, string? name = null, string? text = null, long? integer = null,
            double? number = null, bool? boolean = null, int? sourceIndex = null, long? key = null,
            long? ticks = null, string? state = null, double? value = null)
        {
            token.ThrowIfCancellationRequested();
            writer.Write(kind);
            Token(name);
            Text(text);
            Token(integer?.ToString(CultureInfo.InvariantCulture));
            Token(number?.ToString("R", CultureInfo.InvariantCulture));
            Token(boolean.HasValue ? (boolean.Value ? "true" : "false") : null);
            Token(sourceIndex?.ToString(CultureInfo.InvariantCulture));
            Token(key?.ToString(CultureInfo.InvariantCulture));
            Token(ticks?.ToString(CultureInfo.InvariantCulture));
            Token(state);
            Token(value?.ToString("R", CultureInfo.InvariantCulture));
            writer.Write("\r\n");
        }

        private void Token(string? value) { writer.Write(','); writer.Write(value); }

        private void Text(string? value)
        {
            writer.Write(',');
            if (value is null) return;
            writer.Write("\"'");
            foreach (var character in value)
            {
                if (character == '"') writer.Write('"');
                writer.Write(character);
            }
            writer.Write('"');
        }
    }
}
