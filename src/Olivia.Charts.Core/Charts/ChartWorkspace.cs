using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Olivia.Core.Charts;

/// <summary>Presentation only, addressed by an ordinal stable series identity.</summary>
public sealed class ChartWorkspaceSeries
{
    public ChartWorkspaceSeries(string id, bool visible, string palette, double markerRadius = 4)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > ScatterChartSource.MaximumTextLength ||
            !ChartWorkspaceConfiguration.IsWellFormedUtf16(id)) throw new ArgumentException("Invalid series identity.", nameof(id));
        if (palette is not ("blue" or "orange" or "green" or "purple")) throw new ArgumentException("Unknown palette token.", nameof(palette));
        if (!double.IsFinite(markerRadius) || markerRadius < 1 || markerRadius > 12)
            throw new ArgumentOutOfRangeException(nameof(markerRadius));
        Id = id; Visible = visible; Palette = palette; MarkerRadius = markerRadius;
    }
    public string Id { get; }
    public bool Visible { get; }
    public string Palette { get; }
    public double MarkerRadius { get; }
}

/// <summary>Version-one scatter presentation intent only. Null domains mean auto; null viewports
/// mean no override. A viewport overrides its axis domain. Exact series identity sets are required:
/// missing, new, renamed and duplicate identities reject atomically; labels and positions never map IDs.
/// Raw data, source identity/version, providers, code and runtime objects are not persisted.</summary>
public sealed class ChartWorkspaceConfiguration
{
    public const int CurrentVersion = 1;
    public const int MaximumJsonBytes = 32768;
    public const int MaximumJsonDepth = 8;
    public ChartWorkspaceConfiguration(IEnumerable<ChartWorkspaceSeries> series,
        HistoryValueDomain? fixedXDomain = null, HistoryValueDomain? fixedYDomain = null,
        HistoryValueDomain? viewportX = null, HistoryValueDomain? viewportY = null)
    {
        ArgumentNullException.ThrowIfNull(series);
        var builder = ImmutableArray.CreateBuilder<ChartWorkspaceSeries>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in series)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (builder.Count == ScatterChartSource.MaximumSeries) throw new ArgumentException("Series count exceeds limit.", nameof(series));
            if (!ids.Add(item.Id)) throw new ArgumentException("Duplicate series identity.", nameof(series));
            builder.Add(item);
        }
        ValidateDomain(fixedXDomain); ValidateDomain(fixedYDomain); ValidateDomain(viewportX); ValidateDomain(viewportY);
        Series = builder.ToImmutable(); FixedXDomain = fixedXDomain; FixedYDomain = fixedYDomain;
        ViewportX = viewportX; ViewportY = viewportY;
    }
    public int Version => CurrentVersion;
    public string Kind => "scatter";
    public ImmutableArray<ChartWorkspaceSeries> Series { get; }
    public HistoryValueDomain? FixedXDomain { get; }
    public HistoryValueDomain? FixedYDomain { get; }
    public HistoryValueDomain? ViewportX { get; }
    public HistoryValueDomain? ViewportY { get; }

    public static ChartWorkspaceConfiguration CreateDefault(ScatterChartSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string[] palettes = ["blue", "orange", "green", "purple"];
        return new(source.Series.Select((s, i) => new ChartWorkspaceSeries(s.Id, true, palettes[i % palettes.Length])));
    }

    /// <summary>Pure prepare. Original immutable series and raw pairs are reused, never rewritten.
    /// Visible series retain original source order; callers map styles by Hit.SeriesId, not position.</summary>
    public ScatterChartSnapshot Query(ScatterChartSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Series.Length != Series.Length) throw new ArgumentException("Series identity sets must match exactly.", nameof(source));
        var byId = Series.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var series in source.Series)
            if (!byId.ContainsKey(series.Id)) throw new ArgumentException("Series identity sets must match exactly.", nameof(source));
        var visible = new ScatterChartSource(source.Series.Where(s => byId[s.Id].Visible), source.DatasetVersion);
        return visible.Query(new ScatterChartOptions(fixedXDomain: ViewportX ?? FixedXDomain, fixedYDomain: ViewportY ?? FixedYDomain));
    }

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", Version); writer.WriteString("kind", Kind);
            WriteDomain(writer, "fixedXDomain", FixedXDomain); WriteDomain(writer, "fixedYDomain", FixedYDomain);
            WriteDomain(writer, "viewportX", ViewportX); WriteDomain(writer, "viewportY", ViewportY);
            writer.WriteStartArray("series");
            foreach (var series in Series)
            {
                writer.WriteStartObject(); writer.WriteString("id", series.Id); writer.WriteBoolean("visible", series.Visible);
                writer.WriteString("palette", series.Palette); writer.WriteNumber("markerRadius", series.MarkerRadius); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        if (stream.Length > MaximumJsonBytes) throw new InvalidOperationException("Configuration exceeds byte bound.");
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Strict own JSON codec: every field is required; unknown and duplicate fields fail.
    /// No serializer type activation or permissive partial restore occurs. Errors are FormatException.</summary>
    public static ChartWorkspaceConfiguration Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumJsonBytes || !IsWellFormedUtf16(json) || Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new FormatException("Configuration exceeds text bounds or contains invalid UTF-16.");
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumJsonDepth });
            var root = Fields(doc.RootElement, "version", "kind", "fixedXDomain", "fixedYDomain", "viewportX", "viewportY", "series");
            if (root["version"].ValueKind != JsonValueKind.Number || !root["version"].TryGetInt32(out var version) || version != CurrentVersion)
                throw new FormatException("Unsupported configuration version.");
            if (root["kind"].ValueKind != JsonValueKind.String || root["kind"].GetString() != "scatter")
                throw new FormatException("Unsupported chart kind.");
            var array = root["series"];
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > ScatterChartSource.MaximumSeries)
                throw new FormatException("Invalid series array.");
            var series = new List<ChartWorkspaceSeries>();
            foreach (var item in array.EnumerateArray())
            {
                var fields = Fields(item, "id", "visible", "palette", "markerRadius");
                if (fields["id"].ValueKind != JsonValueKind.String || fields["palette"].ValueKind != JsonValueKind.String ||
                    fields["visible"].ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException("Invalid series field type.");
                series.Add(new(fields["id"].GetString()!, fields["visible"].GetBoolean(), fields["palette"].GetString()!, Number(fields["markerRadius"])));
            }
            return new(series, ReadDomain(root["fixedXDomain"]), ReadDomain(root["fixedYDomain"]),
                ReadDomain(root["viewportX"]), ReadDomain(root["viewportY"]));
        }
        catch (JsonException e) { throw new FormatException("Malformed configuration JSON.", e); }
        catch (ArgumentException e) { throw new FormatException("Invalid configuration value.", e); }
        catch (InvalidOperationException e) { throw new FormatException("Invalid configuration field type.", e); }
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException("Expected object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value))
                throw new FormatException("Unknown or duplicate field.");
        if (fields.Count != names.Length) throw new FormatException("Missing field.");
        return fields;
    }
    private static double Number(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new FormatException("Expected finite number.");
        return number;
    }
    private static HistoryValueDomain? ReadDomain(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var fields = Fields(value, "minimum", "maximum");
        return new HistoryValueDomain(Number(fields["minimum"]), Number(fields["maximum"]));
    }
    private static void WriteDomain(Utf8JsonWriter writer, string name, HistoryValueDomain? domain)
    {
        if (domain is not { } value) { writer.WriteNull(name); return; }
        writer.WriteStartObject(name); writer.WriteNumber("minimum", value.Minimum); writer.WriteNumber("maximum", value.Maximum); writer.WriteEndObject();
    }
    private static void ValidateDomain(HistoryValueDomain? domain)
    {
        if (domain is { } value && (!double.IsFinite(value.Minimum) || !double.IsFinite(value.Maximum) || value.Minimum > value.Maximum))
            throw new ArgumentException("Invalid finite domain.", nameof(domain));
    }
    internal static bool IsWellFormedUtf16(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (!char.IsHighSurrogate(text[i]) || ++i == text.Length || !char.IsLowSurrogate(text[i])) return false;
        }
        return true;
    }
}

