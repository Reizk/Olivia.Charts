using System.Collections.Immutable;
using System.Globalization;
using Olivia.Core.Charts;

namespace Olivia.Core.Workspaces;

public enum MultiSeriesChartKind { Area, Scatter, Bar, Line, StepLineAfter, StepLineBefore, StepAreaAfter, StepAreaBefore, StackedBar, FullStackedBar }
public sealed record MultiSeriesSample(long Key, int X, double? Value);
public sealed record MultiSeriesData(string Id, string Label, IReadOnlyList<MultiSeriesSample> Samples);
public sealed record MultiSeriesSelection(string SeriesId, long Key);
public sealed record MultiSeriesChartPublication(long Revision, long DataVersion,
    AreaChartSource Area, ScatterChartSource Scatter, BarChartSource Bars,
    HistoryValueDomain XDomain, HistoryValueDomain YDomain, int FirstIndex, int LastIndex)
{
    public BarChartSnapshot? StackSnapshot { get; init; }
    public int BarCategoryStart { get; init; }
    public int BarCategoryCount { get; init; }
}

/// <summary>A bounded, owner-thread workspace for keyed XY views of immutable host data.
/// Inclusive X endpoints use original coordinates, not ordinal positions.</summary>
public sealed class MultiSeriesChartSession : IDisposable
{
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private bool _changing;
    public MultiSeriesChartSource Source { get; private set; }
    public IReadOnlyList<MultiSeriesData> Series { get; private set; }
    public MultiSeriesChartKind Kind { get; private set; } = MultiSeriesChartKind.Area;
    public IReadOnlyList<string> VisibleSeries { get; private set; }
    public int FirstIndex { get; private set; }
    public int LastIndex { get; private set; } = 7;
    public bool AutoY { get; private set; } = true;
    public double RequestedYMinimum { get; private set; } = -5;
    public double RequestedYMaximum { get; private set; } = 12;
    public MultiSeriesChartPublication Publication { get; private set; }
    public MultiSeriesSelection? Selection { get; private set; }
    public MultiSeriesSample? SelectedSample => Selection is { } selected
        ? Series.FirstOrDefault(s => s.Id == selected.SeriesId)?.Samples.FirstOrDefault(s => s.Key == selected.Key) : null;
    public bool IsSelectionVisible => Selection is { } selected && VisibleSeries.Contains(selected.SeriesId)
        && SelectedSample is { Value: { } value } sample && double.IsFinite(value)
        && sample.X >= FirstIndex && sample.X <= LastIndex
        && (IsStackKind(Kind) ? Publication.StackSnapshot is { } stack && stack.Bars.Any(bar => bar.Hit.SeriesId == selected.SeriesId && bar.Hit.Key == selected.Key)
            : value >= Publication.YDomain.Minimum && value <= Publication.YDomain.Maximum);
    private static bool IsStackKind(MultiSeriesChartKind kind) => kind is MultiSeriesChartKind.StackedBar or MultiSeriesChartKind.FullStackedBar;
    public bool IsClosed { get; private set; }
    public int CallbackFailureCount { get; private set; }
    public event EventHandler? Changed;

    public MultiSeriesChartSession() : this(new MultiSeriesChartSource(CreateData())) { }

    public MultiSeriesChartSession(MultiSeriesChartSource source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Series = source.Series;
        VisibleSeries = source.Series.Select(s => s.Id).ToImmutableArray();
        FirstIndex = source.MinimumX; LastIndex = source.MaximumX;
        Publication = Build(1, source.Version, Series, VisibleSeries, FirstIndex, LastIndex, true, -5, 12);
    }

    public static IReadOnlyList<MultiSeriesData> CreateData(bool revised = false)
    {
        double?[][] values = [[2, 5, null, 7, -3, 5, 10, 4], [4, 5, 6, null, 1, 8, 10, 3], [-2, 2, 4, 6, 8, null, 5, 9]];
        string[] ids = ["A", "B", "C"], labels = ["Observed", "Forecast", "Reference"];
        return Enumerable.Range(0, 3).Select(i => new MultiSeriesData(ids[i], labels[i],
            values[i].Select((v, x) => new MultiSeriesSample(x + 1, x, revised && v.HasValue ? v + 1 : v))
                .Where(s => !revised || i != 0 || s.Key != 4).ToImmutableArray())).ToImmutableArray();
    }

