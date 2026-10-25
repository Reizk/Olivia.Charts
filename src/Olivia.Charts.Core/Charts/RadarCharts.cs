using System.Collections.Immutable;
using System.Globalization;

namespace Olivia.Core.Charts;

/// <summary>An ordered categorical axis with stable identity and a display-only label.</summary>
public sealed record RadarAxis
{
    public RadarAxis(string id, string label)
    {
        BarChartSource.ValidateText(id, nameof(id), identity: true);
        BarChartSource.ValidateText(label, nameof(label));
        Id = id; Label = label;
    }
    public string Id { get; }
    public string Label { get; }
}

/// <summary>Immutable raw values in the source's declared axis order. Null is missing;
/// signed finite values are retained even when outside the displayed domain.</summary>
public sealed class RadarSeries
{
    public RadarSeries(string id, string label, IEnumerable<double?> values)
    {
        BarChartSource.ValidateText(id, nameof(id), identity: true);
        BarChartSource.ValidateText(label, nameof(label));
        var copy = BarChartSource.CopyBounded(values, RadarSource.MaximumAxes, nameof(values));
        foreach (var value in copy)
            if (value is { } number && (!double.IsFinite(number) || Math.Abs(number) > RadarSource.MaximumMagnitude))
                throw new ArgumentOutOfRangeException(nameof(values), "Values must be null or finite within +/-1,000,000.");
        Id = id; Label = label; Values = copy;
    }
    public string Id { get; }
    public string Label { get; }
    public ImmutableArray<double?> Values { get; }
}

/// <summary>Original bounded categorical radar data. Publication copies at most the admission
/// limit plus one input item, preserves caller order, and compares IDs ordinally.</summary>
public sealed class RadarSource
{
    public const int MinimumAxes = 3;
    public const int MaximumAxes = 12;
    public const int MaximumSeries = 8;
    public const double MaximumMagnitude = 1_000_000;
    private readonly ImmutableDictionary<string, int> _axisIndices;
    private readonly ImmutableDictionary<string, int> _seriesIndices;

    public RadarSource(IEnumerable<RadarAxis> axes, IEnumerable<RadarSeries> series, long version = 0)
    {
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        var axisCopy = BarChartSource.CopyBounded(axes, MaximumAxes, nameof(axes));
        if (axisCopy.Length < MinimumAxes) throw new ArgumentException("Expected 3-12 axes.", nameof(axes));
        var seriesCopy = BarChartSource.CopyBounded(series, MaximumSeries, nameof(series));
        var axisIndices = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < axisCopy.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(axisCopy[i], nameof(axes));
            if (!axisIndices.TryAdd(axisCopy[i].Id, i)) throw new ArgumentException("Duplicate axis identifier.", nameof(axes));
        }
        var seriesIndices = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < seriesCopy.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(seriesCopy[i], nameof(series));
            if (!seriesIndices.TryAdd(seriesCopy[i].Id, i)) throw new ArgumentException("Duplicate series identifier.", nameof(series));
            if (seriesCopy[i].Values.Length != axisCopy.Length)
                throw new ArgumentException("Each series needs exactly one value per declared axis.", nameof(series));
        }
        Axes = axisCopy; Series = seriesCopy; Version = version;
        _axisIndices = axisIndices.ToImmutable(); _seriesIndices = seriesIndices.ToImmutable();
    }
    public ImmutableArray<RadarAxis> Axes { get; }
    public ImmutableArray<RadarSeries> Series { get; }
    public long Version { get; }

    internal bool ContainsSeries(string id) => _seriesIndices.ContainsKey(id);
    internal bool Contains(RadarKey key) => key.SeriesId is not null && key.AxisId is not null &&
        _seriesIndices.ContainsKey(key.SeriesId) && _axisIndices.ContainsKey(key.AxisId);
    internal RadarSeries FindSeries(string id) => Series[_seriesIndices[id]];
    internal RadarAxis FindAxis(string id) => Axes[_axisIndices[id]];
    internal double? Value(RadarKey key) => FindSeries(key.SeriesId).Values[_axisIndices[key.AxisId]];
}

public readonly record struct RadarKey(string SeriesId, string AxisId);
/// <summary>Normalized coordinates: center (0,0), radius 1, +X right and +Y down.</summary>
public readonly record struct RadarPoint(RadarKey Key, double X, double Y, double Value);
public readonly record struct RadarEdge(RadarPoint Start, RadarPoint End);

/// <summary>Immutable lines and markers. Points retain series order then axis order;
/// edges connect only adjacent declared axes, including the final-to-first adjacency.</summary>
public sealed class RadarGeometry
{
    private RadarGeometry(ImmutableArray<RadarPoint> points, ImmutableArray<RadarEdge> edges,
        ImmutableArray<double> ticks)
    {
        Points = points; Edges = edges; Ticks = ticks;
    }
    public ImmutableArray<RadarPoint> Points { get; }
    public ImmutableArray<RadarEdge> Edges { get; }
    public ImmutableArray<double> Ticks { get; }