/// <summary>An unforgeable session-owned prepared view; Source is the original raw-data reference.</summary>
public sealed class ChartWorkspacePreview
{
    internal ChartWorkspacePreview(ChartWorkspaceConfiguration configuration, ScatterChartSnapshot snapshot,
        ScatterChartSource source, long revision)
    { Configuration = configuration; Snapshot = snapshot; Source = source; Revision = revision; }
    public ChartWorkspaceConfiguration Configuration { get; }
    public ScatterChartSnapshot Snapshot { get; }
    public ScatterChartSource Source { get; }
    public long DatasetVersion => Source.DatasetVersion;
    public long Revision { get; }
}

/// <summary>Bounded synchronous, single-owner transaction. Preview does not change committed state.
/// Cancellation, replacement, disposal or a newer preview invalidates earlier tokens. Optional
/// beforeCommit runs before any commit; reentrant apply is rejected and its outer operation invalidated.
/// Callback exceptions propagate without a commit. No providers, background work or resources are owned.</summary>
public sealed class ChartWorkspaceSession : IDisposable
{
    private bool applying;
    public ChartWorkspaceSession(ScatterChartSource source, ChartWorkspaceConfiguration? initial = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var configuration = initial ?? ChartWorkspaceConfiguration.CreateDefault(source);
        var snapshot = configuration.Query(source);
        Source = source; Current = configuration; Snapshot = snapshot;
    }
    public ScatterChartSource Source { get; private set; }
    public ChartWorkspaceConfiguration Current { get; private set; }
    public ScatterChartSnapshot Snapshot { get; private set; }
    public ChartWorkspacePreview? Pending { get; private set; }
    public long Revision { get; private set; }
    public bool IsDisposed { get; private set; }
    public ChartWorkspacePreview PreviewJson(string json) => Preview(ChartWorkspaceConfiguration.Parse(json));
    public ChartWorkspacePreview Preview(ChartWorkspaceConfiguration configuration)
    {
        ThrowIfDisposed(); ArgumentNullException.ThrowIfNull(configuration);
        var snapshot = configuration.Query(Source); // All validation before any mutation.
        Advance();
        return Pending = new(configuration, snapshot, Source, Revision);
    }
    public bool Apply(ChartWorkspacePreview preview, Action<ChartWorkspacePreview>? beforeCommit = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (IsDisposed) return false;
        if (applying) { Advance(); Pending = null; return false; }
        if (!IsCurrent(preview)) return false;
        applying = true;
        try
        {
            beforeCommit?.Invoke(preview);
            if (!IsCurrent(preview)) return false;
            Advance(); Current = preview.Configuration; Snapshot = preview.Snapshot; Pending = null;
            return true;
        }
        finally { applying = false; }
    }
    public void Cancel() { ThrowIfDisposed(); Advance(); Pending = null; }
    public void ReplaceSource(ScatterChartSource source)
    {
        ThrowIfDisposed(); ArgumentNullException.ThrowIfNull(source);
        var configuration = ChartWorkspaceConfiguration.CreateDefault(source);
        var snapshot = configuration.Query(source);
        Advance(); Source = source; Current = configuration; Snapshot = snapshot; Pending = null;
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        Advance(); Pending = null; IsDisposed = true;
    }
    private bool IsCurrent(ChartWorkspacePreview preview) => !IsDisposed && ReferenceEquals(Pending, preview) &&
        preview.Revision == Revision && ReferenceEquals(preview.Source, Source) && preview.DatasetVersion == Source.DatasetVersion;
    private void Advance() { Revision = checked(Revision + 1); }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