    public bool Apply(MultiSeriesChartKind kind, IEnumerable<string> visible, int first, int last,
        bool autoY, double ymin, double ymax)
    {
        Verify();
        if (IsClosed || _changing) return false;
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(visible);
        var requested = visible.Take(MultiSeriesChartSource.MaximumSeries + 1).ToArray();
        if (requested.Length > MultiSeriesChartSource.MaximumSeries || requested.Any(id => id is null || !Series.Any(s => s.Id == id))
            || requested.Distinct(StringComparer.Ordinal).Count() != requested.Length)
            throw new ArgumentException("Expected distinct known series identifiers.", nameof(visible));
        if (first < Source.MinimumX || first > last || last > Source.MaximumX
            || (first == last && Source.MinimumX != Source.MaximumX)) throw new ArgumentOutOfRangeException(nameof(first));
        if (!double.IsFinite(ymin) || !double.IsFinite(ymax) || ymin < -1_000_000 || ymax > 1_000_000 || ymin >= ymax)
            throw new ArgumentOutOfRangeException(nameof(ymin));
        var copy = Series.Where(s => requested.Contains(s.Id, StringComparer.Ordinal)).Select(s => s.Id).ToImmutableArray();
        var publication = Build(Publication.Revision + 1, Publication.DataVersion, Series, copy, first, last, autoY, ymin, ymax, kind);
        return Change(() =>
        {
            Kind = kind; VisibleSeries = copy; FirstIndex = first; LastIndex = last;
            AutoY = autoY; RequestedYMinimum = ymin; RequestedYMaximum = ymax; Publication = publication;
        });
    }

    public bool Select(string id, long key)
    {
        Verify();
        if (IsClosed || _changing || id is null || !Series.Any(s => s.Id == id && s.Samples.Any(p => p.Key == key && p.Value is { } v && double.IsFinite(v)))) return false;
        var selection = new MultiSeriesSelection(id, key);
        if (Selection == selection) return false;
        return Change(() => Selection = selection);
    }

    public bool ClearSelection()
    {
        Verify();
        return Selection is not null && Change(() => Selection = null);
    }

    /// <summary>The fixture buttons remain examples. Host callers use the source overload.</summary>
    public bool ReplaceData(bool revised) => ReplaceData(new MultiSeriesChartSource(CreateData(revised), revised ? 2 : 1));

    /// <summary>Build before commit. Surviving IDs retain visibility; new IDs are visible.
    /// Preserve compatible X range and kind/Y configuration; otherwise use the full new extent.
    /// Selection survives by series ID + key (including a newly null value), and clears if removed.</summary>
    public bool ReplaceData(MultiSeriesChartSource source)
    {
        Verify();
        if (IsClosed || _changing) return false;
        ArgumentNullException.ThrowIfNull(source);
        var visible = source.Series.Where(s => VisibleSeries.Contains(s.Id, StringComparer.Ordinal)
            || !Series.Any(old => old.Id == s.Id)).Select(s => s.Id).ToImmutableArray();
        var first = FirstIndex; var last = LastIndex;
        if (first < source.MinimumX || last > source.MaximumX || (first == last && source.MinimumX != source.MaximumX))
        { first = source.MinimumX; last = source.MaximumX; }
        var publication = Build(Publication.Revision + 1, source.Version, source.Series, visible,
            first, last, AutoY, RequestedYMinimum, RequestedYMaximum, Kind);
        return Change(() =>
        {
            Source = source; Series = source.Series; VisibleSeries = visible; FirstIndex = first; LastIndex = last; Publication = publication;
            if (SelectedSample is null) Selection = null;
        });
    }

    /// <summary>Reject a delayed action captured against any older publication, even reused source versions.</summary>
    public bool Select(string id, long key, MultiSeriesChartPublication expectedPublication)
    {
        Verify();
        return ReferenceEquals(Publication, expectedPublication) && Select(id, key);
    }