    internal static RadarGeometry Create(RadarSource source, double maximum, ImmutableHashSet<string> hiddenSeries)
    {
        var points = ImmutableArray.CreateBuilder<RadarPoint>();
        var edges = ImmutableArray.CreateBuilder<RadarEdge>();
        foreach (var series in source.Series)
        {
            if (hiddenSeries.Contains(series.Id)) continue;
            var adjacent = new RadarPoint?[source.Axes.Length];
            for (var axis = 0; axis < source.Axes.Length; axis++)
            {
                if (series.Values[axis] is not { } value || value < 0 || value > maximum) continue;
                var angle = Math.Tau * axis / source.Axes.Length;
                var radius = value / maximum;
                var point = new RadarPoint(new(series.Id, source.Axes[axis].Id),
                    value == 0 ? 0 : Math.Sin(angle) * radius,
                    value == 0 ? 0 : -Math.Cos(angle) * radius, value);
                adjacent[axis] = point; points.Add(point);
            }
            for (var axis = 0; axis < adjacent.Length; axis++)
                if (adjacent[axis] is { } start && adjacent[(axis + 1) % adjacent.Length] is { } end)
                    edges.Add(new(start, end));
        }
        var ticks = ImmutableArray.CreateBuilder<double>(6);
        for (var i = 0; i <= 5; i++) ticks.Add(maximum * (i / 5d));
        return new(points.ToImmutable(), edges.ToImmutable(), ticks.ToImmutable());
    }
}

/// <summary>Owner-thread radar interaction. Domain and visibility never change raw identities;
/// invalid updates fail before publication. Close permanently disconnects notifications.</summary>
public sealed class RadarSession : IDisposable
{
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private bool _changing;

    public RadarSession(RadarSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source; Geometry = RadarGeometry.Create(source, Maximum, HiddenSeries);
    }
    public RadarSource Source { get; private set; }
    public double Maximum { get; private set; } = 100;
    public ImmutableHashSet<string> HiddenSeries { get; private set; } = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    public RadarKey? SelectedKey { get; private set; }
    public double? SelectedValue => SelectedKey is { } key ? Source.Value(key) : null;
    public RadarGeometry Geometry { get; private set; }
    public bool IsClosed { get; private set; }
    public int CallbackFailureCount { get; private set; }
    public event EventHandler? Changed;

    public string SelectionDescription
    {
        get
        {
            if (SelectedKey is not { } key) return "No selection.";
            var series = Source.FindSeries(key.SeriesId);
            var axis = Source.FindAxis(key.AxisId);
            var value = Source.Value(key);
            var state = value is null ? "missing" : value < 0 || value > Maximum
                ? $"outside [0, {Maximum.ToString("R", CultureInfo.InvariantCulture)}]" : "in domain";
            if (HiddenSeries.Contains(key.SeriesId)) state += "; hidden";
            var raw = value?.ToString("R", CultureInfo.InvariantCulture) ?? "null";
            return $"{series.Label} ({series.Id}), {axis.Label} ({axis.Id}); raw {raw}; source version {Source.Version.ToString(CultureInfo.InvariantCulture)}; {state}.";
        }
    }

    public bool SetMaximum(double maximum)
    {
        Verify();
        if (IsClosed || _changing) return false;
        if (!double.IsFinite(maximum) || maximum <= 0 || maximum > RadarSource.MaximumMagnitude)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        if (maximum == Maximum) return false;
        var geometry = RadarGeometry.Create(Source, maximum, HiddenSeries);
        return Change(() => { Maximum = maximum; Geometry = geometry; });
    }

    public bool SetVisible(string id, bool visible)
    {
        Verify();
        if (IsClosed || _changing) return false;
        BarChartSource.ValidateText(id, nameof(id), identity: true);
        if (!Source.ContainsSeries(id)) throw new ArgumentException("Unknown series identifier.", nameof(id));
        var hidden = visible ? HiddenSeries.Remove(id) : HiddenSeries.Add(id);
        if (ReferenceEquals(hidden, HiddenSeries)) return false;
        var geometry = RadarGeometry.Create(Source, Maximum, hidden);
        return Change(() => { HiddenSeries = hidden; Geometry = geometry; });
    }

    public bool Select(RadarKey key)
    {
        Verify();
        if (IsClosed || _changing) return false;
        if (!Source.Contains(key)) throw new ArgumentException("Unknown series or axis identifier.", nameof(key));
        if (SelectedKey == key) return false;
        return Change(() => SelectedKey = key);
    }

    public bool ClearSelection()
    {
        Verify();
        return SelectedKey is not null && Change(() => SelectedKey = null);
    }

    public bool ReplaceSource(RadarSource source)
    {
        Verify();
        if (IsClosed || _changing) return false;
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(source, Source)) return false;
        var hidden = HiddenSeries.Where(source.ContainsSeries).ToImmutableHashSet(StringComparer.Ordinal);
        var selected = SelectedKey is { } key && source.Contains(key) ? SelectedKey : null;
        var geometry = RadarGeometry.Create(source, Maximum, hidden);
        return Change(() => { Source = source; HiddenSeries = hidden; SelectedKey = selected; Geometry = geometry; });
    }

    private bool Change(Action mutation)
    {
        if (IsClosed || _changing) return false;
        _changing = true;
        try
        {
            mutation();
            if (Changed is { } handlers)
                foreach (EventHandler handler in handlers.GetInvocationList())
                {
                    if (IsClosed) break;
                    try { handler(this, EventArgs.Empty); } catch { CallbackFailureCount++; }
                }
            return true;
        }
        finally { _changing = false; }
    }

    public void Close()
    {
        Verify();
        if (IsClosed) return;
        IsClosed = true; SelectedKey = null; Changed = null;
    }
    public void Dispose() => Close();
    private void Verify()
    {
        if (_owner != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Owner thread required.");
    }
}
