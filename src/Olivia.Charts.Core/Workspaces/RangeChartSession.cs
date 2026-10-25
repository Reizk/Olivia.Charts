using System.Collections.Immutable;
using Olivia.Core.Charts;

namespace Olivia.Core.Workspaces;

public sealed record RangeChartSelection(string SeriesId, long Key);
public sealed record RangeChartPublication(long Revision, RangeChartSnapshot Snapshot);

/// <summary>Owner-thread-only bounded two-endpoint workspace. Candidates are fully validated and
/// queried before mutation; numeric versions alone never authorize a stale selection or apply.</summary>
public sealed class RangeChartSession : IDisposable
{
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private readonly int _workBudget;
    private bool _changing;
    public RangeChartSession() : this(CreateSource()) { }
    public RangeChartSession(RangeChartSource source, int workBudget = RangeChartSource.MaximumWork)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (workBudget < 0 || workBudget > RangeChartSource.MaximumWork) throw new ArgumentOutOfRangeException(nameof(workBudget));
        _workBudget = workBudget;
        Source = source;
        VisibleSeries = source.Series.Select(s => s.Id).ToImmutableArray();
        MinimumX = source.Coordinates.IsEmpty ? 0 : source.Coordinates[0];
        MaximumX = source.Coordinates.IsEmpty ? 1 : source.Coordinates[^1];
        Publication = Build(1, source, Mode, VisibleSeries, MinimumX, MaximumX, AutoY, MinimumY, MaximumY);
    }
    public RangeChartSource Source { get; private set; }
    public RangeChartMode Mode { get; private set; } = RangeChartMode.GroupedBars;
    public ImmutableArray<string> VisibleSeries { get; private set; }
    public double MinimumX { get; private set; }
    public double MaximumX { get; private set; }
    public bool AutoY { get; private set; } = true;
    public double MinimumY { get; private set; } = -4;
    public double MaximumY { get; private set; } = 9;
    public RangeChartPublication Publication { get; private set; }
    public RangeChartSelection? Selection { get; private set; }
    public RangeChartHit? SelectedHit => Selection is { } selection ? Find(Source, selection) : null;
    public bool IsSelectionVisible => Selection is { } selection && Publication.Snapshot.Intervals.Any(x =>
        StringComparer.Ordinal.Equals(x.Hit.SeriesId, selection.SeriesId) && x.Hit.Key == selection.Key);
    public bool IsClosed { get; private set; }
    public int CallbackFailureCount { get; private set; }
    public event EventHandler? Changed;

    public static RangeChartSource CreateSource(bool revised = false)
    {
        if (revised)
            return new([new RangeSeries("P", "Revised observed", [new(101, 0, 1, 3), new(102, 3, -2, 4)]),
                new RangeSeries("Q", "Revised forecast", [new(201, 0, 2, 5), new(202, 3, 1, 6)])], 1);
        return new([new RangeSeries("A", "Observed interval", [new(1, 0, 2, 6), new(2, 2, -3, -1), new(3, 5, null, 4), new(4, 7, 1, 1)]),
            new RangeSeries("B", "Forecast interval", [new(1, 0, 4, 8), new(2, 2, -2, 2), new(3, 5, 2, 5), new(4, 7, 3, 6)])], 1);
    }

    public bool Apply(RangeChartPublication expected, RangeChartMode mode, IEnumerable<string> visible,
        double xmin, double xmax, bool autoY, double ymin, double ymax)
    {
        Verify();
        if (IsClosed || _changing || !ReferenceEquals(expected, Publication)) return false;
        ArgumentNullException.ThrowIfNull(visible);
        var copy = BarChartSource.CopyBounded(visible, RangeChartSource.MaximumSeries, nameof(visible));
        // Caller enumeration/disposal can synchronously reenter this owner-thread session.
        if (IsClosed || _changing || !ReferenceEquals(expected, Publication)) return false;
        var candidate = Build(checked(Publication.Revision + 1), Source, mode, copy, xmin, xmax, autoY, ymin, ymax);
        return Change(() =>
        {
            Mode = mode; VisibleSeries = copy; MinimumX = xmin; MaximumX = xmax;
            AutoY = autoY; MinimumY = ymin; MaximumY = ymax; Publication = candidate;
        });
    }
    public bool ReplaceSource(RangeChartPublication expected, RangeChartSource source)
    {
        Verify(); ArgumentNullException.ThrowIfNull(source);
        if (IsClosed || _changing || !ReferenceEquals(expected, Publication)) return false;
        var visible = source.Series.Where(s => VisibleSeries.Contains(s.Id, StringComparer.Ordinal)).Select(s => s.Id).ToImmutableArray();
        // If no old identity survives, show all of the explicitly supplied replacement source.
        if (visible.IsEmpty && !VisibleSeries.IsEmpty) visible = source.Series.Select(s => s.Id).ToImmutableArray();
        var lower = source.Coordinates.IsEmpty ? 0 : source.Coordinates[0];
        var upper = source.Coordinates.IsEmpty ? 1 : source.Coordinates[^1];
        var xmin = Math.Max(MinimumX, lower); var xmax = Math.Min(MaximumX, upper);
        if (xmin > xmax || (xmin == xmax && lower != upper)) { xmin = lower; xmax = upper; }
        var candidate = Build(checked(Publication.Revision + 1), source, Mode, visible, xmin, xmax, AutoY, MinimumY, MaximumY);
        var selection = Selection is { } old && Find(source, old).HasValue ? old : null;
        return Change(() => { Source = source; VisibleSeries = visible; MinimumX = xmin; MaximumX = xmax; Publication = candidate; Selection = selection; });
    }
    public bool Reset(RangeChartPublication expected)
    {
        Verify();
        if (IsClosed || _changing || !ReferenceEquals(expected, Publication)) return false;
        var xmin = Source.Coordinates.IsEmpty ? 0 : Source.Coordinates[0];
        var xmax = Source.Coordinates.IsEmpty ? 1 : Source.Coordinates[^1];
        return Apply(expected, Mode, Source.Series.Select(s => s.Id), xmin, xmax, true, -4, 9);
    }
    public bool Select(RangeChartPublication expected, string seriesId, long key)
    {
        Verify();
        if (IsClosed || _changing || !ReferenceEquals(expected, Publication)) return false;
        var selected = new RangeChartSelection(seriesId, key);
        if (!Find(Source, selected).HasValue || Selection == selected) return false;
        return Change(() => Selection = selected);
    }
    public bool ClearSelection(RangeChartPublication expected)
    {
        Verify();
        return !IsClosed && !_changing && ReferenceEquals(expected, Publication) && Selection is not null && Change(() => Selection = null);
    }
    private RangeChartPublication Build(long revision, RangeChartSource source, RangeChartMode mode,
        ImmutableArray<string> visible, double xmin, double xmax, bool autoY, double ymin, double ymax)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!double.IsFinite(xmin) || !double.IsFinite(xmax) || xmin < -1_000_000 || xmax > 1_000_000 || xmin > xmax)
            throw new ArgumentOutOfRangeException(nameof(xmin));
        if (!double.IsFinite(ymin) || !double.IsFinite(ymax) || ymin < -1_000_000 || ymax > 1_000_000 || ymin >= ymax)
            throw new ArgumentOutOfRangeException(nameof(ymin));
        if (checked(source.RawEntryCount + source.Series.Length) > 2056)
            throw new ArgumentException("Range host scan limit exceeded.", nameof(source));
        var ids = new HashSet<string>(visible, StringComparer.Ordinal);
        if (ids.Count != visible.Length || ids.Any(id => !source.Series.Any(s => StringComparer.Ordinal.Equals(s.Id, id))))
            throw new ArgumentException("Expected distinct known visible series identities.", nameof(visible));
        double? min = null, max = null;
        foreach (var series in source.Series)
        {
            var shown = ids.Contains(series.Id);
            foreach (var sample in series.Samples)
            {
                if (sample.X != Math.Truncate(sample.X) || Math.Abs(sample.X) > 1_000_000 ||
                    (sample.Lower is { } low && double.IsFinite(low) && Math.Abs(low) > 1_000_000) ||
                    (sample.Upper is { } high && double.IsFinite(high) && Math.Abs(high) > 1_000_000))
                    throw new ArgumentException("Range host requires integer X and finite endpoints within +/-1,000,000.", nameof(source));
                if (!shown || sample.IsGap) continue;
                min = min.HasValue ? Math.Min(min.Value, sample.Lower!.Value) : sample.Lower!.Value;
                max = max.HasValue ? Math.Max(max.Value, sample.Upper!.Value) : sample.Upper!.Value;
            }
        }
        var y = new HistoryValueDomain(ymin, ymax);
        if (autoY) y = !min.HasValue ? new(-1, 1) : min == max ? new(min.Value - 1, max!.Value + 1) : new(min.Value, max!.Value);
        var options = new RangeChartOptions(mode, new(xmin, xmax), y, visible, workBudget: _workBudget);
        var snapshot = source.Query(options);
        if (snapshot.Status != RangeChartStatus.Ready)
            throw new ArgumentException($"Range candidate refused: {snapshot.Status}; prior publication retained.");
        return new(revision, snapshot);
    }
    private static RangeChartHit? Find(RangeChartSource source, RangeChartSelection selected)
    {
        foreach (var series in source.Series)
            if (StringComparer.Ordinal.Equals(series.Id, selected.SeriesId))
                foreach (var sample in series.Samples)
                    if (sample.Key == selected.Key && !sample.IsGap) return new(source, series.Id, series.SourceVersion, sample);
        return null;
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
                { if (IsClosed) break; try { handler(this, EventArgs.Empty); } catch { CallbackFailureCount++; } }
            return true;
        }
        finally { _changing = false; }
    }
    private void Verify()
    { if (Environment.CurrentManagedThreadId != _owner) throw new InvalidOperationException("Owner thread required."); }
    public void Dispose() { Verify(); if (IsClosed) return; IsClosed = true; Selection = null; Changed = null; }
}
