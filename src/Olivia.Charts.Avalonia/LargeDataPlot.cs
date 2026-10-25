using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Olivia.Core.Charts;

namespace Olivia.Avalonia;

/// <summary>Multi-line million-point chart. One cancellable query plus one replaceable newest request.</summary>
public sealed class LargeDataPlot : Control
{
    private readonly Func<LargeDataSourceSnapshot, LargeDataView, int, CancellationToken, Task<LargeDataFrame>> _query;
    private LargeDataSourceSnapshot _source = new(0, []);
    private Request? _pending;
    private CancellationTokenSource? _active;
    private TaskCompletionSource? _idle;
    private bool _attached, _running;
    private int _layoutPixelWidth;
    private LargeDataFrame? _pathFrame;
    private Rect _pathArea;
    private readonly List<(int Id, StreamGeometry Geometry, Point? Marker)> _paths = [];
    private Point? _drag;
    private LargeDataView? _dragView;
    private sealed record Request(LargeDataSourceSnapshot Source, LargeDataView View, int Width, long Revision, long Started);
    private UiPalette _palette = UiPalette.Dark;
    private IReadOnlyList<IBrush> _seriesColors = [Brushes.Turquoise, Brushes.Orange, Brushes.DeepSkyBlue,
        Brushes.HotPink, Brushes.Gold, Brushes.LimeGreen, Brushes.MediumPurple, Brushes.Coral];

