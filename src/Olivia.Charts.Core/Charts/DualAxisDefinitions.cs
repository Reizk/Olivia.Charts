using System.Collections.Immutable;

namespace Olivia.Core.Charts;

public enum HistoryAxisSide { Left, Right }

/// <summary>A stable named unit axis. Axis and series identifiers use ordinal comparison.</summary>
public sealed class HistoryAxisDefinition
{
    public HistoryAxisDefinition(string id, string label, string unit, HistoryAxisSide side)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        if (!Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(side));
        Id = id;
        Label = label;
        Unit = unit;
        Side = side;
    }

    public string Id { get; }
    public string Label { get; }
    public string Unit { get; }
    public HistoryAxisSide Side { get; }
}

/// <summary>Stable series identity and unit-axis mapping for one immutable raw source version.</summary>
public sealed class HistorySeriesDefinition
{
    public HistorySeriesDefinition(string id, string label, string axisId, HistorySeries source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(axisId);
        ArgumentNullException.ThrowIfNull(source);
        Id = id;
        Label = label;
        AxisId = axisId;
        Source = source;
    }

    public string Id { get; }
    public string Label { get; }
    public string AxisId { get; }
    public HistorySeries Source { get; }
}

/// <summary>Finite inclusive Y bounds. A constant range is valid and projects at the center.</summary>
public readonly record struct HistoryValueDomain
{
    public HistoryValueDomain(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum)) throw new ArgumentOutOfRangeException(nameof(minimum));
        if (!double.IsFinite(maximum) || maximum < minimum)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        Minimum = minimum;
        Maximum = maximum;
    }

    public double Minimum { get; }
    public double Maximum { get; }
    public bool IsConstant => Minimum == Maximum;
}

/// <summary>
/// Immutable presentation choices. Absent visibility entries mean visible; absent domains mean auto.
/// Hiding an axis also hides all its curves. Unknown identifiers are rejected when querying a source.
/// </summary>
public sealed class DualAxisChartOptions
{
    private readonly ImmutableDictionary<string, bool> _axes;
    private readonly ImmutableDictionary<string, bool> _series;
    private readonly ImmutableDictionary<string, HistoryValueDomain> _domains;

    public DualAxisChartOptions() : this(
        ImmutableDictionary.Create<string, bool>(StringComparer.Ordinal),
        ImmutableDictionary.Create<string, bool>(StringComparer.Ordinal),
        ImmutableDictionary.Create<string, HistoryValueDomain>(StringComparer.Ordinal)) { }

    private DualAxisChartOptions(ImmutableDictionary<string, bool> axes,
        ImmutableDictionary<string, bool> series, ImmutableDictionary<string, HistoryValueDomain> domains)
    {
        _axes = axes;
        _series = series;
        _domains = domains;
    }

    public static DualAxisChartOptions Default { get; } = new();

    public bool IsAxisVisible(string axisId) => !_axes.TryGetValue(axisId, out var visible) || visible;
    public bool IsSeriesVisible(string seriesId) => !_series.TryGetValue(seriesId, out var visible) || visible;
    public HistoryValueDomain? GetAxisDomain(string axisId) => _domains.TryGetValue(axisId, out var domain) ? domain : null;

    public DualAxisChartOptions WithAxisVisibility(string axisId, bool visible)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(axisId);
        return new(_axes.SetItem(axisId, visible), _series, _domains);
    }

    public DualAxisChartOptions WithSeriesVisibility(string seriesId, bool visible)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesId);
        return new(_axes, _series.SetItem(seriesId, visible), _domains);
    }

    public DualAxisChartOptions WithAxisDomain(string axisId, HistoryValueDomain? domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(axisId);
        return new(_axes, _series, domain.HasValue ? _domains.SetItem(axisId, domain.Value) : _domains.Remove(axisId));
    }

    internal void Validate(IReadOnlySet<string> axes, IReadOnlySet<string> series)
    {
        foreach (var id in _axes.Keys.Concat(_domains.Keys))
            if (!axes.Contains(id)) throw new ArgumentException($"Unknown axis identifier: {id}.", "options");
        foreach (var id in _series.Keys)
            if (!series.Contains(id)) throw new ArgumentException($"Unknown series identifier: {id}.", "options");
    }
}
