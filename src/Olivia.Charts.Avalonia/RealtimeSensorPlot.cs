using System.Globalization;
using System.Collections.Immutable;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Olivia.Core.Charts;
using Olivia.Core.Workspaces;

namespace Olivia.Avalonia;

/// <summary>An original reading owned by the displayed, not the newest produced, frame.</summary>
public sealed record RealtimeSensorSelection(long Generation, long SourceVersion, int SeriesIndex,
    HistorySample Sample);

/// <summary>
/// UI-thread snapshot; counters and maxima span the control's lifetime, including stop/restart.
/// RenderCallbacks includes empty/small-viewport callbacks; DataRenderCallbacks and render timings
/// include only completed four-pane data drawing. DistinctFrameRenders counts the first completed
/// drawing of an accepted generation/revision/source-version, excluding repeat repaints.
/// Commit-to-render measures commit to that first callback's completion. These are CPU drawing
/// command timings, not GPU completion, monitor presentation, or FPS. Preparation is path building only.
/// </summary>
public readonly record struct RealtimeSensorRenderStatistics(long AcceptedFrames, long RenderCallbacks,
    long DataRenderCallbacks, long DistinctFrameRenders, double LastRenderMilliseconds,
    double MaximumRenderMilliseconds, double LastCommitToRenderMilliseconds,
    double MaximumCommitToRenderMilliseconds, double LastPreparationMilliseconds,
    double MaximumPreparationMilliseconds);

/// <summary>
/// Display-only, independent-pane Y grid. Values are multiples of 20, not twenty divisions of
/// the data range. Positions come from the unchanged Core normalization, including its padding.
/// Ticks and Labels are top-to-bottom; labels are a readable subset of the grid, not a new grid.
/// When a full grid would exceed 256 lines or binary64 cannot represent its 20-unit steps exactly,
/// IsLimited explicitly reports the fallback: only zero is retained if visible. No substitute
/// interval or artificial endpoint ticks are introduced. A narrow domain may contain no aligned ticks.
/// LabelOffset is explicitly annotated by the renderer; add it to each formatted label to obtain Y.
/// </summary>
public readonly record struct RealtimeSensorValueAxis(ImmutableArray<HistoryAxisTick> Ticks,
    ImmutableArray<HistoryAxisTick> Labels, bool IsLimited, double LabelOffset)
{
    public const double GridSpacing = 20;
    public const int MaximumGridTickCount = 256;
    public const double MinimumLabelSpacing = 14;
    // 20 * index is an exactly representable multiple of four through this bound (below 2^55).
    // Larger magnitudes need not represent adjacent 20-unit grid values distinctly or exactly.
    private const long MaximumExactTickIndex = 1_801_439_850_948_198;

    public static RealtimeSensorValueAxis Create(HistoryPlotGeometry geometry, double paneHeight)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (!double.IsFinite(paneHeight) || paneHeight < 0)
            throw new ArgumentOutOfRangeException(nameof(paneHeight));
        if (geometry.RawDomain is null || geometry.DisplayDomain is not { } domain)
            return new([], [], false, 0);

        HistoryAxisTick? At(double value)
        {
            var position = geometry.Normalize(geometry.Window.StartTicks, value).Y;
            return double.IsFinite(position) && position >= 0 && position <= 1
                ? new HistoryAxisTick(value, position) : null;
        }
        RealtimeSensorValueAxis Finish(ImmutableArray<HistoryAxisTick> gridTicks, bool limited)
        {
            // Integer labels remain distinct in a narrow domain near a large common offset;
            // G5 alone would turn, for example, 1e12 and 1e12+20 into identical text.
            var offset = !gridTicks.IsEmpty && Math.Abs(gridTicks[0].Value) >= 1_000_000
                ? Math.Truncate(gridTicks[0].Value / 1_000_000) * 1_000_000 : 0;
            var labels = ImmutableArray.CreateBuilder<HistoryAxisTick>();
            var lastY = double.NegativeInfinity;
            if (paneHeight >= MinimumLabelSpacing)
            {
                foreach (var tick in gridTicks)
                {
                    var y = (1 - tick.Position) * paneHeight;
                    if (y - lastY < MinimumLabelSpacing) continue;
                    labels.Add(tick);
                    lastY = y;
                }
            }
            return new(gridTicks, labels.ToImmutable(), limited, offset);
        }
        RealtimeSensorValueAxis Limited() => Finish(At(0) is { } zero
            ? ImmutableArray.Create(zero) : [], true);

        if (!double.IsFinite(domain.Minimum) || !double.IsFinite(domain.Maximum)) return Limited();
        // Rounded display bounds are labels, not the logical domain. One extra index on each
        // side covers boundary rounding; At filters with the actual unchanged normalization.
        var first = Math.Floor(domain.Minimum / GridSpacing) - 1;
        var last = Math.Ceiling(domain.Maximum / GridSpacing) + 1;
        if (first < -MaximumExactTickIndex || last > MaximumExactTickIndex || first > last ||
            last - first + 1 > MaximumGridTickCount + 4) return Limited();

        var ticks = ImmutableArray.CreateBuilder<HistoryAxisTick>();
        // At most 260 candidates, independent of domain magnitude. Integer index stepping
        // cannot stall from floating-point addition, and never subtracts the raw domain range.
        for (var index = (long)last; index >= (long)first; index--)
        {
            if (At(index * GridSpacing) is not { } tick) continue;
            ticks.Add(tick);
            if (ticks.Count > MaximumGridTickCount) return Limited();
        }
        return Finish(ticks.ToImmutable(), false);
    }

    public string FormatLabel(HistoryAxisTick tick) =>
        (tick.Value - LabelOffset).ToString("0", CultureInfo.InvariantCulture);
}

