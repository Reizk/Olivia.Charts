using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Olivia.Core.Charts;

namespace Olivia.Avalonia;

/// <summary>Original bounded numeric XY markers. Queries are synchronous (at most 2048 points),
/// so no worker or pending source is retained. A revision fences geometry-dependent interaction.</summary>
public sealed class ScatterPlot : Control
{
    private ScatterChartSource? _source;
    private ScatterChartOptions _options = new();
    private int _selection = -1;
    private IReadOnlyDictionary<string, ChartWorkspaceSeries>? _workspaceStyles;
    /// <summary>Publishes one fully prepared workspace frame before notifying observers.</summary>
    public void SetWorkspaceSnapshot(ScatterChartSnapshot snapshot, ChartWorkspaceConfiguration configuration)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(configuration);
        var prepared = configuration.Series.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var series in snapshot.Source.Series)
            if (!prepared.ContainsKey(series.Id)) throw new ArgumentException("Missing series style.", nameof(configuration));
        _workspaceStyles = prepared; _source = snapshot.Source; _options = snapshot.Options; Snapshot = snapshot;
        InvalidateFrame();
    }
    private IBrush SeriesBrush(int index)
    {
        if (Snapshot is { } snapshot && _workspaceStyles is not null && _workspaceStyles.TryGetValue(snapshot.Source.Series[index].Id, out var style))
            return style.Palette switch { "blue" => Brushes.CornflowerBlue, "orange" => Brushes.Orange, "green" => Brushes.LimeGreen, "purple" => Brushes.Orchid, _ => Palette.Accent };
        return Brush(index);
    }
    private double MarkerRadius(ScatterChartPoint point) => _workspaceStyles is not null && _workspaceStyles.TryGetValue(point.Hit.SeriesId, out var style) ? style.MarkerRadius : 4;
    private bool _attached;
    public ScatterPlot()
    {
        Focusable = true; ClipToBounds = true; MinHeight = 180;
        GotFocus += (_, _) => InvalidateVisual(); LostFocus += (_, _) => InvalidateFrame();
        AutomationProperties.SetName(this, "Scatter chart. Left and right select every visible point including occluded points; plus and minus zoom XY; PageUp and PageDown pan X; Up and Down pan Y; Home fits; Escape clears.");
    }
    public bool AllowNavigation { get; set; } = true;
    public ScatterChartSnapshot? Snapshot { get; private set; }
    public long FrameRevision { get; private set; }
    public ScatterChartOptions Options => _options;
    public ScatterChartHit? SelectedHit => Snapshot is { } snapshot && _selection >= 0 && _selection < snapshot.Points.Length ? snapshot.Points[_selection].Hit : null;
    public UiPalette Palette { get; set; } = UiPalette.Dark;
    public Rect PlotArea => new(76, 62, Math.Max(0, Bounds.Width - 94), Math.Max(0, Bounds.Height - 132));
    public bool IsPlotAvailable => Bounds.Width >= Math.Max(174, (Snapshot?.Source.Series.Length ?? 0) * 85) && PlotArea.Height >= 35;
    public string AxisDescription => "Numeric X → · Numeric Y ↑ · independent observations (no connecting lines)";
    public string Status => Snapshot is not { } snapshot ? "No source" : snapshot.BudgetStatus == ChartBudgetStatus.InsufficientBudget
        ? $"Budget exceeded: need {snapshot.RequiredPointCount} points; no partial geometry shown"
        : $"Dataset {snapshot.Source.DatasetVersion} · {snapshot.Points.Length} points · {snapshot.Gaps.Length} gaps";
    public string AccessibleSummary => SelectedHit is { } hit
        ? $"{Status}. {hit.SeriesId}; key {hit.Key}; X {hit.X?.ToString("R", CultureInfo.InvariantCulture)}; Y {hit.Y?.ToString("R", CultureInfo.InvariantCulture)}; series version {hit.SeriesSourceVersion}"
        : Status;
    public event EventHandler? StateChanged;
    public void SetSource(ScatterChartSource source)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(source); var snapshot = source.Query(_options); _workspaceStyles = null; _source = source; Snapshot = snapshot; InvalidateFrame();
    }
    public void SetOptions(ScatterChartOptions options)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(options); var snapshot = _source?.Query(options); _options = options; Snapshot = snapshot; InvalidateFrame();
    }
    public void ReleaseSource()
    {
        Dispatcher.UIThread.VerifyAccess(); _workspaceStyles = null; _source = null; Snapshot = null; InvalidateFrame();
    }
    private void InvalidateFrame() { FrameRevision++; _selection = -1; Changed(); }
    private void Changed()
    {
        AutomationProperties.SetHelpText(this, AccessibleSummary); ToolTip.SetTip(this, SelectedHit is null ? null : AccessibleSummary);
        InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty);
    }
    /// <summary>Rejects delayed geometry events, even when a replacement reuses dataset versions or keys.</summary>
    public bool SelectAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || !IsPlotAvailable || !PlotArea.Contains(point) || Snapshot is not { } snapshot) return false;
        var area = PlotArea;
        ScatterChartHit? hit = _workspaceStyles is null ? ScatterChartGeometry.HitTest(snapshot, (point.X - area.X) / area.Width, (point.Y - area.Y) / area.Height, 7 / area.Width, 7 / area.Height) : null;
        var best = double.PositiveInfinity;
        foreach (var marker in _workspaceStyles is null ? [] : snapshot.Points)
        {
            var dx = point.X - (area.X + marker.X * area.Width);
            var dy = point.Y - (area.Y + marker.Y * area.Height);
            var scale = Math.Max(Math.Abs(dx), Math.Abs(dy));
            var distance = scale == 0 ? 0 : scale * Math.Sqrt((dx / scale) * (dx / scale) + (dy / scale) * (dy / scale));
            var radius = Math.Max(7, MarkerRadius(marker));
            if (distance <= radius && distance <= best) { best = distance; hit = marker.Hit; }
        }
        _selection = hit is null ? -1 : Array.FindIndex(snapshot.Points.ToArray(), bar => bar.Hit.Equals(hit.Value));
        Changed(); return hit is not null;
    }
    public void Zoom(double factor)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!AllowNavigation) return;
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        if (Snapshot is not { XDomain: { } xDomain, YDomain: { } yDomain }) return;
        SetOptions(new(_options.GeometryBudget, Transform(xDomain, factor, 0), Transform(yDomain, factor, 0)));
    }
    public void ResetView() { if (AllowNavigation) SetOptions(new(_options.GeometryBudget)); }
    public void Pan(double xFraction, double yFraction = 0)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!AllowNavigation) return;
        if (!double.IsFinite(xFraction) || !double.IsFinite(yFraction)) throw new ArgumentOutOfRangeException(nameof(xFraction));
        if (Snapshot is not { XDomain: { } xDomain, YDomain: { } yDomain }) return;
        SetOptions(new(_options.GeometryBudget, Transform(xDomain, 1, xFraction), Transform(yDomain, 1, yFraction)));
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
        base.OnPointerWheelChanged(e); if (!AllowNavigation || !IsPlotAvailable || !PlotArea.Contains(e.GetPosition(this)) || e.Delta.Y == 0) return;
        Zoom(e.Delta.Y > 0 ? 1.5 : 1 / 1.5); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!AllowNavigation && e.Key is Key.Add or Key.OemPlus or Key.Subtract or Key.OemMinus or Key.PageDown or Key.PageUp or Key.Up or Key.Down or Key.Home) return;
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
            case Key.Escape: _selection = -1; Changed(); break;
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
                context.FillRectangle(SeriesBrush(i), new Rect(x, 33, 8, 8));
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
                foreach (var point in snapshot.Points)
                    context.DrawEllipse(SeriesBrush(point.SeriesIndex), null, new Point(area.X + point.X * area.Width, area.Y + point.Y * area.Height), MarkerRadius(point), MarkerRadius(point));
                // Draw the selection last, so keyboard-selected occluded observations are visible.
                if (_selection >= 0 && _selection < snapshot.Points.Length)
                {
                    var point = snapshot.Points[_selection];
                    context.DrawEllipse(null, new Pen(Palette.Text, 2), new Point(area.X + point.X * area.Width, area.Y + point.Y * area.Height), 7, 7);
                }
            }
        }
        DrawingText.Draw(context, IsPlotAvailable ? Status : "Enlarge the chart to show points and labels", new(10, Math.Max(0, Bounds.Height - 40)), Palette.Muted, 11, Math.Max(1, Bounds.Width - 20));
        if (SelectedHit is not null) DrawingText.Draw(context, AccessibleSummary, new(10, Math.Max(0, Bounds.Height - 22)), Palette.Text, 10, Math.Max(1, Bounds.Width - 20));
    }
}
