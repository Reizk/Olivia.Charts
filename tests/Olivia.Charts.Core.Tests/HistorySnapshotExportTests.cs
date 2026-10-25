using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Olivia.Core.Charts;
using Olivia.Core.Display;
using Olivia.Core.Export;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class HistorySnapshotExportTests
{
    [Fact]
    public void Raw_half_open_rows_duplicate_timestamps_and_original_indices_are_not_representatives()
    {
        var source = new HistorySeries([
            new(90, 5, 90), new(12, 10, 42), new(11, 10, 1), new(13, 15, null), new(14, 20, 9)], 17);
        var displayed = source.Query(new TimeWindow(10, 20), 0);
        var geometry = HistoryPlotGeometry.Create(displayed);
        var copy = HistorySnapshotExport.Capture(source, displayed, geometry, ValueDisplayFormat.Default,
            Labels(geometry) with { Status = "Budget zero: showing version 17" });
        Assert.Equal(17, copy.SourceVersion);
        Assert.Equal(new TimeWindow(10, 20), copy.Window);
        Assert.Equal(1, copy.RawStartIndex);
        Assert.Equal(4, copy.RawEndIndexExclusive);
        Assert.Equal(3, copy.SampleCount);
        Assert.Equal(new long[] { 11, 12, 13 }, copy.Samples.Select(row => row.Key));
        Assert.Equal(new long[] { 10, 10, 15 }, copy.Samples.Select(row => row.UtcTicks));
        Assert.Equal(new double?[] { 1, 42, null }, copy.Samples.Select(row => row.Value));
        Assert.Equal(0, copy.PointBudget);
        Assert.Equal(2, copy.RequiredPointBudget);
        Assert.Equal(0, copy.PointCount);
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, copy.BudgetStatus);
        Assert.False(copy.Presentation.AxesDrawn);
        Assert.Empty(copy.Presentation.YTicks);
        Assert.Empty(copy.Presentation.XStartLabel);
        Assert.Empty(copy.Presentation.XEndLabel);
        Assert.Empty(copy.Presentation.DetailText);
        Assert.Equal("Budget zero: showing version 17", copy.Presentation.Status);
        using var json = JsonDocument.Parse(HistorySnapshotExport.WriteJson(copy));
        Assert.Equal(new[] { 1, 2, 3 }, json.RootElement.GetProperty("samples").EnumerateArray()
            .Select(row => row.GetProperty("sourceIndex").GetInt32()));
    }

    [Fact]
    public void Captured_version17_values_and_supplied_labels_survive_version18_and_formatter_replacement()
    {
        var source = new HistorySeries(Enumerable.Range(0, 20).Select(i => new HistorySample(i, i, i)), 17);
        var displayed = source.Query(new TimeWindow(3, 17), 2);
        var geometry = HistoryPlotGeometry.Create(displayed);
        var presentation = Labels(geometry, source.Samples[7]) with
        {
            XStartLabel = "version 17 start +08:00", XEndLabel = "version 17 end +08:00",
            Status = "Displayed 17", SelectedDisplayText = "selected 7 = 7,000", DetailText = "selected detail 17"
        };
        var format = new ValueDisplayFormat("UTC", "de-DE", "yyyy", "F3");
        var copy = HistorySnapshotExport.Capture(source, displayed, geometry, format, presentation);
        source = new HistorySeries([new(1000, 4, 42)], 18);
        displayed = source.Query(new TimeWindow(0, 6), 10);
        geometry = HistoryPlotGeometry.Create(displayed);
        format = new ValueDisplayFormat("UTC", "en-US", "HH:mm", "F0");
        presentation = Labels(geometry) with { XStartLabel = "version 18 start", Status = "Displayed 18" };
        Assert.Equal(18, displayed.SourceVersion);
        Assert.Equal("en-US", format.CultureName);
        Assert.Equal("Displayed 18", presentation.Status);
        Assert.Equal(17, copy.SourceVersion);
        Assert.Equal(14, copy.SampleCount);
        Assert.Equal(2, copy.PointCount);
        Assert.Equal(new long[] { 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, copy.Samples.Select(row => row.Key));
        Assert.Equal("de-DE", copy.Format.CultureName);
        Assert.Equal("F3", copy.Format.NumberFormat);
        Assert.Equal("version 17 start +08:00", copy.Presentation.XStartLabel);
        Assert.Equal("version 17 end +08:00", copy.Presentation.XEndLabel);
        Assert.Equal("Displayed 17", copy.Presentation.Status);
        Assert.Equal("selected 7 = 7,000", copy.Presentation.SelectedDisplayText);
        Assert.Equal("selected detail 17", copy.Presentation.DetailText);
        Assert.Equal(new HistorySample(7, 7, 7), copy.Presentation.Selection);
        Assert.Equal(new[] { "tick 0", "tick 1", "tick 2", "tick 3", "tick 4" }, copy.Presentation.YTicks.Select(tick => tick.Text));
        AssertNoSourceGraph(copy, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    [Fact]
    public void Capture_copies_arrays_and_does_not_retain_source_snapshot_geometry_formatter_or_presentation()
    {
        var (copy, references) = WeakCapture();
        for (var attempt = 0; attempt < 3 && references.Any(reference => reference.IsAlive); attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Assert.All(references, reference => Assert.False(reference.IsAlive));
        Assert.Equal(2, copy.SampleCount);
        Assert.NotEmpty(HistorySnapshotExport.WriteJson(copy));
        Assert.NotEmpty(HistorySnapshotExport.WriteCsv(copy));
        GC.KeepAlive(copy);
    }

    [Fact]
    public void Json_literal_identity_states_negative_zero_and_maximum_UTC_are_exact()
    {
        var source = new HistorySeries([
            new(long.MinValue, 3155378975999999991L, null),
            new(long.MinValue + 1, 3155378975999999992L, double.NaN),
            new(long.MinValue + 2, 3155378975999999993L, double.PositiveInfinity),
            new(long.MinValue + 3, 3155378975999999994L, double.NegativeInfinity),
            new(long.MinValue + 4, 3155378975999999995L, -0d),
            new(long.MinValue + 5, 3155378975999999996L, double.Epsilon),
            new(long.MinValue + 6, 3155378975999999997L, double.MaxValue),
            new(long.MinValue + 7, 3155378975999999998L, double.MinValue),
            new(long.MaxValue, 3155378975999999999L, 1.2345678901234567)], long.MaxValue);
        var displayed = source.Query(new TimeWindow(3155378975999999991L, 3155378976000000000L), 20);
        var geometry = HistoryPlotGeometry.Create(displayed);
        var copy = HistorySnapshotExport.Capture(source, displayed, geometry,
            new ValueDisplayFormat("UTC", "zh-CN", "yyyy", "R"), Labels(geometry));
        var bytes = HistorySnapshotExport.WriteJson(copy);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("single-axis-history", root.GetProperty("kind").GetString());
        Assert.Equal("visible-window-raw", root.GetProperty("scope").GetString());
        Assert.Equal(long.MaxValue, root.GetProperty("sourceVersion").GetInt64());
        Assert.Equal(3155378976000000000L, root.GetProperty("window").GetProperty("endUtcTicksExclusive").GetInt64());
        Assert.Equal("zh-CN", root.GetProperty("format").GetProperty("cultureName").GetString());
        Assert.False(root.TryGetProperty("axes", out _));
        Assert.False(root.TryGetProperty("series", out _));
        Assert.False(root.TryGetProperty("datasetVersion", out _));
        var rows = root.GetProperty("samples").EnumerateArray().ToArray();
        Assert.Equal(new[] { "Null", "NaN", "PositiveInfinity", "NegativeInfinity", "Finite", "Finite", "Finite", "Finite", "Finite" },
            rows.Select(row => row.GetProperty("valueState").GetString()));
        Assert.Equal(new long[] { long.MinValue, long.MinValue + 1, long.MinValue + 2, long.MinValue + 3,
            long.MinValue + 4, long.MinValue + 5, long.MinValue + 6, long.MinValue + 7, long.MaxValue },
            rows.Select(row => row.GetProperty("key").GetInt64()));
        Assert.Equal(new long[] { 3155378975999999991L, 3155378975999999992L, 3155378975999999993L,
            3155378975999999994L, 3155378975999999995L, 3155378975999999996L, 3155378975999999997L,
            3155378975999999998L, 3155378975999999999L }, rows.Select(row => row.GetProperty("utcTicks").GetInt64()));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, rows.Select(row => row.GetProperty("sourceIndex").GetInt32()));
        Assert.All(rows.Take(4), row => Assert.Equal(JsonValueKind.Null, row.GetProperty("value").ValueKind));
        Assert.Equal("-0", rows[4].GetProperty("value").GetRawText());
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(rows[4].GetProperty("value").GetDouble()));
        Assert.Equal(1L, BitConverter.DoubleToInt64Bits(rows[5].GetProperty("value").GetDouble()));
        Assert.Equal(double.MaxValue, rows[6].GetProperty("value").GetDouble());
        Assert.Equal(double.MinValue, rows[7].GetProperty("value").GetDouble());
        Assert.Equal(1.2345678901234567, rows[8].GetProperty("value").GetDouble());
    }

    [Fact]
    public void Nonfinite_geometry_domains_and_ticks_remain_typed_present_values_with_actual_text()
    {
        const double magnitude = 1.7597120767402753e308;
        var source = new HistorySeries([new(1, 1, -magnitude), new(2, 2, magnitude)], 17);
        var displayed = source.Query(new TimeWindow(0, 3), 2);
        var geometry = HistoryPlotGeometry.Create(displayed);
        var labels = Labels(geometry) with { YTicks = geometry.Ticks.Select((tick, index) =>
            new HistoryExportTick(tick.Value, tick.Position, index == 0 ? "— actual gap" : "actual " + index)).ToImmutableArray() };
        var copy = HistorySnapshotExport.Capture(source, displayed, geometry, ValueDisplayFormat.Default, labels);
        Assert.True(double.IsNegativeInfinity(copy.DisplayDomain!.Value.Minimum));
        Assert.True(double.IsPositiveInfinity(copy.DisplayDomain.Value.Maximum));
        using var json = JsonDocument.Parse(HistorySnapshotExport.WriteJson(copy));
        var root = json.RootElement;
        Assert.Equal(-magnitude, root.GetProperty("rawDomain").GetProperty("minimum").GetProperty("value").GetDouble());
        Assert.Equal("NegativeInfinity", root.GetProperty("displayDomain").GetProperty("minimum").GetProperty("valueState").GetString());
        Assert.Equal("PositiveInfinity", root.GetProperty("displayDomain").GetProperty("maximum").GetProperty("valueState").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("displayDomain").GetProperty("maximum").GetProperty("value").ValueKind);
        var firstTick = root.GetProperty("presentation").GetProperty("yTicks")[0];
        Assert.Equal("PositiveInfinity", firstTick.GetProperty("valueState").GetString());
        Assert.Equal("— actual gap", firstTick.GetProperty("text").GetString());
        Assert.Equal(1d, firstTick.GetProperty("position").GetDouble());
        var rows = ParseCsv(HistorySnapshotExport.WriteCsv(copy));
        Assert.Equal("true", Property(rows, "domain", "displayDomainPresent")[5]);
        Assert.Equal("NegativeInfinity", Property(rows, "domain", "displayDomainMinimum")[9]);
        Assert.Equal("PositiveInfinity", Property(rows, "domain", "displayDomainMaximum")[9]);
        Assert.Equal("", Property(rows, "domain", "displayDomainMaximum")[10]);
        var tickRow = rows.First(row => row[0] == "yTick");
        Assert.Equal("'— actual gap", tickRow[2]);
        Assert.Equal("0", tickRow[3]);
        Assert.Equal("1", tickRow[4]);
        Assert.Equal("PositiveInfinity", tickRow[9]);
        Assert.Equal("", tickRow[10]);
    }

    [Theory]
    [InlineData("=SUM(1,2)")]
    [InlineData("+cmd|' /C calc'!A0")]
    [InlineData("-1+2")]
    [InlineData("@SUM(A1)")]
    [InlineData(" \t\r\n=1+1")]
    [InlineData("\u2028\u2029 =1+1")]
    [InlineData("'already prefixed")]
    [InlineData("\"quote\",comma\r\nnewline 🌡️")]
    public void Csv_every_actual_text_field_is_reversibly_prefixed_once(string hostile)
    {
        var source = new HistorySeries([new(long.MinValue, 1, -1.25)], 17);
        var displayed = source.Query(new TimeWindow(0, 2), 1);
        var geometry = HistoryPlotGeometry.Create(displayed);
        var labels = new HistoryExportPresentation(true, hostile, hostile,
            geometry.Ticks.Select(tick => new HistoryExportTick(tick.Value, tick.Position, hostile)).ToImmutableArray(),
            hostile, source.Samples[0], hostile, hostile);
        var copy = HistorySnapshotExport.Capture(source, displayed, geometry,
            new ValueDisplayFormat("UTC", "", "'@danger'yyyy", "'@danger'0.0"), labels);
        var rows = ParseCsv(HistorySnapshotExport.WriteCsv(copy));
        Assert.Equal(new[] { "recordKind", "name", "textValue", "integerValue", "numberValue", "booleanValue",
            "sourceIndex", "key", "utcTicks", "valueState", "value" }, rows[0]);
        Assert.All(rows, row => Assert.Equal(11, row.Length));
        foreach (var row in rows.Skip(1).Where(row => row[2].Length > 0)) Assert.StartsWith("'", row[2]);
        foreach (var name in new[] { "xStartLabel", "xEndLabel", "status", "selectedDisplayText", "detailText" })
            Assert.Equal(hostile, Property(rows, "presentation", name)[2][1..]);
        Assert.All(rows.Where(row => row[0] == "yTick"), row => Assert.Equal(hostile, row[2][1..]));
        Assert.Equal("'", Property(rows, "metadata", "cultureName")[2]);
        Assert.Equal("''@danger'yyyy", Property(rows, "metadata", "timeFormat")[2]);
        var sample = rows.Single(row => row[0] == "sample");
        Assert.Equal("-9223372036854775808", sample[7]);
        Assert.Equal("-1.25", sample[10]);
        Assert.Equal("Finite", sample[9]);
        Assert.Equal("", rows.Single(row => row[0] == "selection")[6]);
        using var json = JsonDocument.Parse(HistorySnapshotExport.WriteJson(copy));
        Assert.Equal(hostile, json.RootElement.GetProperty("presentation").GetProperty("xStartLabel").GetString());
        Assert.Equal(hostile, json.RootElement.GetProperty("presentation").GetProperty("yTicks")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Csv_literal_rows_preserve_states_integer_precision_and_finite_bits_across_cultures()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-SA");
            var source = new HistorySeries([
                new(100, 9007199254740993L, null), new(101, 9007199254740994L, double.NaN),
                new(102, 9007199254740995L, double.PositiveInfinity), new(103, 9007199254740996L, double.NegativeInfinity),
                new(104, 9007199254740997L, -0d), new(105, 9007199254740998L, double.Epsilon),
                new(106, 9007199254740999L, 1e12 + .01), new(long.MaxValue, 9007199254741000L, -1234.5678901234)], 17);
            var displayed = source.Query(source.Extent!.Value, 20);
            var geometry = HistoryPlotGeometry.Create(displayed);
            var copy = HistorySnapshotExport.Capture(source, displayed, geometry,
                new ValueDisplayFormat("UTC", "de-DE", "yyyy", "F2"), Labels(geometry));
            var json = HistorySnapshotExport.WriteJson(copy);
            var csv = HistorySnapshotExport.WriteCsv(copy);
            var rows = ParseCsv(csv);
            var samples = rows.Where(row => row[0] == "sample").ToArray();
            Assert.Equal(new[] { "Null", "NaN", "PositiveInfinity", "NegativeInfinity", "Finite", "Finite", "Finite", "Finite" }, samples.Select(row => row[9]));
            Assert.Equal(new[] { "9007199254740993", "9007199254740994", "9007199254740995", "9007199254740996",
                "9007199254740997", "9007199254740998", "9007199254740999", "9007199254741000" }, samples.Select(row => row[8]));
            Assert.All(samples.Take(4), row => Assert.Empty(row[10]));
            Assert.Equal("-0", samples[4][10]);
            Assert.Equal(long.MinValue, Bits(samples[4][10]));
            Assert.Equal(1L, Bits(samples[5][10]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(1e12 + .01), Bits(samples[6][10]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(-1234.5678901234), Bits(samples[7][10]));
            Assert.Equal("9223372036854775807", samples[7][7]);
            Assert.Equal("'single-axis-history", Property(rows, "metadata", "kind")[2]);
            Assert.Equal("17", Property(rows, "metadata", "sourceVersion")[3]);
            Assert.Equal("'de-DE", Property(rows, "metadata", "cultureName")[2]);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(json, HistorySnapshotExport.WriteJson(copy));
            Assert.Equal(csv, HistorySnapshotExport.WriteCsv(copy));
            Assert.Equal("ar-SA", CultureInfo.CurrentUICulture.Name);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; CultureInfo.CurrentUICulture = originalUiCulture; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_maximum_window_and_all_gap_frame_do_not_invent_labels_domains_or_samples(bool gaps)
    {
        var source = gaps ? new HistorySeries([new(1, 1, null), new(2, 2, double.NaN)], 17) : new HistorySeries([], 17);
        var window = gaps ? new TimeWindow(0, 3) : new TimeWindow(3155378976000000000L, 3155378976000000000L);
        var displayed = source.Query(window, 0);
        var geometry = HistoryPlotGeometry.Create(displayed);
        var copy = HistorySnapshotExport.Capture(source, displayed, geometry, ValueDisplayFormat.Default,
            Labels(geometry), new SnapshotExportLimits(maxRows: gaps ? 2 : 0));
        Assert.Equal(gaps ? 2 : 0, copy.SampleCount);
        Assert.False(copy.Presentation.AxesDrawn);
        Assert.Null(copy.RawDomain);
        Assert.Null(copy.DisplayDomain);
        using var json = JsonDocument.Parse(HistorySnapshotExport.WriteJson(copy));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("rawDomain").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("displayDomain").ValueKind);
        Assert.Equal("", json.RootElement.GetProperty("presentation").GetProperty("xStartLabel").GetString());
        Assert.Empty(json.RootElement.GetProperty("presentation").GetProperty("yTicks").EnumerateArray());
        var rows = ParseCsv(HistorySnapshotExport.WriteCsv(copy));
        Assert.Equal(18, rows.Count(row => row[0] == "metadata"));
        Assert.Equal("false", Property(rows, "domain", "rawDomainPresent")[5]);
        Assert.DoesNotContain(rows, row => row[0] == "yTick");
        Assert.Equal(gaps ? 2 : 0, rows.Count(row => row[0] == "sample"));
    }

    [Fact]
    public void Actual_selection_outside_window_is_preserved_without_inventing_an_exported_row()
    {
        var source = new HistorySeries([new(8, 1, -0d), new(9, 10, 10), new(10, 11, 11)], 17);
        var displayed = source.Query(new TimeWindow(10, 12), 2);
        var geometry = HistoryPlotGeometry.Create(displayed);
        var copy = HistorySnapshotExport.Capture(source, displayed, geometry, ValueDisplayFormat.Default,
            Labels(geometry, source.Samples[0]) with { SelectedDisplayText = "outside selection", DetailText = "outside selection" });
        Assert.Equal(new long[] { 9, 10 }, copy.Samples.Select(sample => sample.Key));
        Assert.Equal(new HistorySample(8, 1, -0d), copy.Presentation.Selection);
        using var json = JsonDocument.Parse(HistorySnapshotExport.WriteJson(copy));
        var selection = json.RootElement.GetProperty("presentation").GetProperty("selection");
        Assert.Equal(8, selection.GetProperty("key").GetInt64());
        Assert.Equal(1, selection.GetProperty("utcTicks").GetInt64());
        Assert.Equal("-0", selection.GetProperty("value").GetRawText());
        Assert.False(selection.TryGetProperty("sourceIndex", out _));
        var row = ParseCsv(HistorySnapshotExport.WriteCsv(copy)).Single(record => record[0] == "selection");
        Assert.Equal("", row[6]);
        Assert.Equal("8", row[7]);
        Assert.Equal("-0", row[10]);
    }

    [Fact]
    public void Default_4096_raw_row_boundary_is_independent_of_display_budget_and_never_truncated()
    {
        var source = new HistorySeries(Enumerable.Range(0, 4097).Select(i => new HistorySample(i, i, null)), 17);
        var first = source.Query(new TimeWindow(0, 4096), 0);
        var firstGeometry = HistoryPlotGeometry.Create(first);
        var copy = HistorySnapshotExport.Capture(source, first, firstGeometry, ValueDisplayFormat.Default, Labels(firstGeometry));
        Assert.Equal(4096, copy.SampleCount);
        Assert.Equal(0, copy.PointCount);
        var all = source.Query(new TimeWindow(0, 4097), 0);
        var allGeometry = HistoryPlotGeometry.Create(all);
        var error = Assert.Throws<SnapshotExportLimitException>(() => HistorySnapshotExport.Capture(source, all, allGeometry,
            ValueDisplayFormat.Default, Labels(allGeometry)));
        Assert.Equal(SnapshotExportLimit.Rows, error.Limit);
        Assert.Equal(4096, error.Maximum);
        Assert.Equal(4097, HistorySnapshotExport.Capture(source, all, allGeometry, ValueDisplayFormat.Default,
            Labels(allGeometry), new SnapshotExportLimits(maxRows: 4097)).SampleCount);
    }

    [Fact]
    public void Oversize_preflight_rejects_before_raw_sample_or_tick_copy_allocation()
    {
        var source = new HistorySeries(Enumerable.Range(0, 100000).Select(i => new HistorySample(i, i, null)), 17);
        var displayed = source.Query(new TimeWindow(0, 100000), 0);
        var geometry = HistoryPlotGeometry.Create(displayed);
        var presentation = Labels(geometry) with { Status = new string('x', 1025) };
        var format = ValueDisplayFormat.Default; // Exclude one-time formatter initialization from allocation accounting.
        SnapshotExportLimitException? error = null;
        var before = GC.GetAllocatedBytesForCurrentThread();
        try { HistorySnapshotExport.Capture(source, displayed, geometry, format, presentation); }
        catch (SnapshotExportLimitException failure) { error = failure; }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotNull(error);
        Assert.Equal(SnapshotExportLimit.Rows, error.Limit);
        // A 100,000-row HistorySample[] would exceed 3 MiB. The rejected call only allocates its error.
        Assert.InRange(allocated, 0, 65536);
    }

    [Fact]
    public void Version_range_window_and_representative_mismatches_are_rejected()
    {
        var source = new HistorySeries([new(1, 1, 1), new(2, 2, 2), new(3, 3, 3)], 17);
        var displayed = source.Query(new TimeWindow(0, 4), 3);
        var geometry = HistoryPlotGeometry.Create(displayed);
        var labels = Labels(geometry);
        void Reject(HistorySeries candidate, HistoryChartSnapshot frame, HistoryPlotGeometry shape) =>
            Assert.Throws<ArgumentException>(() => HistorySnapshotExport.Capture(candidate, frame, shape, ValueDisplayFormat.Default, labels));
        Reject(new HistorySeries(source.Samples, 18), displayed, geometry);
        Reject(source, displayed, HistoryPlotGeometry.Create(source.Query(new TimeWindow(1, 3), 3)));
        Reject(source, displayed, HistoryPlotGeometry.Create(source.Query(displayed.Window, 2)));
        Reject(new HistorySeries([new(1, 1, 1), new(2, 2, 200), new(3, 3, 3)], 17), displayed, geometry);
        Reject(new HistorySeries([new(1, 1, 1), new(5, 2, 2), new(3, 3, 3)], 17), displayed, geometry);
        Reject(new HistorySeries([new(1, 1, 1), new(2, 20, 2), new(3, 30, 3)], 17), displayed, geometry);
        foreach (var (start, end) in new[] { (-1, 3), (0, 4), (2, 1), (1, 3), (0, 2) })
            Reject(source, WithRawRange(displayed, start, end), geometry);
        Reject(source, WithRawRange(displayed, 0, 3, new TimeWindow(0, 2)), geometry);
    }

    [Fact]
    public void Negative_zero_representative_or_selection_is_not_silently_replaced_by_positive_zero()
    {
        var source = new HistorySeries([new(1, 1, -0d)], 17);
        var displayed = source.Query(new TimeWindow(0, 2), 1);
        var geometry = HistoryPlotGeometry.Create(displayed);
        Assert.Throws<ArgumentException>(() => HistorySnapshotExport.Capture(new HistorySeries([new(1, 1, 0d)], 17),
            displayed, geometry, ValueDisplayFormat.Default, Labels(geometry)));
        Assert.Throws<ArgumentException>(() => HistorySnapshotExport.Capture(source, displayed, geometry,
            ValueDisplayFormat.Default, Labels(geometry, new HistorySample(1, 1, 0d))));
    }

    [Fact]
    public void Mismatched_tick_flags_selection_and_unrendered_labels_are_rejected()
    {
        var source = new HistorySeries([new(1, 1, 1)], 17);
        var displayed = source.Query(new TimeWindow(0, 2), 1);
        var geometry = HistoryPlotGeometry.Create(displayed);
        var labels = Labels(geometry);
        void Reject(HistoryExportPresentation candidate) => Assert.Throws<ArgumentException>(() =>
            HistorySnapshotExport.Capture(source, displayed, geometry, ValueDisplayFormat.Default, candidate));
        Reject(labels with { AxesDrawn = false });
        Reject(labels with { YTicks = default });
        Reject(labels with { YTicks = [] });
        Reject(labels with { YTicks = labels.YTicks.SetItem(0, labels.YTicks[0] with { Value = 42 }) });
        Reject(labels with { YTicks = labels.YTicks.SetItem(0, labels.YTicks[0] with { Position = .25 }) });
        Reject(labels with { Selection = new HistorySample(999, 1, 1), SelectedDisplayText = "not real" });
        Reject(labels with { SelectedDisplayText = "no selection" });
        var empty = source.Query(new TimeWindow(2, 2), 0);
        var emptyGeometry = HistoryPlotGeometry.Create(empty);
        foreach (var invalid in new[] { Labels(emptyGeometry) with { XStartLabel = "not drawn" },
            Labels(emptyGeometry) with { XEndLabel = "not drawn" }, Labels(emptyGeometry) with { DetailText = "not drawn" } })
            Assert.Throws<ArgumentException>(() => HistorySnapshotExport.Capture(source, empty, emptyGeometry, ValueDisplayFormat.Default, invalid));
    }

    [Fact]
    public void Text_limits_and_well_formed_UTF16_apply_to_every_supplied_presentation_field()
    {
        var source = new HistorySeries([new(1, 1, 1)], 17);
        var displayed = source.Query(new TimeWindow(0, 2), 1);
        var geometry = HistoryPlotGeometry.Create(displayed);
        var labels = Labels(geometry, source.Samples[0]);
        HistoryExportSnapshot Copy(HistoryExportPresentation presentation, SnapshotExportLimits? limits = null) =>
            HistorySnapshotExport.Capture(source, displayed, geometry, ValueDisplayFormat.Default, presentation, limits);
        Assert.Equal(1024, Copy(labels with { Status = new string('字', 1024) }).Presentation.Status.Length);
        Assert.Equal(4096, Copy(labels with { Status = new string('字', 4096) }, new SnapshotExportLimits(maxTextLength: 4096)).Presentation.Status.Length);
        foreach (var badText in new[] { new string('x', 1025), "bad\ud800", "bad\udc00" })
        {
            foreach (var invalid in new[] { labels with { XStartLabel = badText }, labels with { XEndLabel = badText },
                labels with { Status = badText }, labels with { SelectedDisplayText = badText }, labels with { DetailText = badText },
                labels with { YTicks = labels.YTicks.SetItem(0, labels.YTicks[0] with { Text = badText }) } })
            {
                if (badText.Length > 1024)
                    Assert.Equal(SnapshotExportLimit.TextLength, Assert.Throws<SnapshotExportLimitException>(() => Copy(invalid)).Limit);
                else Assert.Throws<ArgumentException>(() => Copy(invalid));
            }
        }
        Assert.Throws<SnapshotExportLimitException>(() => Copy(labels with { Status = new string('x', 4097) }, new SnapshotExportLimits(maxTextLength: 4096)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Each_encoding_obeys_exact_byte_limit_and_never_returns_truncated_output(bool csv)
    {
        var source = new HistorySeries([new(1, 1, 1)], 17);
        var displayed = source.Query(new TimeWindow(0, 2), 1);
        var geometry = HistoryPlotGeometry.Create(displayed);
        var presentation = Labels(geometry) with { Status = "中文 🌡️ \" quoted" };
        HistoryExportSnapshot Copy(int maximum) => HistorySnapshotExport.Capture(source, displayed, geometry,
            ValueDisplayFormat.Default, presentation, new SnapshotExportLimits(maxEncodedBytes: maximum));
        byte[] Write(HistoryExportSnapshot copy) => csv ? HistorySnapshotExport.WriteCsv(copy) : HistorySnapshotExport.WriteJson(copy);
        var expected = Write(Copy(4 * 1024 * 1024));
        Assert.Equal(expected, Write(Copy(expected.Length)));
        var failure = Assert.Throws<SnapshotExportLimitException>(() => Write(Copy(expected.Length - 1)));
        Assert.Equal(SnapshotExportLimit.EncodedBytes, failure.Limit);
        Assert.Equal(expected.Length - 1, failure.Maximum);
        Assert.Throws<SnapshotExportLimitException>(() => Write(Copy(1)));
        Assert.Equal(expected, Write(Copy(expected.Length)));
    }

    [Fact]
    public void Capture_and_both_encoders_honor_precancellation_without_damaging_copy()
    {
        var source = new HistorySeries([new(1, 1, 1)], 17);
        var displayed = source.Query(new TimeWindow(0, 2), 1);
        var geometry = HistoryPlotGeometry.Create(displayed);
        var labels = Labels(geometry);
        var copy = HistorySnapshotExport.Capture(source, displayed, geometry, ValueDisplayFormat.Default, labels);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => HistorySnapshotExport.Capture(source, displayed, geometry,
            ValueDisplayFormat.Default, labels, cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => HistorySnapshotExport.WriteJson(copy, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => HistorySnapshotExport.WriteCsv(copy, cancellation.Token));
        Assert.NotEmpty(HistorySnapshotExport.WriteJson(copy));
        Assert.NotEmpty(HistorySnapshotExport.WriteCsv(copy));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Private_checkpoint_cancels_the_real_copy_loop_without_publishing_partial_DTO(int cancelAfter)
    {
        // Deterministic contract probe, not evidence of an Avalonia/UI cancellation event.
        var source = new HistorySeries([new(1, 1, 1), new(2, 2, 2), new(3, 3, 3), new(4, 4, 4)], 17);
        var displayed = source.Query(new TimeWindow(0, 5), 2);
        var geometry = HistoryPlotGeometry.Create(displayed);
        using var cancellation = new CancellationTokenSource();
        var observed = new List<int>();
        Action<int> checkpoint = count => { observed.Add(count); if (count == cancelAfter) cancellation.Cancel(); };
        var core = typeof(HistorySnapshotExport).GetMethod("CaptureCore", BindingFlags.NonPublic | BindingFlags.Static)!;
        var failure = Assert.Throws<TargetInvocationException>(() => core.Invoke(null,
            [source, displayed, geometry, ValueDisplayFormat.Default, Labels(geometry), null, cancellation.Token, checkpoint]));
        Assert.IsType<OperationCanceledException>(failure.InnerException);
        Assert.Equal(cancelAfter == 2 ? new[] { 1, 2 } : new[] { 1, 2, 3, 4 }, observed);
        Assert.Equal(4, HistorySnapshotExport.Capture(source, displayed, geometry, ValueDisplayFormat.Default, Labels(geometry)).SampleCount);
    }

    [Fact]
    public void Null_arguments_remain_explicit_without_ambiguating_existing_dual_axis_API()
    {
        var source = new HistorySeries([], 17);
        var displayed = source.Query(default, 0);
        var geometry = HistoryPlotGeometry.Create(displayed);
        var labels = Labels(geometry);
        Assert.Throws<ArgumentNullException>(() => HistorySnapshotExport.Capture(null!, displayed, geometry, ValueDisplayFormat.Default, labels));
        Assert.Throws<ArgumentNullException>(() => HistorySnapshotExport.Capture(source, null!, geometry, ValueDisplayFormat.Default, labels));
        Assert.Throws<ArgumentNullException>(() => HistorySnapshotExport.Capture(source, displayed, null!, ValueDisplayFormat.Default, labels));
        Assert.Throws<ArgumentNullException>(() => HistorySnapshotExport.Capture(source, displayed, geometry, null!, labels));
        Assert.Throws<ArgumentNullException>(() => HistorySnapshotExport.Capture(source, displayed, geometry, ValueDisplayFormat.Default, null!));
        Assert.Throws<ArgumentNullException>(() => HistorySnapshotExport.WriteJson(null!));
        Assert.Throws<ArgumentNullException>(() => HistorySnapshotExport.WriteCsv(null!));
    }

    private static HistoryExportPresentation Labels(HistoryPlotGeometry geometry, HistorySample? selection = null) =>
        new(!geometry.Nodes.IsEmpty, geometry.Nodes.IsEmpty ? "" : "actual start 17", geometry.Nodes.IsEmpty ? "" : "actual end 17",
            geometry.Ticks.Select((tick, index) => new HistoryExportTick(tick.Value, tick.Position,
                "tick " + index.ToString(CultureInfo.InvariantCulture))).ToImmutableArray(), "actual status 17", selection,
            selection.HasValue ? "actual selected 17" : "", geometry.Nodes.IsEmpty ? "" : "actual detail 17");

    private static HistoryChartSnapshot WithRawRange(HistoryChartSnapshot frame, int start, int end, TimeWindow? window = null) =>
        (HistoryChartSnapshot)typeof(HistoryChartSnapshot).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke([frame.SourceVersion, window ?? frame.Window, frame.PointBudget, frame.RequiredPointBudget, start, end,
                frame.BudgetStatus, frame.Segments, frame.Gaps]);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (HistoryExportSnapshot, WeakReference[]) WeakCapture()
    {
        var source = new HistorySeries([new(1, 1, 1), new(2, 2, 2)], 17);
        var displayed = source.Query(new TimeWindow(0, 3), 2);
        var geometry = HistoryPlotGeometry.Create(displayed);
        var format = new ValueDisplayFormat("UTC", "en-US", "yyyy", "R");
        var presentation = Labels(geometry);
        var copy = HistorySnapshotExport.Capture(source, displayed, geometry, format, presentation);
        var rawArray = ImmutableCollectionsMarshal.AsArray(source.Samples)!;
        var tickArray = ImmutableCollectionsMarshal.AsArray(presentation.YTicks)!;
        Assert.NotSame(rawArray, ImmutableCollectionsMarshal.AsArray(copy.Samples));
        Assert.NotSame(tickArray, ImmutableCollectionsMarshal.AsArray(copy.Presentation.YTicks));
        return (copy, [new(source), new(displayed), new(geometry), new(format), new(presentation), new(rawArray), new(tickArray)]);
    }

    private static void AssertNoSourceGraph(object value, HashSet<object> visited)
    {
        if (!visited.Add(value)) return;
        Assert.False(value is HistorySeries or HistoryChartSnapshot or HistoryPlotGeometry or ValueDisplayFormat or
            DualAxisChartSnapshot or DualAxisHistorySource or Delegate or Task, "Captured export retained " + value.GetType().Name);
        if (value is string || value.GetType().IsPrimitive || value.GetType().IsEnum) return;
        if (value is IEnumerable collection)
        {
            foreach (var item in collection) if (item is not null) AssertNoSourceGraph(item, visited);
            return;
        }
        foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (field.GetValue(value) is { } child) AssertNoSourceGraph(child, visited);
    }

    private static long Bits(string text) => BitConverter.DoubleToInt64Bits(double.Parse(text, CultureInfo.InvariantCulture));
    private static string[] Property(List<string[]> rows, string kind, string name) => rows.Single(row => row[0] == kind && row[1] == name);

    // Independent character-level RFC 4180 reader: no production CSV helper or Split(',').
    private static List<string[]> ParseCsv(byte[] bytes)
    {
        var input = new UTF8Encoding(false, true).GetString(bytes);
        Assert.False(input.StartsWith('\ufeff'));
        var rows = new List<string[]>();
        var fields = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        var closed = false;
        for (var index = 0; index < input.Length; index++)
        {
            var character = input[index];
            if (quoted)
            {
                if (character != '"') value.Append(character);
                else if (index + 1 < input.Length && input[index + 1] == '"') { value.Append('"'); index++; }
                else { quoted = false; closed = true; }
            }
            else if (character == '"') { Assert.False(closed); Assert.Equal(0, value.Length); quoted = true; }
            else if (character is ',' or '\r')
            {
                fields.Add(value.ToString()); value.Clear(); closed = false;
                if (character == ',') continue;
                Assert.True(index + 1 < input.Length && input[++index] == '\n');
                rows.Add(fields.ToArray()); fields.Clear();
            }
            else { Assert.False(closed); Assert.NotEqual('\n', character); value.Append(character); }
        }
        Assert.False(quoted); Assert.False(closed); Assert.Equal(0, value.Length); Assert.Empty(fields);
        Assert.All(rows, row => Assert.Equal(11, row.Length));
        return rows;
    }
}