/// <summary>
/// Eight batched, normalized platform paths, cached once per accepted frame. The source's
/// sampling and normalization already ran in Core's worker. Only the owning control may update transforms.
/// </summary>
public sealed class RealtimeSensorRenderFrame
{
    private RealtimeSensorRenderFrame(RealtimeSensorFrame source, ImmutableArray<StreamGeometry> paths,
        double preparationMilliseconds)
    { Source = source; Paths = paths; PreparationMilliseconds = preparationMilliseconds; }
    public RealtimeSensorFrame Source { get; }
    /// <summary>Elapsed CPU-side path preparation, excluding Core sampling and normalization.</summary>
    public double PreparationMilliseconds { get; }
    internal ImmutableArray<StreamGeometry> Paths { get; }

    public static RealtimeSensorRenderFrame Prepare(RealtimeSensorFrame source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var started = Stopwatch.GetTimestamp();
        var paths = ImmutableArray.CreateBuilder<StreamGeometry>(8);
        foreach (var pane in source.Panes)
        foreach (var series in pane.Series)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                var index = 0;
                foreach (var segment in series.Snapshot.Segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (segment.Points.IsEmpty) continue;
                    var first = series.Nodes[index++].Position;
                    path.BeginFigure(new(first.X, 1 - first.Y), false);
                    for (var point = 1; point < segment.Points.Length; point++)
                    {
                        var position = series.Nodes[index++].Position;
                        path.LineTo(new(position.X, 1 - position.Y));
                    }
                    path.EndFigure(false);
                }
            }
            paths.Add(geometry);
        }
        return new(source, paths.MoveToImmutable(), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}

/// <summary>
/// Four independent value axes and one UTC axis. The session prepares the immutable sampled
/// lines and the common Y domain of each pair off-thread. This control does no history query,
/// sorting, retention or timer scheduling on the UI thread. It marshals bounded normalized
/// nodes into eight cached Avalonia paths once at commit, not on every render.
/// </summary>
public sealed class RealtimeSensorPlot : Control
{
    private static readonly IBrush FirstSensorBrush = new SolidColorBrush(Color.Parse("#61D5C3"));
    private static readonly IBrush SecondSensorBrush = new SolidColorBrush(Color.Parse("#FFBE72"));
    private UiPalette _palette = UiPalette.Dark;
    private Point? _dragStart;
    private TimeWindow? _dragWindow;
    private IPointer? _dragPointer;
    private Window? _owner;
    private RealtimeSensorRenderFrame? _renderFrame;
    private Size _pathViewport;
    private long _acceptedFrames, _renderCallbacks, _dataRenderCallbacks, _distinctFrameRenders;
    private long _committedTimestamp, _renderedGeneration, _renderedRevision, _renderedSourceVersion;
    private bool _hasRenderedIdentity;
    private double _lastRenderMilliseconds, _maximumRenderMilliseconds;
    private double _lastCommitToRenderMilliseconds, _maximumCommitToRenderMilliseconds;
    private double _lastPreparationMilliseconds, _maximumPreparationMilliseconds;

