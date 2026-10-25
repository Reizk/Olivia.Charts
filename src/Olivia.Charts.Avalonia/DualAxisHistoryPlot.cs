using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Olivia.Core.Charts;
using Olivia.Core.Display;
using Olivia.Core.Snapshots;

namespace Olivia.Avalonia;

public sealed record DualAxisTickPresentation(double Value, double Position, string Text);
public sealed record DualAxisPresentation(string AxisId, string Label, HistoryAxisSide Side,
    ImmutableArray<DualAxisTickPresentation> Ticks);
public sealed record DualAxisLegendPresentation(string SeriesId, string AxisId, string Text, bool IsVisible);

/// <summary>
/// Independent-unit historical lines with one UTC window and one aggregate point budget.
/// A displayed frame owns the exact immutable dataset used for raw hits, axes and legend.
/// </summary>
public sealed class DualAxisHistoryPlot : Control
{
    private readonly object _gate = new();
    private readonly VersionedSnapshotStore<DualAxisHistorySource> _sources;
    private readonly Func<DualAxisHistorySource, TimeWindow, DualAxisChartOptions, int, CancellationToken, Task<DualAxisChartSnapshot>> _query;
    private DualAxisHistorySource? _source;
    private SnapshotLease<DualAxisHistorySource>? _displayedSource;
    private RenderRequest? _request, _active;
    private RenderFrame? _pendingFrame;
    private long _sourceIdentity, _revision, _epoch;
    private bool _attached, _workerRunning, _notificationPending;
    private Task _idle = Task.CompletedTask;
    private DualAxisHistoryHit? _selection;
    private DualAxisChartOptions _options = DualAxisChartOptions.Default;
    private ValueDisplayFormat _displayFormat = ValueDisplayFormat.Default;
    private UiPalette _palette = UiPalette.Dark;
    private int _pointBudget = 800;
    private Point? _dragStart;
    private TimeWindow? _dragWindow;
    private IPointer? _dragPointer;
    private Window? _ownerWindow;

    private sealed class RenderRequest(SnapshotLease<DualAxisHistorySource> source, TimeWindow window,
        DualAxisChartOptions options, int budget, long revision, long epoch)
    {
        public SnapshotLease<DualAxisHistorySource> Source { get; } = source;
        public TimeWindow Window { get; } = window;
        public DualAxisChartOptions Options { get; } = options;
        public int Budget { get; } = budget;
        public long Revision { get; } = revision;
        public long Epoch { get; } = epoch;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task CancellationFinished { get; private set; } = Task.CompletedTask;
        public void Cancel() { if (!Cancellation.IsCancellationRequested) CancellationFinished = Cancellation.CancelAsync(); }
        public void Dispose() { Cancellation.Dispose(); Source.Dispose(); }
    }
    private sealed record RenderFrame(RenderRequest Request, DualAxisChartSnapshot? Snapshot, string? Error);

