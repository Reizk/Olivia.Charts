using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Olivia.Core.Charts;

namespace Olivia.Avalonia;

public enum LineInterpolation { Smooth, Straight, Step }

/// <summary>A line-only view over the bounded keyed XY geometry. All readback refers to
/// original samples; interpolation never creates selectable observations or bridges gaps.</summary>
public sealed class LinePlot : Control
{
    private UiPalette _palette = UiPalette.Light;
    private HistoryValueDomain? _extent, _viewport, _dragDomain;
    private Point? _dragStart;
    private IPointer? _dragPointer;
    private bool _markers = true, _grid = true;
    public LinePlot() { Focusable = true; ClipToBounds = true; MinHeight = 240; }
    public AreaChartSource? Source { get; private set; }
    public AreaChartSnapshot? Snapshot { get; private set; }
    public AreaChartHit? SelectedHit { get; private set; }
    public LineInterpolation Interpolation { get; private set; } = LineInterpolation.Smooth;
    public UiPalette Palette { get => _palette; set { _palette = value; InvalidateVisual(); } }
    public bool ShowMarkers { get => _markers; set { _markers = value; InvalidateVisual(); } }
    public bool ShowGrid { get => _grid; set { _grid = value; InvalidateVisual(); } }
    public string ValueAxisTitle { get; set; } = "Value";
    public string ArgumentAxisTitle { get; set; } = "X";
    public Rect PlotArea => new(64, 76, Math.Max(1, Bounds.Width - 92), Math.Max(1, Bounds.Height - 132));
    public string AccessibleSummary => Source is null ? "No source" :
        $"{Source.Series.Length} series · {Snapshot?.Points.Length ?? 0} original points · {Interpolation}";
    public void SetSource(AreaChartSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source; _extent = source.Query(new(mode: AreaChartMode.Line, includeZeroInAutoRange: false)).XDomain;
        _viewport = null; SelectedHit = null; Refresh();
    }
    public void SetInterpolation(LineInterpolation mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Interpolation = mode; Refresh();
    }
    public void ReleaseSource()
    {
        EndDrag(); Source = null; Snapshot = null; SelectedHit = null;
        _viewport = _extent = null; InvalidateVisual();
    }
    public void ResetView() { _viewport = null; SelectedHit = null; Refresh(); }
    public void Zoom(double scale, double anchor = .5)
    {
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(anchor)) throw new ArgumentOutOfRangeException(nameof(scale));
        if (_extent is not { } extent || Snapshot?.XDomain is not { } current || extent.IsConstant) return;
        var width = Math.Clamp((current.Maximum - current.Minimum) / scale, (extent.Maximum - extent.Minimum) / 100, extent.Maximum - extent.Minimum);
        anchor = Math.Clamp(anchor, 0, 1);
        var start = current.Minimum + (current.Maximum - current.Minimum) * anchor - width * anchor;
        SetViewport(start, width);
    }
    private void SetViewport(double start, double width)
    {
        if (_extent is not { } extent) return;
        start = Math.Clamp(start, extent.Minimum, Math.Max(extent.Minimum, extent.Maximum - width));
        _viewport = new(start, start + width); Refresh();
    }
    private void Refresh()
    {
        if (Source is null) return;
        var mode = Interpolation switch { LineInterpolation.Smooth => AreaChartMode.SplineArea, LineInterpolation.Step => AreaChartMode.StepLineAfter, _ => AreaChartMode.Line };
        Snapshot = Source.Query(new(fixedXDomain: _viewport, mode: mode, includeZeroInAutoRange: false));
        if (SelectedHit is { } selected && !Snapshot.Points.Any(p => p.Hit == selected)) SelectedHit = null;
        AutomationProperties.SetName(this, "Line chart. " + AccessibleSummary + ". Arrow keys select points; plus/minus zoom; Home resets.");
        InvalidateVisual();
    }
    private Point At(double x, double y) => new(PlotArea.Left + x * PlotArea.Width, PlotArea.Top + y * PlotArea.Height);
    public AreaChartHit? SelectNearest(Point position)
    {
        SelectedHit = Snapshot?.Points.OrderBy(p => Math.Pow(At(p.X, p.Y).X - position.X, 2) + Math.Pow(At(p.X, p.Y).Y - position.Y, 2))
            .Select(p => (AreaChartHit?)p.Hit).FirstOrDefault();
        InvalidateVisual(); return SelectedHit;
    }
    private IBrush SeriesBrush(int i) => (i % 4) switch { 0 => Palette.Accent, 1 => new SolidColorBrush(Color.Parse("#11A894")), 2 => new SolidColorBrush(Color.Parse("#E39B39")), _ => new SolidColorBrush(Color.Parse("#A879DA")) };
    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Palette.Surface, new Rect(Bounds.Size));
        if (Snapshot is not { } snapshot || Source is null || Bounds.Width < 220 || Bounds.Height < 180) return;
        var area = PlotArea;
        DrawingText.Draw(context, ValueAxisTitle, new(area.Left, 10), Palette.Muted, 12);
        for (var i = 0; i < Source.Series.Length; i++)
        {
            var x = area.Left + i * Math.Max(100, Math.Min(160, area.Width / Source.Series.Length));
            context.DrawLine(new Pen(SeriesBrush(i), 3), new(x, 47), new(x + 20, 47));
            DrawingText.Draw(context, Source.Series[i].Label, new(x + 29, 38), Palette.Text, 12, 125);
        }
        foreach (var tick in snapshot.YTicks)
        {
            var y = area.Bottom - tick.Position * area.Height;
            if (ShowGrid) context.DrawLine(new Pen(Palette.GridLine, 1), new(area.Left, y), new(area.Right, y));
            DrawingText.Draw(context, Number(tick.Value), new(12, y - 7), Palette.Muted, 11, 45);
        }
        if (snapshot.XDomain is { } xd)
        {
            for (var i = 0; i <= 6; i++)
            {
                var x = area.Left + area.Width * i / 6;
                DrawingText.Draw(context, Number(xd.Minimum + (xd.Maximum - xd.Minimum) * i / 6),
                    new(Math.Clamp(x - 12, area.Left, area.Right - 35), area.Bottom + 12), Palette.Muted, 11, 42);
            }
        }
        DrawingText.Draw(context, ArgumentAxisTitle, new(area.Right - 130, Bounds.Height - 19), Palette.Muted, 11, 130);
        using (context.PushClip(area.Inflate(5)))
        {
            foreach (var segment in snapshot.Segments)
                context.DrawLine(new Pen(SeriesBrush(segment.SeriesIndex), 2.4), At(segment.Start.X, segment.Start.Y), At(segment.End.X, segment.End.Y));
            if (ShowMarkers)
                foreach (var point in snapshot.Points) context.DrawEllipse(Palette.Surface, new Pen(SeriesBrush(point.SeriesIndex), 2), At(point.X, point.Y), 3.8, 3.8);
            if (SelectedHit is { } hit && snapshot.Points.FirstOrDefault(p => p.Hit == hit) is var selected)
            {
                var position = At(selected.X, selected.Y);
                context.DrawLine(new Pen(Palette.Muted, 1, DashStyle.Dash), new(position.X, area.Top), new(position.X, area.Bottom));
                context.DrawEllipse(Palette.Surface, new Pen(SeriesBrush(selected.SeriesIndex), 3), position, 5, 5);
            }
        }
        if (SelectedHit is { } selectedHit)
        {
            var label = Source.Series.First(s => s.Id == selectedHit.SeriesId).Label;
            var width = Math.Min(240, area.Width - 16);
            var box = new Rect(area.Right - width - 8, area.Top + 8, width, 59);
            context.DrawRectangle(Palette.Surface, new Pen(Palette.GridLine, 1), box, 8, 8);
            DrawingText.Draw(context, $"{label} · {Number(selectedHit.X!.Value)}", new(box.Left + 12, box.Top + 8), Palette.Muted, 11, width - 24);
            DrawingText.Draw(context, Number(selectedHit.Y!.Value), new(box.Left + 12, box.Top + 27), Palette.Text, 18, width - 24);
        }
        if (snapshot.BudgetStatus != ChartBudgetStatus.Satisfied)
            DrawingText.Draw(context, "数据超出当前曲线计算预算，请减少点数或选择折线。", new(area.Left + 12, area.Top + 12), Palette.Error, 13, area.Width - 24);
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e); var p = e.GetPosition(this);
        if (!PlotArea.Contains(p) || e.Delta.Y == 0) return;
        Zoom(e.Delta.Y > 0 ? 1.3 : 1 / 1.3, (p.X - PlotArea.Left) / PlotArea.Width); e.Handled = true;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); var p = e.GetPosition(this);
        if (!PlotArea.Contains(p) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(); SelectNearest(p); _dragStart = p; _dragDomain = Snapshot?.XDomain; _dragPointer = e.Pointer;
        e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var p = e.GetPosition(this);
        if (_dragStart is { } start && _dragDomain is { } domain)
        {
            if (Math.Abs(p.X - start.X) > 3) SetViewport(domain.Minimum - (p.X - start.X) / PlotArea.Width * (domain.Maximum - domain.Minimum), domain.Maximum - domain.Minimum);
        }
        else if (PlotArea.Contains(p)) SelectNearest(p);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { EndDrag(); base.OnPointerReleased(e); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { EndDrag(); base.OnPointerCaptureLost(e); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { EndDrag(); base.OnDetachedFromVisualTree(e); }
    private void EndDrag() { var pointer = _dragPointer; _dragPointer = null; _dragStart = null; _dragDomain = null; if (pointer?.Captured == this) pointer.Capture(null); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Home: ResetView(); break;
            case Key.Add: case Key.OemPlus: Zoom(1.3); break;
            case Key.Subtract: case Key.OemMinus: Zoom(1 / 1.3); break;
            case Key.Escape: SelectedHit = null; InvalidateVisual(); break;
            case Key.Left: case Key.Right:
                if (Snapshot is not { Points.Length: > 0 } snapshot) return;
                var points = snapshot.Points.ToArray(); var index = Array.FindIndex(points, p => p.Hit == SelectedHit);
                SelectedHit = points[Math.Clamp(index + (e.Key == Key.Left ? -1 : 1), 0, points.Length - 1)].Hit; InvalidateVisual(); break;
            default: return;
        }
        e.Handled = true;
    }
}
