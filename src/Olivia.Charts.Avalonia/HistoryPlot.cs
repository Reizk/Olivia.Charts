using System.Globalization;
using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Olivia.Core.Charts;
using Olivia.Core.Display;
using Olivia.Core.Export;
using Olivia.Core.Snapshots;

namespace Olivia.Avalonia;

/// <summary>Bounded historical-line adapter. The immutable source is borrowed, never disposed.</summary>
public sealed class HistorySourceChangedEventArgs(long previousIdentity, long sourceIdentity, bool resetView, long requestRevision) : EventArgs
{
    public long PreviousIdentity { get; } = previousIdentity;
    public long SourceIdentity { get; } = sourceIdentity;
    public bool ResetView { get; } = resetView;
    public long RequestRevision { get; } = requestRevision;
}

public sealed class HistoryPlot : Control
{
    private readonly object _gate = new();
    private HistorySeries? _series;
    private readonly VersionedSnapshotStore<HistorySeries> _sources;
    private readonly Func<HistorySeries, TimeWindow, int, CancellationToken, Task<HistoryChartSnapshot>> _query;
    private SnapshotLease<HistorySeries>? _displayedSource;
    private long _sourceIdentity;
    private RenderRequest? _request;
    private RenderFrame? _pendingFrame;
    private bool _workerRunning, _notificationPending, _attached;
    private long _revision, _epoch;
    private Task _idle = Task.CompletedTask;
    private UiPalette _palette = UiPalette.Dark;
    private HistorySample? _selection;
    private ValueDisplayFormat _displayFormat = ValueDisplayFormat.Default;
    private double _axisLabelWidth = 54;
    private Point? _dragStart;
    private TimeWindow? _dragWindow;

    private sealed record RenderRequest(SnapshotLease<HistorySeries> Source, TimeWindow Window, int Budget, long Revision, long Epoch);
    private sealed record RenderFrame(RenderRequest Request, HistoryChartSnapshot? Snapshot, string? Error);

    public HistoryPlot() : this(new SnapshotBudget(4, 4_000_000, 4)) { }