    public RealtimeSensorPlot()
    {
        Focusable = true;
        ClipToBounds = true;
        MinHeight = 420;
        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) => { EndDrag(); InvalidateVisual(); };
        AutomationProperties.SetName(this,
            "Four-pane real-time sensor chart. Temperature, Pressure, Power, Intensity. " +
            "Each pane has Sensor Set 1 and Sensor Set 2 and its own Y axis. " +
            "Wheel or plus and minus zoom the shared UTC axis. Drag or arrows pan. Home follows latest.");
    }

    public RealtimeSensorFrame? Frame { get; private set; }
    public RealtimeSensorSelection? Selection { get; private set; }
    /// <summary>Read on the UI thread. This value snapshot does not retain any frame.</summary>
    public RealtimeSensorRenderStatistics RenderStatistics => new(_acceptedFrames, _renderCallbacks,
        _dataRenderCallbacks, _distinctFrameRenders, _lastRenderMilliseconds, _maximumRenderMilliseconds,
        _lastCommitToRenderMilliseconds, _maximumCommitToRenderMilliseconds,
        _lastPreparationMilliseconds, _maximumPreparationMilliseconds);
    public UiPalette Palette
    {
        get => _palette;
        set { ArgumentNullException.ThrowIfNull(value); _palette = value; InvalidateVisual(); }
    }
    public event EventHandler? StateChanged;
    public event Action<double, long>? ZoomRequested;
    public event Action<long>? PanRequested;
    public event Action<TimeWindow>? WindowRequested;
    public event EventHandler? FollowRequested;

    public string Status => Frame is { } frame
        ? FormattableString.Invariant($"4 panes · 8 series · shown v{frame.SourceVersion} · {frame.Window.DurationTicks / (double)TimeSpan.TicksPerSecond:0.###} s · {(frame.IsFollowingLatest ? "following latest" : "manual window")}")
        : "Ready. Start to display four synchronized sensor panes.";

    public string SelectionText
    {
        get
        {
            if (Selection is not { } hit || Frame is not { } frame)
                return "Click a line to read its original key and value from the shown data version.";
            var pane = frame.Panes[hit.SeriesIndex / 2];
            var line = pane.Series[hit.SeriesIndex % 2];
            return $"{pane.Name} / {line.Name} · key {Invariant(hit.Sample.Key)} · " +
                $"{Timestamp(hit.Sample.UtcTicks)} UTC · original {Value(hit.Sample.Value)} · " +
                $"shown v{Invariant(hit.SourceVersion)}";
        }
    }

    public bool IsPlotViewportAvailable => Bounds.Width >= 440 && Bounds.Height >= 410;

    /// <summary>The exact independent pane rectangle used both by drawing and pointer mapping.</summary>
    public Rect PanePlotBounds(int paneIndex)
    {
        if (paneIndex is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(paneIndex));
        var stride = Math.Max(1, (Bounds.Height - 50) / 4);
        return new Rect(88, paneIndex * stride + 28, Math.Max(1, Bounds.Width - 108),
            Math.Max(1, stride - 40));
    }

    /// <summary>
    /// Commits already-normalized data, marshaling its bounded points into cached platform paths once.
    /// Older generations, interaction revisions and source versions cannot replace a newer frame.
    /// </summary>
    public bool SetFrame(RealtimeSensorFrame frame)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(frame);
        return !IsOlderOrDuplicate(frame) && SetFrame(RealtimeSensorRenderFrame.Prepare(frame));
    }

    public bool SetFrame(RealtimeSensorRenderFrame prepared)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(prepared);
        var frame = prepared.Source;
        if (IsOlderOrDuplicate(frame)) return false;
        if (frame.Panes.Length != 4 || frame.Panes.Any(pane => pane.Series.Length != 2))
            throw new ArgumentException("A real-time sensor frame must have four two-series panes.", nameof(prepared));
        var prior = Selection;
        Frame = frame;
        _renderFrame = prepared;
        _pathViewport = default;
        Selection = null;
        if (prior is not null && prior.Generation == frame.Generation &&
            frame.FindNearest(prior.SeriesIndex, prior.Sample.UtcTicks, includeGaps: true) is { } sample &&
            sample.Key == prior.Sample.Key)
            Selection = new(frame.Generation, frame.SourceVersion, prior.SeriesIndex, sample);
        _acceptedFrames++;
        _committedTimestamp = Stopwatch.GetTimestamp();
        _lastPreparationMilliseconds = prepared.PreparationMilliseconds;
        _maximumPreparationMilliseconds = Math.Max(_maximumPreparationMilliseconds, _lastPreparationMilliseconds);
        UpdatePresentation();
        InvalidateVisual();
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool IsOlderOrDuplicate(RealtimeSensorFrame frame) => Frame is { } shown &&
        (frame.Generation < shown.Generation ||
         (frame.Generation == shown.Generation && frame.Revision < shown.Revision) ||
         (frame.Generation == shown.Generation && frame.SourceVersion < shown.SourceVersion) ||
         (frame.Generation == shown.Generation && frame.Revision == shown.Revision &&
          frame.SourceVersion == shown.SourceVersion));

    public void ReleaseFrame()
    {
        Dispatcher.UIThread.VerifyAccess();
        EndDrag();
        Frame = null;
        _renderFrame = null;
        // Keep lifetime counters but no identity or timestamp that could connect separate sessions.
        _hasRenderedIdentity = false;
        _renderedGeneration = _renderedRevision = _renderedSourceVersion = _committedTimestamp = 0;
        Selection = null;
        UpdatePresentation();
        InvalidateVisual();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public HistorySample? SelectNearest(int seriesIndex, long ticks, bool includeGaps = false)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (seriesIndex is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(seriesIndex));
        Selection = Frame is { } frame && frame.FindNearest(seriesIndex, ticks, includeGaps) is { } sample
            ? new(frame.Generation, frame.SourceVersion, seriesIndex, sample) : null;
        UpdatePresentation();
        InvalidateVisual();
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Selection?.Sample;
    }

    public void Zoom(double scale, long? anchorTicks = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        EndDrag();
        if (Frame is { } frame)
            ZoomRequested?.Invoke(scale, anchorTicks ?? frame.Window.StartTicks + frame.Window.DurationTicks / 2);
    }
    public void Pan(double fraction)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!double.IsFinite(fraction)) throw new ArgumentOutOfRangeException(nameof(fraction));
        EndDrag();
        if (Frame is { } frame)
            PanRequested?.Invoke(ToTicks(frame.Window.DurationTicks * fraction));
    }
    public void FollowLatest()
    {
        Dispatcher.UIThread.VerifyAccess();
        EndDrag();
        FollowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePresentation()
    {
        AutomationProperties.SetHelpText(this, Status + ". " + SelectionText);
        ToolTip.SetTip(this, Selection is null ? null : SelectionText);
    }
    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Timestamp(long ticks) =>
        new DateTime(Math.Clamp(ticks, 0, DateTime.MaxValue.Ticks), DateTimeKind.Utc)
            .ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    private static string Value(double? value) => value.HasValue
        ? double.IsFinite(value.Value) ? value.Value.ToString("R", CultureInfo.InvariantCulture)
            : value.Value.ToString(CultureInfo.InvariantCulture) + " (gap)"
        : "missing (gap)";
    private static long ToTicks(double value) => value >= long.MaxValue ? long.MaxValue
        : value <= long.MinValue ? long.MinValue : (long)value;
    private static long AtFraction(TimeWindow window, double fraction) => window.StartTicks +
        Math.Clamp((long)(Math.Clamp(fraction, 0, 1) * Math.Max(0, window.DurationTicks - 1)),
            0, Math.Max(0, window.DurationTicks - 1));

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner = TopLevel.GetTopLevel(this) as Window;
        if (_owner is not null) _owner.Deactivated += WindowDeactivated;
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_owner is not null) _owner.Deactivated -= WindowDeactivated;
        _owner = null;
        EndDrag();
        base.OnDetachedFromVisualTree(e);
    }
    private void WindowDeactivated(object? sender, EventArgs e) => EndDrag();
    private void EndDrag()
    {
        var pointer = _dragPointer;
        _dragStart = null; _dragWindow = null; _dragPointer = null;
        if (pointer?.Captured == this) pointer.Capture(null);
    }
    private int PaneAt(Point point)
    {
        if (!IsPlotViewportAvailable) return -1;
        for (var index = 0; index < 4; index++)
            if (PanePlotBounds(index).Contains(point)) return index;
        return -1;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pointer = e.GetPosition(this);
        var paneIndex = PaneAt(pointer);
        if (paneIndex < 0 || Frame is not { } frame ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        _dragStart = pointer; _dragWindow = frame.Window; _dragPointer = e.Pointer;
        e.Pointer.Capture(this);
        var area = PanePlotBounds(paneIndex);
        var ticks = AtFraction(frame.Window, (pointer.X - area.Left) / area.Width);
        var nearestSeries = -1;
        var distance = double.PositiveInfinity;
        foreach (var series in frame.Panes[paneIndex].Series)
        {
            if (frame.Panes[paneIndex].Geometry.RawDomain is null) continue;
            if (frame.FindNearest(series.Index, ticks) is not { Value: { } value } sample) continue;
            var normalized = frame.Panes[paneIndex].Geometry.Normalize(sample.UtcTicks, value);
            var candidate = Math.Abs(area.Bottom - normalized.Y * area.Height - pointer.Y);
            if (candidate < distance) { distance = candidate; nearestSeries = series.Index; }
        }
        if (nearestSeries >= 0) SelectNearest(nearestSeries, ticks);
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is not { } start || _dragWindow is not { } window || Frame is not { } frame) return;
        if (!IsPlotViewportAvailable) { EndDrag(); return; }
        var delta = e.GetPosition(this).X - start.X;
        if (Math.Abs(delta) < 3) return;
        var shifted = window.Pan(ToTicks(-delta / PanePlotBounds(0).Width * window.DurationTicks), frame.Extent);
        WindowRequested?.Invoke(shifted);
        e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        EndDrag();
        base.OnPointerReleased(e);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        EndDrag();
        base.OnPointerCaptureLost(e);
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var position = e.GetPosition(this);
        var index = PaneAt(position);
        if (index < 0 || e.Delta.Y == 0 || Frame is not { } frame) return;
        var area = PanePlotBounds(index);
        Zoom(e.Delta.Y > 0 ? 1.3 : 1 / 1.3, AtFraction(frame.Window, (position.X - area.Left) / area.Width));
        e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left: Pan(-.2); break;
            case Key.Right: Pan(.2); break;
            case Key.Add: case Key.OemPlus: Zoom(1.5); break;
            case Key.Subtract: case Key.OemMinus: Zoom(1 / 1.5); break;
            case Key.Home: FollowLatest(); break;
            case Key.Escape: EndDrag(); break;
            default: return;
        }
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var started = Stopwatch.GetTimestamp();
        _renderCallbacks++;
        base.Render(context);
        context.FillRectangle(Palette.Surface, new Rect(Bounds.Size));
        if (IsFocused) context.DrawRectangle(null, new Pen(Palette.Accent, 2), new Rect(Bounds.Size).Deflate(1));
        if (!IsPlotViewportAvailable)
        {
            DrawingText.Draw(context, "Enlarge the chart to see four independent panes.", new(16, 24),
                Palette.Text, 13, Math.Max(1, Bounds.Width - 32));
            return;
        }
        if (Frame is not { } frame || _renderFrame is not { } prepared)
        {
            DrawingText.Draw(context, Status, new(16, 24), Palette.Text, 13, Bounds.Width - 32);
            return;
        }
        if (_pathViewport != Bounds.Size)
        {
            foreach (var pane in frame.Panes)
            {
                var viewport = PanePlotBounds(pane.Index);
                foreach (var series in pane.Series)
                    prepared.Paths[series.Index].Transform = new MatrixTransform(
                        new Matrix(viewport.Width, 0, 0, viewport.Height, viewport.Left, viewport.Top));
            }
            _pathViewport = Bounds.Size;
        }
        var gridPen = new Pen(Palette.GridLine, 1);
        for (var paneIndex = 0; paneIndex < frame.Panes.Length; paneIndex++)
        {
            var pane = frame.Panes[paneIndex];
            var area = PanePlotBounds(paneIndex);
            DrawingText.Draw(context, pane.Name + " · Y", new(12, area.Top - 24), Palette.Text, 13, area.Width / 2);
            var legendLeft = Math.Max(220, Bounds.Width - 272);
            for (var pair = 0; pair < 2; pair++)
            {
                var brush = pair == 0 ? FirstSensorBrush : SecondSensorBrush;
                var x = legendLeft + pair * 132;
                context.DrawLine(new Pen(brush, 2), new(x, area.Top - 15), new(x + 15, area.Top - 15));
                DrawingText.Draw(context, pane.Series[pair].Name, new(x + 20, area.Top - 23), brush, 11, 105);
            }
            var valueAxis = RealtimeSensorValueAxis.Create(pane.Geometry, area.Height);
            foreach (var tick in valueAxis.Ticks)
            {
                var y = area.Bottom - tick.Position * area.Height;
                context.DrawLine(gridPen, new(area.Left, y), new(area.Right, y));
            }
            foreach (var tick in valueAxis.Labels)
            {
                var y = area.Bottom - tick.Position * area.Height;
                DrawingText.Draw(context, valueAxis.FormatLabel(tick),
                    new(8, y - 7), Palette.Muted, 10, 72);
            }
            for (var i = 0; i <= 4; i++)
            {
                var x = area.Left + area.Width * i / 4;
                context.DrawLine(gridPen, new(x, area.Top), new(x, area.Bottom));
            }
            Point Project(HistoryChartPoint point) => new(area.Left + point.X * area.Width,
                area.Bottom - point.Y * area.Height);
            using (context.PushClip(area))
            {
                for (var pair = 0; pair < pane.Series.Length; pair++)
                {
                    var series = pane.Series[pair];
                    var brush = pair == 0 ? FirstSensorBrush : SecondSensorBrush;
                    var pen = new Pen(brush, 1.6);
                    context.DrawGeometry(null, pen, prepared.Paths[series.Index]);
                    foreach (var marker in series.Markers)
                        context.DrawEllipse(brush, null, Project(marker.Position), 1.8, 1.8);
                }
                if (Selection is { } selected && frame.Window.Contains(selected.Sample.UtcTicks))
                {
                    var x = area.Left + (selected.Sample.UtcTicks - frame.Window.StartTicks) /
                        (double)Math.Max(1, frame.Window.DurationTicks) * area.Width;
                    context.DrawLine(new Pen(Palette.Muted, 1), new(x, area.Top), new(x, area.Bottom));
                    if (selected.SeriesIndex / 2 == paneIndex && selected.Sample.Value is { } value &&
                        double.IsFinite(value) && pane.Geometry.RawDomain is not null)
                    {
                        var brush = selected.SeriesIndex % 2 == 0 ? FirstSensorBrush : SecondSensorBrush;
                        context.DrawEllipse(Palette.Surface, new Pen(brush, 2),
                            Project(pane.Geometry.Normalize(selected.Sample.UtcTicks, value)), 4, 4);
                    }
                }
            }
            var axisNote = valueAxis.IsLimited ? "20-unit grid limited (range/precision)"
                : valueAxis.LabelOffset != 0
                    ? "Y = " + valueAxis.LabelOffset.ToString("0", CultureInfo.InvariantCulture) + " + label" : null;
            if (axisNote is not null)
                DrawingText.Draw(context, axisNote, new(area.Left + 6, area.Top + 6),
                    Palette.Muted, 10, area.Width - 12);
            if (pane.Series.Any(series => series.Snapshot.BudgetStatus == ChartBudgetStatus.InsufficientBudget))
                DrawingText.Draw(context, "Point budget too small to preserve all gaps",
                    new(area.Left + 6, area.Top + (axisNote is null ? 6 : 21)),
                    Palette.Error, 11, area.Width - 12);
        }
        var bottom = PanePlotBounds(3);
        for (var index = 0; index <= 4; index++)
        {
            var ticks = AtFraction(frame.Window, index / 4d);
            var x = bottom.Left + bottom.Width * index / 4;
            var width = Math.Min(92, bottom.Width / 4 - 2);
            var left = index == 0 ? x : index == 4 ? x - width : x - width / 2;
            DrawingText.Draw(context, Timestamp(ticks), new(left, bottom.Bottom + 8),
                Palette.Muted, 10, width);
        }
        DrawingText.Draw(context, "Shared X: UTC time · wheel / + / − zoom · drag / arrows pan · Home follows",
            new(bottom.Left, Bounds.Height - 20), Palette.Muted, 11, bottom.Width);
        // Count only the completed data path. No invalidations, events, or UI updates here:
        // instrumentation must not generate an extra repaint or retain a previous frame.
        var completed = Stopwatch.GetTimestamp();
        _dataRenderCallbacks++;
        _lastRenderMilliseconds = Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds;
        _maximumRenderMilliseconds = Math.Max(_maximumRenderMilliseconds, _lastRenderMilliseconds);
        if (!_hasRenderedIdentity || _renderedGeneration != frame.Generation ||
            _renderedRevision != frame.Revision || _renderedSourceVersion != frame.SourceVersion)
        {
            _hasRenderedIdentity = true;
            _renderedGeneration = frame.Generation;
            _renderedRevision = frame.Revision;
            _renderedSourceVersion = frame.SourceVersion;
            _distinctFrameRenders++;
            _lastCommitToRenderMilliseconds = Stopwatch.GetElapsedTime(_committedTimestamp, completed).TotalMilliseconds;
            _maximumCommitToRenderMilliseconds = Math.Max(_maximumCommitToRenderMilliseconds, _lastCommitToRenderMilliseconds);
        }
    }
}
