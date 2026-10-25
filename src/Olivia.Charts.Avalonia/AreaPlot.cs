using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Olivia.Core.Charts;

namespace Olivia.Avalonia;

/// <summary>Original bounded numeric XY area fills. Queries are synchronous (at most 2048 raw entries),
/// so no worker or pending source is retained. A revision fences geometry-dependent interaction.</summary>
public sealed class AreaPlot : Control
{
    private AreaChartSource? _source;
    private AreaChartOptions _options = new();
    private int _selection = -1;
    private bool _attached;
    public AreaPlot()
    {
        Focusable = true; ClipToBounds = true; MinHeight = 180;
        GotFocus += (_, _) => InvalidateVisual(); LostFocus += (_, _) => InvalidateFrame();
        AutomationProperties.SetName(this, "Area chart. Left and right select every visible raw point including occluded points; plus and minus zoom XY; PageUp and PageDown pan X; Up and Down pan Y; Home fits; Escape cancels selection. Filled interpolation has no sample identity.");
    }
    public AreaChartSnapshot? Snapshot { get; private set; }
    public long FrameRevision { get; private set; }
    public AreaChartOptions Options => _options;
    public AreaChartHit? SelectedHit => Snapshot is { } snapshot && _selection >= 0 && _selection < snapshot.Points.Length ? snapshot.Points[_selection].Hit : null;
    public UiPalette Palette { get; set; } = UiPalette.Dark;
    public Rect PlotArea => new(76, 62, Math.Max(0, Bounds.Width - 94), Math.Max(0, Bounds.Height - 132));
    public bool IsPlotAvailable => Bounds.Width >= Math.Max(174, (Snapshot?.Source.Series.Length ?? 0) * 85) && PlotArea.Height >= 35;
    private bool IsLine => _options.Mode is AreaChartMode.Line or AreaChartMode.StepLineAfter or AreaChartMode.StepLineBefore;
    public string AxisDescription => IsLine
        ? $"Numeric XY · {_options.Mode} strokes · original markers only"
        : Snapshot?.YDomain is { Minimum: > 0 } or { Maximum: < 0 }
        ? "Numeric XY · clipped fills (zero offscreen) · independent overlays"
        : "Numeric XY · zero-baseline fills · independent overlays";
    public string Status => Snapshot is not { } snapshot ? "No source" : snapshot.BudgetStatus == ChartBudgetStatus.InsufficientBudget
        ? _options.Mode != AreaChartMode.LinearArea
            ? snapshot.GeometryCountsKnown
                ? $"Budget exceeded: need {snapshot.RequiredPointCount} points / {snapshot.RequiredPolygonCount} fills / {snapshot.RequiredVertexCount} fill vertices / {snapshot.RequiredSegmentCount} strokes / {snapshot.RequiredStrokeVertexCount} stroke vertices / {snapshot.RequiredWorkCount} work; no partial geometry"
                : $"Work budget exceeded: need {snapshot.RequiredWorkCount} work; fills / strokes / vertices not computed; no partial geometry"
            : snapshot.GeometryCountsKnown
            ? $"Budget exceeded: need {snapshot.RequiredPointCount} points / {snapshot.RequiredPolygonCount} fills / {snapshot.RequiredVertexCount} vertices / {snapshot.RequiredWorkCount} work; no partial geometry"
            : $"Work budget exceeded: need {snapshot.RequiredWorkCount} work; fills / vertices not computed; no partial geometry"
        : _options.Mode == AreaChartMode.LinearArea
            ? $"Dataset {snapshot.Source.DatasetVersion} · {snapshot.Points.Length} raw points · {snapshot.Polygons.Length} fills · {snapshot.Gaps.Length} gaps"
            : $"Dataset {snapshot.Source.DatasetVersion} · {_options.Mode} · {snapshot.Points.Length} raw points · {snapshot.Segments.Length} strokes · {snapshot.Polygons.Length} fills · {snapshot.Gaps.Length} gaps";
    public string AccessibleSummary => SelectedHit is { } hit
        ? $"{Status}. {hit.SeriesId}; key {hit.Key}; X {hit.X?.ToString("R", CultureInfo.InvariantCulture)}; Y {hit.Y?.ToString("R", CultureInfo.InvariantCulture)}; series version {hit.SeriesSourceVersion}"
        : Status;
    public event EventHandler? StateChanged;
    public void SetSource(AreaChartSource source)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(source); var snapshot = source.Query(_options); _source = source; Snapshot = snapshot; InvalidateFrame();
    }
    public void SetOptions(AreaChartOptions options)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(options); var snapshot = _source?.Query(options); _options = options; Snapshot = snapshot; InvalidateFrame();
    }
    public void ReleaseSource()
    {
        Dispatcher.UIThread.VerifyAccess(); _source = null; Snapshot = null; InvalidateFrame();
    }
    private void InvalidateFrame() { FrameRevision++; _selection = -1; Changed(); }
    private void Changed()
    {
        AutomationProperties.SetName(this, IsLine
            ? $"{_options.Mode} chart. Left and right select original points; plus/minus zoom, PageUp/PageDown pan X, Up/Down pan Y, Home fits; Escape cancels selection. Stroke endpoints and step corners have no sample identity."
            : "Area chart. Left and right select every visible raw point including occluded points; plus and minus zoom XY; PageUp and PageDown pan X; Up and Down pan Y; Home fits; Escape cancels selection. Filled interpolation has no sample identity.");
        AutomationProperties.SetHelpText(this, AccessibleSummary); ToolTip.SetTip(this, SelectedHit is null ? null : AccessibleSummary);
        InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty);
    }
    /// <summary>Rejects delayed geometry events, even when a replacement reuses dataset versions or keys.</summary>
    public bool SelectAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || !IsPlotAvailable || !PlotArea.Contains(point) || Snapshot is not { } snapshot) return false;
        var area = PlotArea;
        var hit = AreaChartGeometry.HitTest(snapshot, (point.X - area.X) / area.Width, (point.Y - area.Y) / area.Height, 7 / area.Width, 7 / area.Height);
        _selection = hit is null ? -1 : Array.FindIndex(snapshot.Points.ToArray(), candidate => candidate.Hit.Equals(hit.Value));
        Changed(); return hit is not null;
    }
    public void Zoom(double factor)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        if (Snapshot is not { XDomain: { } xDomain, YDomain: { } yDomain }) return;
        SetOptions(ViewOptions(Transform(xDomain, factor, 0), Transform(yDomain, factor, 0)));
    }
    public void ResetView() => SetOptions(ViewOptions(null, null));
    public void CancelInteraction() { Dispatcher.UIThread.VerifyAccess(); InvalidateFrame(); }
    private AreaChartOptions ViewOptions(HistoryValueDomain? x, HistoryValueDomain? y)
        => new(_options.GeometryBudget, x, y, _options.PolygonBudget, _options.VertexBudget, _options.WorkBudget,
            _options.Mode, _options.SegmentBudget, _options.StrokeVertexBudget);
    public void Pan(double xFraction, double yFraction = 0)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!double.IsFinite(xFraction) || !double.IsFinite(yFraction)) throw new ArgumentOutOfRangeException(nameof(xFraction));
        if (Snapshot is not { XDomain: { } xDomain, YDomain: { } yDomain }) return;
        SetOptions(ViewOptions(Transform(xDomain, 1, xFraction), Transform(yDomain, 1, yFraction)));
    }
    // Half-range arithmetic avoids overflow for domains spanning ±double.MaxValue.
    private static HistoryValueDomain Transform(HistoryValueDomain domain, double factor, double pan)
    {
        if (factor == 1 && pan == 0) return domain;
        var span = domain.Maximum - domain.Minimum;
        var center = double.IsFinite(span) ? domain.Minimum + span / 2 : domain.Minimum / 2 + domain.Maximum / 2;
        var half = double.IsFinite(span) ? span / 2 : domain.Maximum / 2 - domain.Minimum / 2;
        // Constant or subnormal ranges without representable extent stay unchanged.
        if (half == 0) return domain;
        if (factor == 1)
        {
            var shift = double.IsFinite(span) ? span * pan : half * (2 * pan);
            var pannedMin = domain.Minimum + shift; var pannedMax = domain.Maximum + shift;
            return double.IsFinite(pannedMin) && double.IsFinite(pannedMax) && pannedMin < pannedMax
                ? new(pannedMin, pannedMax) : domain;
        }
        var shifted = center + half * (2 * pan);
        var extent = half / factor;
        var min = shifted - extent; var max = shifted + extent;
        if (!double.IsFinite(min) || !double.IsFinite(max) || min >= max) return domain;
        return new(min, max);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change); if (change.Property == BoundsProperty) InvalidateFrame();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _attached = true; InvalidateFrame(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _attached = false; InvalidateFrame(); base.OnDetachedFromVisualTree(e); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsPlotAvailable || !PlotArea.Contains(e.GetPosition(this))) return;
        Focus(); SelectAt(e.GetPosition(this), FrameRevision); e.Handled = true;
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e); if (!IsPlotAvailable || !PlotArea.Contains(e.GetPosition(this)) || e.Delta.Y == 0) return;
        Zoom(e.Delta.Y > 0 ? 1.5 : 1 / 1.5); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Right: case Key.Left:
                if (_attached && IsPlotAvailable && Snapshot is { Points.Length: > 0 } snapshot) { _selection = _selection < 0 ? (e.Key == Key.Right ? 0 : snapshot.Points.Length - 1) : Math.Clamp(_selection + (e.Key == Key.Right ? 1 : -1), 0, snapshot.Points.Length - 1); Changed(); } break;
            case Key.Add: case Key.OemPlus: Zoom(1.5); break;
            case Key.Subtract: case Key.OemMinus: Zoom(1 / 1.5); break;
            case Key.PageDown: Pan(.2); break;
            case Key.PageUp: Pan(-.2); break;
            case Key.Up: Pan(0, .2); break;
            case Key.Down: Pan(0, -.2); break;
            case Key.Home: ResetView(); break;
            case Key.Escape: CancelInteraction(); break;
            default: return;
        }
        e.Handled = true;
    }
    private IBrush Brush(int index) => index switch { 0 => Palette.Accent, 1 => Palette.Error, 2 => Brushes.CornflowerBlue, 3 => Brushes.Orchid, 4 => Brushes.Goldenrod, 5 => Brushes.LimeGreen, 6 => Brushes.DeepPink, _ => Brushes.SlateBlue };
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Palette.Surface, new Rect(Bounds.Size));
        if (IsFocused) context.DrawRectangle(null, new Pen(Palette.Accent, 2), new Rect(Bounds.Size).Deflate(1));
        DrawingText.Draw(context, AxisDescription, new(10, 8), Palette.Text, 12, Math.Max(1, Bounds.Width - 20));
        var area = PlotArea; var snapshot = Snapshot;
        if (snapshot is not null && IsPlotAvailable)
        {
            for (var i = 0; i < snapshot.Source.Series.Length; i++)
            {
                var x = 12 + i * Math.Max(50, (Bounds.Width - 24) / snapshot.Source.Series.Length);
                context.FillRectangle(Brush(i), new Rect(x, 33, 8, 8));
                DrawingText.Draw(context, snapshot.Source.Series[i].Label, new(x + 12, 28), Palette.Text, 10, Math.Max(1, (Bounds.Width - 24) / snapshot.Source.Series.Length - 16));
            }
            foreach (var tick in snapshot.YTicks)
            {
                var y = area.Bottom - tick.Position * area.Height;
                context.DrawLine(new Pen(Palette.GridLine, 1), new(area.Left, y), new(area.Right, y));
                DrawingText.Draw(context, tick.Value.ToString("G4", CultureInfo.InvariantCulture), new(5, y - 7), Palette.Muted, 10, 67);
            }
            foreach (var tick in snapshot.XTicks)
            {
                var x = area.Left + tick.Position * area.Width;
                context.DrawLine(new Pen(Palette.GridLine, 1), new(x, area.Top), new(x, area.Bottom));
                DrawingText.Draw(context, tick.Value.ToString("G4", CultureInfo.InvariantCulture), new(Math.Clamp(x - 20, area.Left, area.Right - 45), area.Bottom + 8), Palette.Muted, 10, 48);
            }
            context.DrawRectangle(null, new Pen(Palette.Muted, 1), area);
            using (context.PushClip(area))
            {
                // Only finite, clipped polygons from Core enter the rendering API. Fill vertices are
                // interpolation, never invented samples; only the raw marker array can be selected.
                foreach (var polygon in snapshot.Polygons)
                {
                    if (polygon.Vertices.Length < 3) continue;
                    var geometry = new StreamGeometry();
                    using (var path = geometry.Open())
                    {
                        var first = polygon.Vertices[0];
                        path.BeginFigure(new Point(area.X + first.X * area.Width, area.Y + first.Y * area.Height), true);
                        for (var i = 1; i < polygon.Vertices.Length; i++)
                        {
                            var vertex = polygon.Vertices[i];
                            path.LineTo(new Point(area.X + vertex.X * area.Width, area.Y + vertex.Y * area.Height));
                        }
                        path.EndFigure(true);
                    }
                    using (context.PushOpacity(.45)) context.DrawGeometry(Brush(polygon.SeriesIndex), null, geometry);
                }
                if (snapshot.IsBaselineVisible)
                {
                    var baseline = area.Top + snapshot.BaselinePosition * area.Height;
                    context.DrawLine(new Pen(Palette.Muted, 1), new(area.Left, baseline), new(area.Right, baseline));
                }
                foreach (var segment in snapshot.Segments)
                    context.DrawLine(new Pen(Brush(segment.SeriesIndex), 2),
                        new Point(area.X + segment.Start.X * area.Width, area.Y + segment.Start.Y * area.Height),
                        new Point(area.X + segment.End.X * area.Width, area.Y + segment.End.Y * area.Height));
                foreach (var point in snapshot.Points)
                    context.DrawEllipse(Brush(point.SeriesIndex), null, new Point(area.X + point.X * area.Width, area.Y + point.Y * area.Height), 4, 4);
                // Draw the selection last, so keyboard-selected occluded observations are visible.
                if (_selection >= 0 && _selection < snapshot.Points.Length)
                {
                    var point = snapshot.Points[_selection];
                    context.DrawEllipse(null, new Pen(Palette.Text, 2), new Point(area.X + point.X * area.Width, area.Y + point.Y * area.Height), 7, 7);
                }
            }
        }
        DrawingText.Draw(context, IsPlotAvailable ? Status : IsLine ? "Enlarge the chart to show strokes, points and labels" : "Enlarge the chart to show fills, points and labels", new(10, Math.Max(0, Bounds.Height - 40)), Palette.Muted, 11, Math.Max(1, Bounds.Width - 20));
        if (SelectedHit is not null) DrawingText.Draw(context, AccessibleSummary, new(10, Math.Max(0, Bounds.Height - 22)), Palette.Text, 10, Math.Max(1, Bounds.Width - 20));
    }
}