    /// <summary>The query delegate is borrowed; a single worker and its source lease stay active until it returns.</summary>
    public HistoryPlot(SnapshotBudget sourceBudget,
        Func<HistorySeries, TimeWindow, int, CancellationToken, Task<HistoryChartSnapshot>>? query = null)
    {
        ArgumentNullException.ThrowIfNull(sourceBudget);
        // Display + running query + newest queued query must coexist. Smaller budgets cannot
        // guarantee forward progress after a slow query, so reject them before retaining a source.
        if (sourceBudget.MaximumLeases < 3)
            throw new ArgumentException("A chart requires at least three source leases (display, running, latest).", nameof(sourceBudget));
        _sources = new(sourceBudget);
        _query = query ?? ((series, window, budget, token) => Task.FromResult(series.Query(window, budget, token)));
        Focusable = true;
        ClipToBounds = true;
        MinHeight = 180;
        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) => InvalidateVisual();
        AutomationProperties.SetName(this, "Historical line. Arrow keys pan, plus and minus zoom, Home resets.");
    }

    public HistoryChartSnapshot? Snapshot { get; private set; }
    internal HistorySeries? AcceptedSeries => _series;
    public long SourceIdentity => _sourceIdentity;
    public long RequestRevision { get { lock (_gate) return _revision; } }
    public event EventHandler<HistorySourceChangedEventArgs>? SourceChanged;
    public string? NotificationError { get; private set; }
    /// <summary>The immutable data geometry consumed by Render for the committed snapshot.</summary>
    public HistoryPlotGeometry? Geometry { get; private set; }
    public SnapshotStoreStatistics SourceStatistics => _sources.Statistics;
    public SnapshotPublishStatus LastSourceStatus { get; private set; } = SnapshotPublishStatus.Published;
    public TimeWindow? ViewWindow { get; private set; }
    public HistorySample? SelectedSample => _selection;
    public int PointBudget { get; set; } = 800;
    public string Status { get; private set; } = "No source";
    public string AccessibleSummary => _selection is { } sample
        ? $"{Status}. {SelectedDisplayText}; UTC ticks {sample.UtcTicks.ToString(CultureInfo.InvariantCulture)}, original value {sample.Value?.ToString("R", CultureInfo.InvariantCulture) ?? "missing"}" : Status;
    public string AxisStartLabel => Snapshot is { } snapshot ? DisplayFormat.FormatTimestamp(snapshot.Window.StartTicks) : "";
    public string AxisEndLabel => Snapshot is { } snapshot ? DisplayFormat.FormatTimestamp(snapshot.Window.EndTicks - 1) : "";
    public string SelectedDisplayText => _selection is { } sample
        ? $"Selected key {sample.Key.ToString(CultureInfo.InvariantCulture)} · {DisplayFormat.FormatTimestamp(sample.UtcTicks)} · value {DisplayFormat.FormatNumber(sample.Value)}" : "";
    /// <summary>Presentation only: never re-queries or changes source/selection/viewport ownership.</summary>
    public ValueDisplayFormat DisplayFormat
    {
        get => _displayFormat;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_displayFormat, value)) return;
            _displayFormat = value;
            UpdateSelectionPresentation();
            InvalidateVisual();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private void UpdateSelectionPresentation()
    {
        AutomationProperties.SetHelpText(this, AccessibleSummary);
        ToolTip.SetTip(this, _selection is null ? null : AccessibleSummary);
    }
    public event EventHandler? StateChanged;
    public event EventHandler? ViewChanged;
    public Task WhenIdle { get { lock (_gate) return _idle; } }
    public UiPalette Palette { get => _palette; set { _palette = value; InvalidateVisual(); } }

    /// <summary>On budget rejection the previously accepted source stays visible; the candidate is not retained.</summary>
    public SnapshotPublishStatus SetSeries(HistorySeries series, bool resetView = true)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(series);
        var previousIdentity = _sourceIdentity;
        SnapshotPublishStatus acceptedStatus;
        lock (_gate)
        {
            DropQueuedLocked();
            acceptedStatus = LastSourceStatus = _sources.Publish(checked(_sourceIdentity + 1), series, series.Count);
            if (LastSourceStatus == SnapshotPublishStatus.Published)
            {
                _sourceIdentity++;
                _series = series;
            }
        }
        if (LastSourceStatus != SnapshotPublishStatus.Published)
        {
            // Dropping the replaceable latest slot made room for admission. If admission fails,
            // restore a render of the last accepted source instead of silently losing that work.
            QueueRender();
            Status = $"Source budget exceeded; showing version {Snapshot?.SourceVersion.ToString(CultureInfo.InvariantCulture) ?? "none"}";
            InvalidateVisual();
            return LastSourceStatus;
        }
        ViewWindow = resetView || ViewWindow is null ? series.Extent : series.Extent is { } extent ? ViewWindow.Value.ClampTo(extent) : null;
        QueueRender();
        NotifySourceChanged(new(previousIdentity, _sourceIdentity, resetView, _revision));
        return acceptedStatus;
    }

    /// <summary>Terminal source release, separate from reusable detach. Active work stays counted until it returns.</summary>
    public void ReleaseSource()
    {
        Dispatcher.UIThread.VerifyAccess();
        var previousIdentity = _sourceIdentity;
        lock (_gate)
        {
            _revision++;
            _epoch++;
            DropQueuedLocked();
            _displayedSource?.Dispose();
            _displayedSource = null;
            _series = null;
            _sourceIdentity++;
            Snapshot = null;
            Geometry = null;
            ViewWindow = null;
            _selection = null;
            _sources.Reset();
            Status = "No source";
        }
        UpdateSelectionPresentation();
        InvalidateVisual();
        NotifySourceChanged(new(previousIdentity, _sourceIdentity, true, _revision));
    }

    // Admission and requested state are already committed before observers run. A bad observer
    // is reported separately and cannot convert successful admission into a false failure.
    private void NotifySourceChanged(HistorySourceChangedEventArgs args)
    {
        NotificationError = null;
        if (SourceChanged is not { } handlers) return;
        foreach (EventHandler<HistorySourceChangedEventArgs> handler in handlers.GetInvocationList())
        {
            if (_sourceIdentity != args.SourceIdentity) break;
            try { handler(this, args); }
            catch (Exception exception) { NotificationError = exception.Message; }
        }
    }
    private void NotifyViewChanged()
    {
        NotificationError = null;
        var identity = _sourceIdentity; var revision = _revision;
        if (ViewChanged is not { } handlers) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            if (_sourceIdentity != identity || _revision != revision) break;
            try { handler(this, EventArgs.Empty); }
            catch (Exception exception) { NotificationError = exception.Message; }
        }
    }

    /// <summary>Sets the exact requested UTC interval, clamped by the existing duration-preserving policy.
    /// An equal effective interval does not enqueue work or raise a view event.</summary>
    public bool SetViewWindow(TimeWindow window)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_series?.Extent is not { } extent) return false;
        var next = window.ClampTo(extent);
        if (ViewWindow == next) return false;
        ViewWindow = next;
        QueueRender();
        NotifyViewChanged();
        return true;
    }

    public void ResetView()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_series?.Extent is { } extent) SetViewWindow(extent);
    }
    public void Zoom(double scale, long? anchorTicks = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (ViewWindow is not { } window || _series?.Extent is not { } extent) return;
        SetViewWindow(window.Zoom(scale, anchorTicks ?? window.StartTicks + window.DurationTicks / 2, extent));
    }
    public void Pan(double fraction)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!double.IsFinite(fraction)) throw new ArgumentOutOfRangeException(nameof(fraction));
        if (ViewWindow is not { } window || _series?.Extent is not { } extent) return;
        var requestedDelta = window.DurationTicks * fraction;
        var delta = requestedDelta >= long.MaxValue ? long.MaxValue : requestedDelta <= long.MinValue ? long.MinValue : (long)requestedDelta;
        SetViewWindow(window.Pan(delta, extent));
    }
    public HistorySample? SelectNearest(long utcTicks)
    {
        _selection = _displayedSource is { } lease && lease.TryGetValue(out var source)
            ? source!.FindNearest(utcTicks, Snapshot?.Window) : null;
        UpdateSelectionPresentation();
        InvalidateVisual();
        StateChanged?.Invoke(this, EventArgs.Empty);
        return _selection;
    }

    private void DropQueuedLocked()
    {
        _request?.Source.Dispose();
        _request = null;
        _pendingFrame?.Request.Source.Dispose();
        _pendingFrame = null;
    }

    private void QueueRender()
    {
        lock (_gate)
        {
            _revision++;
            DropQueuedLocked();
            if (!_attached || _series is null || ViewWindow is null)
            {
                if (_series?.Count == 0)
                {
                    Snapshot = null;
                    Geometry = null;
                    _displayedSource?.Dispose();
                    _displayedSource = null;
                    _selection = null;
                    Status = "No samples in this source";
                    UpdateSelectionPresentation();
                }
                InvalidateVisual();
                return;
            }
            var acquisition = _sources.Acquire(_sourceIdentity, SnapshotOwner.Pending);
            if (acquisition.Lease is not { } lease)
            {
                Status = $"Snapshot {acquisition.Status}; showing version {Snapshot?.SourceVersion.ToString(CultureInfo.InvariantCulture) ?? "none"}";
                return;
            }
            _request = new(lease, ViewWindow.Value, PointBudget, _revision, _epoch);
            Status = Snapshot is null ? "Loading…" : FormattableString.Invariant($"Loading… showing older version {Snapshot.SourceVersion}");
            if (!_workerRunning)
            {
                _workerRunning = true;
                _idle = Task.Run(RenderLoopAsync);
            }
        }
        InvalidateVisual();
    }

    private async Task RenderLoopAsync()
    {
        while (true)
        {
            RenderRequest? request;
            lock (_gate)
            {
                request = _request;
                _request = null;
                if (request is null) { _workerRunning = false; return; }
                request.Source.TransferTo(SnapshotOwner.Query);
            }
            RenderFrame frame;
            try
            {
                var snapshot = await _query(request.Source.Value, request.Window, request.Budget, CancellationToken.None).ConfigureAwait(false);
                if (snapshot.SourceVersion != request.Source.Value.SourceVersion || snapshot.Window != request.Window || snapshot.PointBudget != request.Budget)
                    throw new InvalidOperationException("The history provider returned a different source or query.");
                frame = new(request, snapshot, null);
            }
            catch (Exception exception) { frame = new(request, null, exception.Message); }
            lock (_gate)
            {
                if (!_attached || request.Epoch != _epoch || request.Revision != _revision)
                {
                    request.Source.Dispose();
                    continue;
                }
                request.Source.TransferTo(SnapshotOwner.Pending);
                _pendingFrame?.Request.Source.Dispose();
                _pendingFrame = frame;
                if (!_notificationPending)
                {
                    _notificationPending = true;
                    Dispatcher.UIThread.Post(CommitLatest, DispatcherPriority.Background);
                }
            }
        }
    }

    private void CommitLatest()
    {
        RenderFrame? frame;
        lock (_gate)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            _notificationPending = false;
            if (!_attached || frame is null || frame.Request.Epoch != _epoch || frame.Request.Revision != _revision)
            {
                frame?.Request.Source.Dispose();
                return;
            }
        }
        if (frame.Error is not null)
        {
            frame.Request.Source.Dispose();
            Status = $"Chart error: {frame.Error}";
        }
        else if (!frame.Request.Source.TransferTo(SnapshotOwner.Display))
        {
            frame.Request.Source.Dispose();
            Status = "Snapshot expired; refresh the source";
        }
        else
        {
            Snapshot = frame.Snapshot;
            Geometry = HistoryPlotGeometry.Create(Snapshot!);
            _displayedSource?.Dispose();
            _displayedSource = frame.Request.Source;
            if (_selection is { } selected)
            {
                _selection = null;
                foreach (var candidate in _displayedSource.Value.Samples)
                    if (candidate.Key == selected.Key) { if (!candidate.IsGap) _selection = candidate; break; }
            }
            Status = Snapshot!.BudgetStatus == ChartBudgetStatus.InsufficientBudget
                ? FormattableString.Invariant($"Budget {PointBudget} is too small; at least {Snapshot.RequiredPointBudget} points needed to preserve gaps")
                : FormattableString.Invariant($"Version {Snapshot.SourceVersion} · {Snapshot.RawSampleCount:N0} raw / {Snapshot.PointCount:N0} display · {Snapshot.Gaps.Length} gaps");
        }
        UpdateSelectionPresentation();
        InvalidateVisual();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        lock (_gate) { _attached = true; _epoch++; }
        QueueRender();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _dragStart = null; _dragWindow = null;
        lock (_gate) { _attached = false; _epoch++; _revision++; DropQueuedLocked(); }
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragStart = e.GetPosition(this);
            _dragWindow = ViewWindow;
            e.Pointer.Capture(this);
        }
        if (Snapshot?.Window is { } window)
        {
            var area = PlotArea;
            var fraction = Math.Clamp((e.GetPosition(this).X - area.X) / Math.Max(1, area.Width), 0, 1);
            SelectNearest(window.StartTicks + (long)(fraction * Math.Max(0, window.DurationTicks - 1)));
        }
        e.Handled = true;
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        long? anchor = null;
        if (ViewWindow is { } window)
        {
            var fraction = Math.Clamp((e.GetPosition(this).X - PlotArea.X) / PlotArea.Width, 0, 1);
            anchor = window.StartTicks + (long)(fraction * Math.Max(0, window.DurationTicks - 1));
        }
        Zoom(e.Delta.Y > 0 ? 1.3 : 1 / 1.3, anchor);
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is not { } start || _dragWindow is not { } window || _series?.Extent is not { } extent) return;
        var deltaPixels = e.GetPosition(this).X - start.X;
        if (Math.Abs(deltaPixels) < 3) return;
        var delta = -deltaPixels / PlotArea.Width * window.DurationTicks;
        var ticks = delta >= long.MaxValue ? long.MaxValue : delta <= long.MinValue ? long.MinValue : (long)delta;
        SetViewWindow(window.Pan(ticks, extent));
        e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragStart = null; _dragWindow = null;
        e.Pointer.Capture(null);
        base.OnPointerReleased(e);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _dragStart = null; _dragWindow = null;
        base.OnPointerCaptureLost(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left: Pan(-0.2); break;
            case Key.Right: Pan(0.2); break;
            case Key.Add: case Key.OemPlus: Zoom(1.5); break;
            case Key.Subtract: case Key.OemMinus: Zoom(1 / 1.5); break;
            case Key.Home: ResetView(); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Presentation-only label gutter. The default preserves the original layout.</summary>
    public double AxisLabelWidth
    {
        get => _axisLabelWidth;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (!double.IsFinite(value) || value < 54 || value > 240) throw new ArgumentOutOfRangeException(nameof(value));
            _axisLabelWidth = value;
            InvalidateVisual();
        }
    }
    /// <summary>The exact local rectangle used by rendering and pointer mapping.</summary>
    public Rect PlotBounds => new(AxisLabelWidth + 10, 24, Math.Max(1, Bounds.Width - AxisLabelWidth - 34), Math.Max(1, Bounds.Height - 84));
    private Rect PlotArea => PlotBounds;

    // This short-lived token never leaves the synchronous exporter. The completed export owns
    // only copied data/pixels, not this source lease or the control's source graph.
    internal sealed record HistoryExportFrame(HistoryExportSnapshot Data, object SourceIdentity,
        HistoryChartSnapshot Shown, HistoryPlotGeometry Geometry, ValueDisplayFormat Format,
        UiPalette Palette, string Status, HistorySample? Selected, Rect Bounds, Rect PlotBounds,
        double LabelWidth, bool Focused, long Epoch);

    internal string ExportDetailText => _selection is not null ? SelectedDisplayText
        : "Click a point for its original value · wheel or + / − to zoom · arrows to pan";

    internal HistoryExportFrame CaptureExportFrame(SnapshotExportLimits? limits, CancellationToken token)
    {
        Dispatcher.UIThread.VerifyAccess();
        token.ThrowIfCancellationRequested();
        var shown = Snapshot ?? throw new InvalidOperationException("No displayed history snapshot to export.");
        var geometry = Geometry ?? throw new InvalidOperationException("No displayed history geometry to export.");
        var lease = _displayedSource;
        if (lease is null || !lease.TryGetValue(out var source) || source is null)
            throw new InvalidOperationException("The displayed history source is no longer available.");
        var format = DisplayFormat; var palette = Palette; var selected = _selection;
        var status = Status; var bounds = Bounds; var plot = PlotBounds;
        var width = AxisLabelWidth; var focused = IsFocused; var epoch = _epoch;
        var axesDrawn = geometry.Nodes.Length != 0;
        // Empty [M,M) windows must never attempt DateTime(M) via the unused axis-label getters.
        var presentation = new HistoryExportPresentation(axesDrawn,
            axesDrawn ? AxisStartLabel : "", axesDrawn ? AxisEndLabel : "",
            axesDrawn ? geometry.Ticks.Select(t => new HistoryExportTick(t.Value, t.Position,
                format.FormatNumber(t.Value))).ToImmutableArray() : [],
            status, selected, SelectedDisplayText, axesDrawn ? ExportDetailText : "");
        var data = HistorySnapshotExport.Capture(source, shown, geometry, format, presentation, limits, token);
        return new(data, lease, shown, geometry, format, palette, status, selected, bounds, plot, width, focused, epoch);
    }

    internal bool IsExportFrameCurrent(HistoryExportFrame frame) =>
        _attached && ReferenceEquals(_displayedSource, frame.SourceIdentity) && _epoch == frame.Epoch &&
        ReferenceEquals(Snapshot, frame.Shown) && ReferenceEquals(Geometry, frame.Geometry) &&
        ReferenceEquals(DisplayFormat, frame.Format) && ReferenceEquals(Palette, frame.Palette) &&
        Status == frame.Status && _selection == frame.Selected && Bounds == frame.Bounds &&
        PlotBounds == frame.PlotBounds && AxisLabelWidth == frame.LabelWidth && IsFocused == frame.Focused &&
        IsMeasureValid && IsArrangeValid;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Palette.Surface, new Rect(Bounds.Size));
        if (IsFocused) context.DrawRectangle(null, new Pen(Palette.Accent, 2), new Rect(Bounds.Size).Deflate(1));
        var area = PlotArea;
        var gridPen = new Pen(Palette.GridLine, 1);
        for (var i = 0; i <= 4; i++)
        {
            var y = area.Top + area.Height * i / 4;
            context.DrawLine(gridPen, new(area.Left, y), new(area.Right, y));
        }
        var snapshot = Snapshot;
        var geometry = Geometry;
        if (snapshot is null || geometry is null || geometry.Nodes.Length == 0)
        {
            DrawingText.Draw(context, Status, new(18, 40), Palette.Muted, 13, Math.Max(1, Bounds.Width - 36));
            return;
        }
        Point Project(HistoryChartPoint point) => new(area.X + point.X * area.Width,
            area.Bottom - point.Y * area.Height);
        using (context.PushClip(area))
        {
            var line = new Pen(Palette.Accent, 1.8);
            for (var i = 0; i < geometry.Segments.Length; i++)
            {
                var segment = geometry.Segments[i];
                context.DrawEllipse(Palette.Accent, null, Project(geometry.Markers[i].Position), 2, 2);
                for (var edgeIndex = segment.FirstEdgeIndex; edgeIndex < segment.FirstEdgeIndex + segment.EdgeCount; edgeIndex++)
                {
                    var edge = geometry.DataEdges[edgeIndex];
                    context.DrawLine(line, Project(edge.Start.Position), Project(edge.End.Position));
                }
            }
            if (_selection is { Value: { } selectedValue } selected && snapshot.Window.Contains(selected.UtcTicks))
            {
                var point = Project(geometry.Normalize(selected.UtcTicks, selectedValue));
                context.DrawLine(new Pen(Palette.Muted, 1), new(point.X, area.Top), new(point.X, area.Bottom));
                context.DrawEllipse(Palette.Surface, new Pen(Palette.Accent, 2), point, 5, 5);
            }
        }
        foreach (var tick in geometry.Ticks)
        {
            DrawingText.Draw(context, DisplayFormat.FormatNumber(tick.Value), new(8, area.Top + area.Height * (1 - tick.Position) - 7), Palette.Muted, 10, AxisLabelWidth);
        }
        // Separate bounded halves prevent long localized date labels from painting over each other.
        var labelWidth = Math.Max(1, area.Width / 2 - 8);
        DrawingText.DrawTimestamp(context, AxisStartLabel, new(area.Left, area.Bottom + 8), Palette.Muted, 10, labelWidth);
        DrawingText.DrawTimestamp(context, AxisEndLabel, new(area.Left + area.Width / 2 + 8, area.Bottom + 8), Palette.Muted, 10, labelWidth);
        var detail = ExportDetailText;
        DrawingText.Draw(context, detail, new(16, Bounds.Height - 22), Palette.Text, 11, Math.Max(1, Bounds.Width - 32));
    }
}