    public DualAxisHistoryPlot() : this(new SnapshotBudget(4, 4_000_000, 4)) { }
    public DualAxisHistoryPlot(SnapshotBudget sourceBudget,
        Func<DualAxisHistorySource, TimeWindow, DualAxisChartOptions, int, CancellationToken, Task<DualAxisChartSnapshot>>? query = null)
    {
        ArgumentNullException.ThrowIfNull(sourceBudget);
        if (sourceBudget.MaximumLeases < 3)
            throw new ArgumentException("A chart needs display, active and newest-request leases.", nameof(sourceBudget));
        _sources = new(sourceBudget);
        _query = query ?? ((source, window, options, budget, token) => Task.FromResult(source.Query(window, budget, options, token)));
        Focusable = true; ClipToBounds = true; MinHeight = 240;
        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) => { CancelDrag(); InvalidateVisual(); };
        SizeChanged += (_, _) => { if (!IsPlotViewportAvailable) CancelDrag(); };
        AutomationProperties.SetName(this, "Dual-axis history. Independent units and Y scales. Arrows pan; plus and minus zoom; Home fits; Escape cancels a drag.");
    }

    public DualAxisChartSnapshot? Snapshot { get; private set; }
    public SnapshotStoreStatistics SourceStatistics => _sources.Statistics;
    public SnapshotPublishStatus LastSourceStatus { get; private set; } = SnapshotPublishStatus.Published;
    public TimeWindow? ViewWindow { get; private set; }
    public DualAxisHistoryHit? SelectedHit => _selection;
    public string Status { get; private set; } = "No source";
    public event EventHandler? StateChanged;
    public event EventHandler? ViewChanged;
    public Task WhenIdle { get { lock (_gate) return _idle; } }
    public UiPalette Palette { get => _palette; set { ArgumentNullException.ThrowIfNull(value); _palette = value; InvalidateVisual(); } }
    public int PointBudget
    {
        get => _pointBudget;
        set { Dispatcher.UIThread.VerifyAccess(); if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); if (_pointBudget == value) return; _pointBudget = value; QueueRender(); }
    }
    public DualAxisChartOptions Options
    {
        get => _options;
        set { Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(value); if (ReferenceEquals(value, _options)) return; CancelDrag(); _options = value; QueueRender(); }
    }
    /// <summary>Formatting is presentation-only. It does not publish, query, select, zoom or lease.</summary>
    public ValueDisplayFormat DisplayFormat
    {
        get => _displayFormat;
        set
        {
            Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(value, _displayFormat)) return;
            _displayFormat = value; UpdatePresentation(); InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public string AxisStartLabel => Snapshot is { } s ? DisplayFormat.FormatTimestamp(s.Window.StartTicks) : "";
    public string AxisEndLabel => Snapshot is { } s ? DisplayFormat.FormatTimestamp(s.Window.EndTicks - 1) : "";
    public string SelectedDisplayText
    {
        get
        {
            if (_selection is not { } hit || Snapshot is not { } snapshot) return "";
            var series = snapshot.Series.First(s => s.Definition.Id == hit.SeriesId);
            var axis = snapshot.Axes.First(a => a.Definition.Id == series.Definition.AxisId).Definition;
            return $"{series.Definition.Label} [{hit.SeriesId}] · {axis.Label} ({axis.Unit}, {axis.Side}) · key {Invariant(hit.Key)} · {DisplayFormat.FormatTimestamp(hit.UtcTicks)} · {DisplayFormat.FormatNumber(hit.Value)} {axis.Unit}";
        }
    }
    public string AccessibleSummary => _selection is { } hit
        ? $"{Status}. {SelectedDisplayText}; dataset {Invariant(hit.DatasetVersion)}, series source {Invariant(hit.SeriesSourceVersion)}, UTC ticks {Invariant(hit.UtcTicks)}, original value {hit.Value?.ToString("R", CultureInfo.InvariantCulture) ?? "missing"}"
        : Status;
    public ImmutableArray<DualAxisPresentation> AxisPresentations => Snapshot is not { } snapshot ? [] : snapshot.Axes
        .Where(axis => axis.IsVisible).Select(axis => new DualAxisPresentation(axis.Definition.Id,
            $"{axis.Definition.Side}: {axis.Definition.Label} ({axis.Definition.Unit})", axis.Definition.Side,
            axis.Ticks.Select(tick => new DualAxisTickPresentation(tick.Value, tick.Position, DisplayFormat.FormatNumber(tick.Value))).ToImmutableArray())).ToImmutableArray();
    public ImmutableArray<DualAxisLegendPresentation> LegendPresentations => Snapshot is not { } snapshot ? [] : snapshot.Series.Select(series =>
    {
        var axis = snapshot.Axes.First(a => a.Definition.Id == series.Definition.AxisId).Definition;
        return new DualAxisLegendPresentation(series.Definition.Id, axis.Id,
            $"{series.Definition.Label} [{series.Definition.Id}] → {axis.Side} · {axis.Label} ({axis.Unit}) · {(series.IsVisible ? "shown" : "hidden")}", series.IsVisible);
    }).ToImmutableArray();
    /// <summary>A constrained viewport shows an explicit resize notice, never an overlapping axes/curve plot.</summary>
    public bool IsPlotViewportAvailable => Bounds.Width >= 360 && Bounds.Height >= 232 + 20 * (Snapshot?.Series.Length ?? 0);
    public Rect PlotArea => new(104, 76 + 20 * (Snapshot?.Series.Length ?? 0),
        Math.Max(1, Bounds.Width - 208), Math.Max(1, Bounds.Height - 152 - 20 * (Snapshot?.Series.Length ?? 0)));

    public SnapshotPublishStatus SetSource(DualAxisHistorySource source, bool resetView = true)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(source); EndDrag();
        lock (_gate)
        {
            DropQueuedLocked();
            LastSourceStatus = _sources.Publish(checked(_sourceIdentity + 1), source, source.Count);
            if (LastSourceStatus == SnapshotPublishStatus.Published) { _sourceIdentity++; _source = source; }
        }
        if (LastSourceStatus != SnapshotPublishStatus.Published)
        {
            QueueRender(); Status = $"Source budget exceeded; showing dataset {Snapshot?.DatasetVersion.ToString(CultureInfo.InvariantCulture) ?? "none"}";
            UpdatePresentation(); InvalidateVisual(); return LastSourceStatus;
        }
        ViewWindow = resetView || ViewWindow is null ? source.Extent : source.Extent is { } extent ? ViewWindow.Value.ClampTo(extent) : null;
        QueueRender(); return LastSourceStatus;
    }
    public void ReleaseSource()
    {
        Dispatcher.UIThread.VerifyAccess(); EndDrag();
        lock (_gate)
        {
            _revision++; _epoch++; DropQueuedLocked(); _active?.Cancel();
            _displayedSource?.Dispose(); _displayedSource = null; _source = null;
            Snapshot = null; ViewWindow = null; _selection = null; _sources.Reset(); Status = "No source";
        }
        UpdatePresentation(); InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty);
    }
    public void ResetView()
    {
        Dispatcher.UIThread.VerifyAccess(); EndDrag(); ViewWindow = _source?.Extent; QueueRender(); ViewChanged?.Invoke(this, EventArgs.Empty);
    }
    public void Zoom(double scale, long? anchorTicks = null)
    {
        Dispatcher.UIThread.VerifyAccess(); EndDrag();
        if (ViewWindow is not { } window || _source?.Extent is not { } extent) return;
        ViewWindow = window.Zoom(scale, anchorTicks ?? window.StartTicks + window.DurationTicks / 2, extent);
        QueueRender(); ViewChanged?.Invoke(this, EventArgs.Empty);
    }
    public void Pan(double fraction)
    {
        Dispatcher.UIThread.VerifyAccess(); if (!double.IsFinite(fraction)) throw new ArgumentOutOfRangeException(nameof(fraction)); EndDrag();
        if (ViewWindow is not { } window || _source?.Extent is not { } extent) return;
        ViewWindow = window.Pan(ToTicks(window.DurationTicks * fraction), extent); QueueRender(); ViewChanged?.Invoke(this, EventArgs.Empty);
    }
    public DualAxisHistoryHit? SelectNearest(string seriesId, long utcTicks)
    {
        Dispatcher.UIThread.VerifyAccess();
        _selection = Snapshot is { } snapshot && snapshot.Series.Any(s => s.Definition.Id == seriesId && s.IsVisible)
            && _displayedSource is { } lease && lease.TryGetValue(out var source)
            ? source!.FindNearest(seriesId, utcTicks, snapshot.Window) : null;
        UpdatePresentation(); InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty); return _selection;
    }
    private void UpdatePresentation()
    {
        AutomationProperties.SetHelpText(this, AccessibleSummary); ToolTip.SetTip(this, _selection is null ? null : AccessibleSummary);
    }
    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static long AtFraction(TimeWindow window, double fraction)
    {
        var lastOffset = Math.Max(0, window.DurationTicks - 1);
        return window.StartTicks + Math.Clamp((long)(Math.Clamp(fraction, 0, 1) * lastOffset), 0, lastOffset);
    }
    private static long ToTicks(double value) => value >= long.MaxValue ? long.MaxValue : value <= long.MinValue ? long.MinValue : (long)value;

    private void DropQueuedLocked()
    {
        _request?.Dispose(); _request = null;
        _pendingFrame?.Request.Dispose(); _pendingFrame = null;
    }
    private void QueueRender()
    {
        lock (_gate)
        {
            _revision++; DropQueuedLocked(); _active?.Cancel();
            if (!_attached || _source is null || ViewWindow is null)
            {
                if (_source?.Count == 0)
                {
                    Snapshot = null; _displayedSource?.Dispose(); _displayedSource = null; _selection = null;
                    Status = "No samples in this source";
                }
            }
            else
            {
                var acquisition = _sources.Acquire(_sourceIdentity, SnapshotOwner.Pending);
                if (acquisition.Lease is not { } lease)
                    Status = $"Snapshot {acquisition.Status}; showing dataset {Snapshot?.DatasetVersion.ToString(CultureInfo.InvariantCulture) ?? "none"}";
                else
                {
                    _request = new(lease, ViewWindow.Value, Options, PointBudget, _revision, _epoch);
                    Status = Snapshot is null ? "Loading…" : $"Loading… showing older dataset {Invariant(Snapshot.DatasetVersion)}";
                    if (!_workerRunning) { _workerRunning = true; _idle = Task.Run(RenderLoopAsync); }
                }
            }
        }
        UpdatePresentation(); InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty);
    }
    private async Task RenderLoopAsync()
    {
        while (true)
        {
            RenderRequest? request;
            lock (_gate)
            {
                request = _request; _request = null;
                if (request is null) { _workerRunning = false; return; }
                _active = request; request.Source.TransferTo(SnapshotOwner.Query);
            }
            RenderFrame frame;
            try
            {
                var source = request.Source.Value;
                var snapshot = await _query(source, request.Window, request.Options, request.Budget, request.Cancellation.Token).ConfigureAwait(false);
                if (!ReferenceEquals(snapshot.Source, source) || snapshot.DatasetVersion != source.DatasetVersion || snapshot.Window != request.Window
                    || snapshot.PointBudget != request.Budget || !ReferenceEquals(snapshot.Options, request.Options))
                    throw new InvalidOperationException("The history provider returned a different dataset or query.");
                frame = new(request, snapshot, null);
            }
            catch (Exception error) { frame = new(request, null, error.Message); }
            Task cancellation;
            lock (_gate) { _active = null; cancellation = request.CancellationFinished; }
            // Cancellation callbacks are part of actual work. Keep ownership until they terminate.
            try { await cancellation.ConfigureAwait(false); }
            catch (Exception error) { frame = new(request, null, "Cancellation callback failed: " + error.Message); }
            lock (_gate)
            {
                if (!_attached || request.Epoch != _epoch || request.Revision != _revision)
                { request.Dispose(); continue; }
                request.Source.TransferTo(SnapshotOwner.Pending);
                _pendingFrame?.Request.Dispose(); _pendingFrame = frame;
                if (!_notificationPending) { _notificationPending = true; Dispatcher.UIThread.Post(CommitLatest, DispatcherPriority.Background); }
            }
        }
    }
    private void CommitLatest()
    {
        RenderFrame? frame;
        lock (_gate)
        {
            frame = _pendingFrame; _pendingFrame = null; _notificationPending = false;
            if (!_attached || frame is null || frame.Request.Epoch != _epoch || frame.Request.Revision != _revision)
            { frame?.Request.Dispose(); return; }
        }
        if (frame.Error is not null) { frame.Request.Dispose(); Status = "Chart error: " + frame.Error; }
        else if (!frame.Request.Source.TransferTo(SnapshotOwner.Display)) { frame.Request.Dispose(); Status = "Snapshot expired; refresh the source"; }
        else
        {
            Snapshot = frame.Snapshot; _displayedSource?.Dispose(); _displayedSource = frame.Request.Source;
            frame.Request.Cancellation.Dispose();
            if (_selection is { } previous)
            {
                _selection = null;
                var selectedSeries = Snapshot!.Series.FirstOrDefault(s => s.Definition.Id == previous.SeriesId && s.IsVisible);
                if (selectedSeries is not null)
                    foreach (var sample in selectedSeries.Definition.Source.Samples)
                        if (sample.Key == previous.Key)
                        { if (!sample.IsGap) _selection = new DualAxisHistoryHit(Snapshot.DatasetVersion, previous.SeriesId, selectedSeries.SourceVersion, sample); break; }
            }
            Status = Snapshot!.BudgetStatus == ChartBudgetStatus.InsufficientBudget
                ? FormattableString.Invariant($"Total budget {Snapshot.PointBudget} is too small; at least {Snapshot.RequiredPointBudget} points needed to preserve all visible gaps")
                : FormattableString.Invariant($"Dataset {Snapshot.DatasetVersion} · {Snapshot.PointCount}/{Snapshot.PointBudget} total display points · independent Y scales");
        }
        UpdatePresentation(); InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ownerWindow = TopLevel.GetTopLevel(this) as Window;
        if (_ownerWindow is not null) _ownerWindow.Deactivated += OnWindowDeactivated;
        lock (_gate) { _attached = true; _epoch++; } QueueRender();
    }
    private void OnWindowDeactivated(object? sender, EventArgs e) => CancelDrag();
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_ownerWindow is not null) _ownerWindow.Deactivated -= OnWindowDeactivated;
        _ownerWindow = null;
        EndDrag(); lock (_gate) { _attached = false; _epoch++; _revision++; DropQueuedLocked(); _active?.Cancel(); }
        base.OnDetachedFromVisualTree(e);
    }
    private void EndDrag()
    {
        var pointer = _dragPointer; _dragStart = null; _dragWindow = null; _dragPointer = null;
        if (pointer?.Captured == this) pointer.Capture(null);
    }
    private void CancelDrag()
    {
        var initial = _dragWindow; EndDrag();
        if (initial is { } window && ViewWindow != window && _source?.Extent is { } extent)
        { ViewWindow = window.ClampTo(extent); QueueRender(); ViewChanged?.Invoke(this, EventArgs.Empty); }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsPlotViewportAvailable || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !PlotArea.Contains(e.GetPosition(this))) return;
        Focus(); _dragStart = e.GetPosition(this); _dragWindow = ViewWindow; _dragPointer = e.Pointer; e.Pointer.Capture(this);
        if (Snapshot is { } snapshot && _displayedSource is { } lease && lease.TryGetValue(out var source))
        {
            var area = PlotArea; var pointer = e.GetPosition(this);
            var fraction = Math.Clamp((pointer.X - area.X) / area.Width, 0, 1);
            var ticks = AtFraction(snapshot.Window, fraction);
            DualAxisHistoryHit? nearest = null; var distance = double.PositiveInfinity;
            foreach (var series in snapshot.Series.Where(s => s.IsVisible))
            {
                var axis = snapshot.Axes.First(a => a.Definition.Id == series.Definition.AxisId);
                if (axis.Domain is not { } domain || source!.FindNearest(series.Definition.Id, ticks, snapshot.Window) is not { } hit) continue;
                if (hit.Value < domain.Minimum || hit.Value > domain.Maximum) continue;
                var projected = DualAxisChartGeometry.Project(hit.UtcTicks, hit.Value!.Value, snapshot.Window, domain, area.X, area.Y, area.Width, area.Height);
                var candidate = Math.Abs(projected.Y - pointer.Y);
                if (candidate < distance) { distance = candidate; nearest = hit; }
            }
            _selection = nearest; UpdatePresentation(); InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty);
        }
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsPlotViewportAvailable) { CancelDrag(); return; }
        if (_dragStart is not { } start || _dragWindow is not { } window || _source?.Extent is not { } extent) return;
        var delta = e.GetPosition(this).X - start.X; if (Math.Abs(delta) < 3) return;
        ViewWindow = window.Pan(ToTicks(-delta / PlotArea.Width * window.DurationTicks), extent);
        QueueRender(); ViewChanged?.Invoke(this, EventArgs.Empty); e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { EndDrag(); base.OnPointerReleased(e); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { CancelDrag(); base.OnPointerCaptureLost(e); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e); if (!IsPlotViewportAvailable || !PlotArea.Contains(e.GetPosition(this)) || e.Delta.Y == 0) return;
        long? anchor = ViewWindow is { } window ? AtFraction(window, (e.GetPosition(this).X - PlotArea.X) / PlotArea.Width) : null;
        Zoom(e.Delta.Y > 0 ? 1.3 : 1 / 1.3, anchor); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape: CancelDrag(); break;
            case Key.Left: Pan(-.2); break;
            case Key.Right: Pan(.2); break;
            case Key.Add: case Key.OemPlus: Zoom(1.5); break;
            case Key.Subtract: case Key.OemMinus: Zoom(1 / 1.5); break;
            case Key.Home: ResetView(); break;
            default: return;
        }
        e.Handled = true;
    }

    private IBrush SeriesBrush(string id)
    {
        var index = Snapshot!.Series.Select(s => s.Definition.Id).Order(StringComparer.Ordinal).ToList().IndexOf(id);
        return index switch
        {
            0 => Palette.Accent, 1 => Palette.Error,
            2 => Brushes.CornflowerBlue, 3 => Brushes.Orchid,
            4 => Brushes.Goldenrod, 5 => Brushes.LimeGreen,
            6 => Brushes.DeepPink, _ => Brushes.SlateBlue
        };
    }
    private Pen SeriesPen(string id, string axisId, double width = 1.8)
    {
        var right = Snapshot!.Axes.First(a => a.Definition.Id == axisId).Definition.Side == HistoryAxisSide.Right;
        return new Pen(SeriesBrush(id), width, right ? DashStyle.Dash : null);
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Palette.Surface, new Rect(Bounds.Size));
        if (IsFocused) context.DrawRectangle(null, new Pen(Palette.Accent, 2), new Rect(Bounds.Size).Deflate(1));
        DrawingText.Draw(context, "Independent Y scales · read each series against its named axis", new(12, 10), Palette.Text, 12, Math.Max(1, Bounds.Width - 24));
        var legends = LegendPresentations;
        for (var i = 0; i < legends.Length; i++)
        {
            var legend = legends[i]; var y = 38 + i * 20;
            context.DrawLine(SeriesPen(legend.SeriesId, legend.AxisId), new(14, y + 7), new(38, y + 7));
            DrawingText.Draw(context, legend.Text, new(46, y), legend.IsVisible ? Palette.Text : Palette.Muted, 11, Math.Max(1, Bounds.Width - 60));
        }
        var area = PlotArea; var snapshot = Snapshot;
        if (!IsPlotViewportAvailable)
        {
            DrawingText.Draw(context, "Enlarge the view to display these legends and both named axes.", new(12, Bounds.Height - 43), Palette.Text, 11, Math.Max(1, Bounds.Width - 24));
            DrawingText.Draw(context, Status, new(12, Bounds.Height - 24), Palette.Muted, 11, Math.Max(1, Bounds.Width - 24));
            return;
        }
        foreach (var axis in AxisPresentations)
        {
            var left = axis.Side == HistoryAxisSide.Left;
            var titleX = left ? 8 : area.X + area.Width / 2 + 8;
            DrawingText.Draw(context, axis.Label, new(titleX, area.Y - 23), Palette.Text, 11,
                Math.Max(1, Bounds.Width / 2 - 16));
            foreach (var tick in axis.Ticks)
            {
                var y = area.Bottom - tick.Position * area.Height;
                context.DrawLine(new Pen(Palette.Muted, 1), new(left ? area.Left - 4 : area.Right, y), new(left ? area.Left : area.Right + 4, y));
                DrawingText.Draw(context, tick.Text, new(left ? 8 : area.Right + 8, y - 7), Palette.Muted, 10, 88);
            }
            context.DrawLine(new Pen(Palette.Muted, 1), new(left ? area.Left : area.Right, area.Top), new(left ? area.Left : area.Right, area.Bottom));
        }
        if (snapshot is not null)
        {
            using (context.PushClip(area))
            {
                // Horizontal grids are deliberately omitted: matching pixel heights do not mean equal units.
                foreach (var series in snapshot.Series.Where(s => s.IsVisible))
                {
                    var axis = snapshot.Axes.First(a => a.Definition.Id == series.Definition.AxisId);
                    if (axis.Domain is not { } domain) continue;
                    var pen = SeriesPen(series.Definition.Id, axis.Definition.Id);
                    foreach (var segment in series.Segments)
                    {
                        HistorySample? previous = null;
                        foreach (var sample in segment.Points)
                        {
                            if (previous is { } prior)
                            {
                                var edge = DualAxisChartGeometry.ClipSegment(prior, sample.Sample, snapshot.Window, domain, area.X, area.Y, area.Width, area.Height);
                                if (edge is { } clipped) context.DrawLine(pen, new(clipped.Start.X, clipped.Start.Y), new(clipped.End.X, clipped.End.Y));
                            }
                            else if (sample.Value >= domain.Minimum && sample.Value <= domain.Maximum)
                            {
                                var point = DualAxisChartGeometry.Project(sample.UtcTicks, sample.Value, snapshot.Window, domain, area.X, area.Y, area.Width, area.Height);
                                context.DrawEllipse(SeriesBrush(series.Definition.Id), null, new(point.X, point.Y), 2, 2);
                            }
                            previous = sample.Sample;
                        }
                    }
                }
                if (_selection is { Value: { } selectedValue } selected && snapshot.Window.Contains(selected.UtcTicks))
                {
                    var series = snapshot.Series.First(s => s.Definition.Id == selected.SeriesId);
                    var axis = snapshot.Axes.First(a => a.Definition.Id == series.Definition.AxisId);
                    if (axis.Domain is { } domain && selectedValue >= domain.Minimum && selectedValue <= domain.Maximum)
                    {
                        var geometry = DualAxisChartGeometry.Project(selected.UtcTicks, selectedValue, snapshot.Window, domain, area.X, area.Y, area.Width, area.Height);
                        var point = new Point(geometry.X, geometry.Y);
                        context.DrawLine(new Pen(Palette.Muted, 1), new(point.X, area.Top), new(point.X, area.Bottom));
                        context.DrawEllipse(Palette.Surface, SeriesPen(selected.SeriesId, series.Definition.AxisId, 2), point, 5, 5);
                    }
                }
                if (snapshot.PointCount == 0) DrawingText.Draw(context, Status, new(area.Left + 10, area.Top + 12), Palette.Muted, 12, Math.Max(1, area.Width - 20));
            }
            var half = Math.Max(1, area.Width / 2 - 8);
            DrawingText.DrawTimestamp(context, AxisStartLabel, new(area.Left, area.Bottom + 10), Palette.Muted, 10, half);
            DrawingText.DrawTimestamp(context, AxisEndLabel, new(area.Left + area.Width / 2 + 8, area.Bottom + 10), Palette.Muted, 10, half);
        }
        else DrawingText.Draw(context, Status, new(16, 70), Palette.Muted, 12, Math.Max(1, Bounds.Width - 32));
        DrawingText.Draw(context, Status, new(12, Bounds.Height - 43), Palette.Muted, 11, Math.Max(1, Bounds.Width - 24));
        if (_selection is not null) DrawingText.Draw(context, SelectedDisplayText,
            new(12, Bounds.Height - 24), Palette.Text, 11, Math.Max(1, Bounds.Width - 24));
    }
}