    public LargeDataPlot(Func<LargeDataSourceSnapshot, LargeDataView, int, CancellationToken, Task<LargeDataFrame>>? query = null)
    {
        _query = query ?? ((source, view, width, token) => Task.FromResult(LargeDataFrame.Query(source, view, width, token)));
        Focusable = true; ClipToBounds = true; MinHeight = 200;
        AutomationProperties.SetName(this, "Large data curves. Wheel zooms X; Shift-wheel zooms Y. Drag pans both axes; Home resets.");
    }
    public UiPalette Palette
    {
        get => _palette;
        set { Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(value); _palette = value; InvalidateVisual(); }
    }
    public IReadOnlyList<IBrush> SeriesColors
    {
        get => _seriesColors;
        set
        {
            Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(value);
            if (value.Count == 0 || value.Any(brush => brush is null)) throw new ArgumentException("At least one non-null series brush is required.", nameof(value));
            _seriesColors = Array.AsReadOnly(value.ToArray()); InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public IBrush SeriesBrush(int id) => _seriesColors[(id - 1) % _seriesColors.Count];
    public LargeDataFrame? Snapshot { get; private set; }
    public LargeDataView? View { get; private set; }
    public long RequestRevision { get; private set; }
    public string Status { get; private set; } = "No data";
    public Task WhenIdle => _idle?.Task ?? Task.CompletedTask;
    public double LastResponseMilliseconds { get; private set; }
    public double LastRenderMilliseconds { get; private set; }
    public long RenderCount { get; private set; }
    public event EventHandler? StateChanged;
    public Rect PlotBounds => new(78, 22, Math.Max(1, Bounds.Width - 102), Math.Max(1, Bounds.Height - 72));

    public void SetSource(LargeDataSourceSnapshot source, bool resetView = true)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(source);
        var wasFull = View == _source.Extent;
        _source = source;
        View = source.Extent is { } extent ? resetView || View is null || wasFull ? extent : View.Value.Clamp(extent) : null;
        Snapshot = null; _pathFrame = null; _paths.Clear();
        QueueFrame();
    }
    public void ReleaseSource() => SetSource(new(_source.Version + 1, []));
    public void SetView(LargeDataView view)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_source.Extent is not { } extent) return;
        View = view.Clamp(extent); QueueFrame();
    }
    public void ResetView()
    {
        if (_source.Extent is { } extent) SetView(extent);
    }
    public void Zoom(double factor, double xAnchor = .5, double yAnchor = .5, bool yOnly = false)
    {
        if (View is { } view && _source.Extent is { } extent) SetView(view.ZoomWithin(extent, factor, xAnchor, yAnchor, yOnly));
    }
    public void Pan(double xFraction, double yFraction = 0)
    {
        if (View is { } view) SetView(view.Pan(xFraction, yFraction));
    }

    private void QueueFrame()
    {
        RequestRevision++; _active?.Cancel(); _pending = null;
        _layoutPixelWidth = Math.Clamp((int)Math.Ceiling(PlotBounds.Width), 1, 4096);
        if (_attached && View is { } view && !_source.Series.IsEmpty)
        {
            _pending = new(_source, view, _layoutPixelWidth,
                RequestRevision, Stopwatch.GetTimestamp());
            Status = "Updating display…";
        }
        else { Snapshot = null; Status = _source.Series.IsEmpty ? "No data" : "Detached"; }
        if (!_running)
        {
            _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            StartNext();
        }
        InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartNext()
    {
        var request = _pending; _pending = null;
        if (request is null) { _running = false; _idle?.TrySetResult(); return; }
        _running = true;
        var cancellation = _active = new CancellationTokenSource();
        _ = ExecuteAsync(request, cancellation);
    }

    private async Task ExecuteAsync(Request request, CancellationTokenSource cancellation)
    {
        LargeDataFrame? frame = null; string? error = null;
        try
        {
            frame = await Task.Run(() => _query(request.Source, request.View, request.Width, cancellation.Token),
                cancellation.Token).ConfigureAwait(false);
            if (frame.SourceVersion != request.Source.Version || frame.View != request.View || frame.PixelWidth != request.Width)
                throw new InvalidOperationException("The query returned a different source or viewport.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) { error = exception.Message; }
        Dispatcher.UIThread.Post(() =>
        {
            var current = _attached && request.Revision == RequestRevision && !cancellation.IsCancellationRequested;
            cancellation.Dispose(); _active = null;
            if (current)
            {
                Snapshot = error is null ? frame : null;
                LastResponseMilliseconds = Stopwatch.GetElapsedTime(request.Started).TotalMilliseconds;
                Status = error is not null ? "Query failed: " + error : frame is null ? "Query cancelled" :
                    $"v{frame.SourceVersion} · {frame.VisiblePoints:N0} X-range raw / {frame.DisplayPoints:N0} display points";
                InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty);
            }
            StartNext();
        }, DispatcherPriority.Background);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e); _attached = true; QueueFrame();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false; _drag = null; _dragView = null; QueueFrame();
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _attached)
        {
            // Sampling depends on width only. Status text can change the Auto-row height;
            // querying on height changes would create a layout → query → status feedback loop.
            if (Math.Clamp((int)Math.Ceiling(PlotBounds.Width), 1, 4096) != _layoutPixelWidth) QueueFrame();
            else InvalidateVisual();
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); Focus();
        if (!PlotBounds.Contains(e.GetPosition(this)) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _drag = e.GetPosition(this); _dragView = View; e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } start || _dragView is not { } view) return;
        var delta = e.GetPosition(this) - start;
        SetView(view.Pan(-delta.X / PlotBounds.Width, delta.Y / PlotBounds.Height)); e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _drag = null; _dragView = null; e.Pointer.Capture(null); base.OnPointerReleased(e);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _drag = null; _dragView = null; base.OnPointerCaptureLost(e);
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var position = e.GetPosition(this); var area = PlotBounds;
        if (!area.Contains(position) || e.Delta.Y == 0) return;
        Zoom(e.Delta.Y > 0 ? 1.4 : 1 / 1.4, (position.X - area.X) / area.Width,
            (area.Bottom - position.Y) / area.Height, e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left: Pan(-.2); break;
            case Key.Right: Pan(.2); break;
            case Key.Up: Pan(0, .2); break;
            case Key.Down: Pan(0, -.2); break;
            case Key.Add: case Key.OemPlus: Zoom(1.5, yOnly: e.KeyModifiers.HasFlag(KeyModifiers.Shift)); break;
            case Key.Subtract: case Key.OemMinus: Zoom(1 / 1.5, yOnly: e.KeyModifiers.HasFlag(KeyModifiers.Shift)); break;
            case Key.Home: ResetView(); break;
            default: return;
        }
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var watch = Stopwatch.StartNew(); base.Render(context);
        var palette = Palette; var area = PlotBounds;
        context.FillRectangle(palette.Surface, new Rect(Bounds.Size));
        if (IsFocused) context.DrawRectangle(null, new Pen(palette.Accent, 1), new Rect(Bounds.Size).Deflate(1));
        var frame = Snapshot;
        if (frame is null)
        {
            DrawingText.Draw(context, Status, new(20, 40), palette.Muted, 13, Math.Max(1, Bounds.Width - 40));
        }
        else
        {
            for (var i = 0; i <= 4; i++)
            {
                var y = area.Top + area.Height * i / 4;
                context.DrawLine(new Pen(palette.GridLine, 1), new(area.Left, y), new(area.Right, y));
                var label = frame.View.YMaximum - frame.View.Height * i / 4;
                DrawingText.Draw(context, label.ToString("0.###", CultureInfo.InvariantCulture), new(4, y - 7), palette.Muted, 10, 70);
                var x = area.Left + area.Width * i / 4;
                context.DrawLine(new Pen(palette.GridLine, 1), new(x, area.Top), new(x, area.Bottom));
                var argument = frame.View.XMinimum + frame.View.Width * i / 4;
                DrawingText.Draw(context, argument.ToString("N0", CultureInfo.InvariantCulture), new(Math.Min(x, area.Right - 58), area.Bottom + 9), palette.Muted, 10, 78);
            }
            using (context.PushClip(area))
            {
                if (!ReferenceEquals(_pathFrame, frame) || _pathArea != area)
                {
                    _paths.Clear();
                    foreach (var line in frame.Lines)
                    {
                        if (line.Points.IsEmpty) continue;
                        Point Project(LargeDataPoint point) => new(area.Left + (point.Index - frame.View.XMinimum) / frame.View.Width * area.Width,
                            area.Bottom - (point.Value - frame.View.YMinimum) / frame.View.Height * area.Height);
                        var first = Project(line.Points[0]); var geometry = new StreamGeometry();
                        using (var path = geometry.Open())
                        {
                            path.BeginFigure(first, false);
                            for (var i = 1; i < line.Points.Length; i++) path.LineTo(Project(line.Points[i]));
                            path.EndFigure(false);
                        }
                        _paths.Add((line.SeriesId, geometry, line.Points.Length == 1 ? first : null));
                    }
                    _pathFrame = frame; _pathArea = area;
                }
                foreach (var path in _paths)
                {
                    var brush = SeriesBrush(path.Id);
                    if (path.Marker is { } marker) context.DrawEllipse(brush, null, marker, 2, 2);
                    else context.DrawGeometry(null, new Pen(brush, 1.2), path.Geometry);
                }
            }
        }
        DrawingText.Draw(context, "Argument (original index) · wheel X / Shift-wheel Y · drag XY · Home reset",
            new(16, Bounds.Height - 20), palette.Muted, 11, Math.Max(1, Bounds.Width - 32));
        RenderCount++; LastRenderMilliseconds = watch.Elapsed.TotalMilliseconds;
    }
}