    public bool Reset()
    {
        Verify();
        if (IsClosed || _changing) return false;
        var data = Series;
        var visible = data.Select(s => s.Id).ToImmutableArray();
        var publication = Build(Publication.Revision + 1, Publication.DataVersion, data, visible, Source.MinimumX, Source.MaximumX, true, -5, 12);
        return Change(() =>
        {
            VisibleSeries = visible; Kind = MultiSeriesChartKind.Area;
            FirstIndex = Source.MinimumX; LastIndex = Source.MaximumX; AutoY = true; RequestedYMinimum = -5; RequestedYMaximum = 12;
            Selection = null; Publication = publication;
        });
    }

    private static MultiSeriesChartPublication Build(long revision, long version, IReadOnlyList<MultiSeriesData> data,
        IReadOnlyList<string> visible, int first, int last, bool autoY, double ymin, double ymax,
        MultiSeriesChartKind kind = MultiSeriesChartKind.Area)
    {
        var shown = data.Where(s => visible.Contains(s.Id)).ToArray();
        var finite = shown.SelectMany(s => s.Samples).Where(s => s.X >= first && s.X <= last && s.Value is { } v && double.IsFinite(v))
            .Select(s => s.Value!.Value).ToArray();
        var y = !autoY ? new HistoryValueDomain(ymin, ymax) : finite.Length == 0 ? new HistoryValueDomain(-1, 1)
            : new HistoryValueDomain(Math.Min(0, finite.Min()), Math.Max(0, finite.Max()));
        if(autoY && y.Minimum == y.Maximum) y = new HistoryValueDomain(-1, 1);
        var area = new AreaChartSource(shown.Select(s => new AreaSeries(s.Id, s.Label,
            s.Samples.Select(p => new AreaSample(p.Key, p.X, p.Value)), version)), version);
        var scatter = new ScatterChartSource(shown.Select(s => new ScatterSeries(s.Id, s.Label,
            s.Samples.Select(p => new ScatterSample(p.Key, p.X, p.Value)), version)), version);
        var categories = data.SelectMany(s => s.Samples).Select(p => p.X).Distinct().Order().ToArray();
        var bars = new BarChartSource(categories.Select(x => new BarCategory(Category(x), Category(x))),
            shown.Select(s => new BarSeries(s.Id, s.Label, s.Samples.Select(p => new BarSample(p.Key, Category(p.X), p.Value)), version)), version);
        var categoryStart = categories.Count(x => x < first);
        var categoryCount = categories.Count(x => x >= first && x <= last);
        BarChartSnapshot? stack = null;
        if (IsStackKind(kind))
        {
            var layout = kind == MultiSeriesChartKind.FullStackedBar ? BarChartLayout.FullStacked : BarChartLayout.Stacked;
            stack = bars.Query(new(layout, categoryStart, categoryCount, fixedDomain: autoY ? null : y));
            RequireStack(stack);
            if (autoY && (stack.Domain is not { } domain || domain.Minimum == domain.Maximum))
            {
                // At most one extra bounded query: preserve the host's readable empty/zero fallback.
                y = new(-1, 1);
                stack = bars.Query(new(layout, categoryStart, categoryCount, fixedDomain: y));
                RequireStack(stack);
            }
            else if (stack.Domain is { } stackDomain) y = stackDomain;
        }
        return new(revision, version, area, scatter, bars, new(first, last), y, first, last)
        { BarCategoryStart = categoryStart, BarCategoryCount = categoryCount, StackSnapshot = stack };
    }

    private static void RequireStack(BarChartSnapshot snapshot)
    {
        if (snapshot.StackStatus != BarChartStackStatus.Ready)
            throw new ArgumentException($"Stack configuration rejected: {snapshot.StackStatus}; no publication changed.");
    }

    private static string Category(int x) => x.ToString(CultureInfo.InvariantCulture);
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
    private void Verify()
    {
        if (_owner != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Owner thread required.");
    }
    public void Close() { Verify(); if (IsClosed) return; IsClosed = true; Changed = null; Selection = null; }
    public void Dispose() => Close();
}
