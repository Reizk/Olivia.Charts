using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Olivia.Core.Charts;

namespace Olivia.Avalonia;

/// <summary>Finite original two-endpoint observations. Shared controls own domains; wheel never
/// mutates the plot. Frame revisions fence delayed pointer hits after source/size/detach changes.</summary>
public sealed class RangePlot : Control
{
    private RangeChartOptions _options = RangeChartOptions.Default;
    private ImmutableArray<RangeChartHit> _originals = [];
    private int _selection = -1;
    private bool _attached;
    public RangePlot()
    {
        Focusable = true; ClipToBounds = true; MinHeight = 240;
        GotFocus += (_, _) => InvalidateVisual(); LostFocus += (_, _) => InvalidateVisual();
        AutomationProperties.SetName(this, "Range chart. Original lower and upper endpoints. Left/right select observations, Home selects first, Escape clears. Shared controls own the ranges.");
    }
    public RangeChartSnapshot? Snapshot { get; private set; }
    public RangeChartOptions Options => _options;
    public RangeChartSource? Source => Snapshot?.Source;
    public long FrameRevision { get; private set; }
    public RangeChartHit? SelectedHit => _selection >= 0 && _selection < _originals.Length ? _originals[_selection] : null;
    public UiPalette Palette { get; set; } = UiPalette.Dark;
    public Rect PlotArea => new(76, 62, Math.Max(0, Bounds.Width - 94), Math.Max(0, Bounds.Height - 132));
    public bool IsPlotAvailable => Bounds.Width >= Math.Max(230, (Source?.Series.Length ?? 0) * 85) && PlotArea.Height >= 35;
    public string AxisDescription => _options.Mode == RangeChartMode.LinearArea
        ? "Lower / Upper · linear band · numeric X spacing · original interval hits only"
        : "Lower / Upper · range bars · equal selected-category slots · no implicit zero fill";
    public string Status => Snapshot is not { } snapshot ? "No range source"
        : $"{snapshot.Options.Mode} · {snapshot.Bars.Length} bars / {snapshot.Polygons.Length} bands · {snapshot.Gaps.Length} gaps · dataset {snapshot.Source.DatasetVersion}";
    public string AccessibleSummary => SelectedHit is { } hit
        ? $"{Status}. Selected {hit.SeriesId} / key {hit.Key}; X {hit.X.ToString("R", CultureInfo.InvariantCulture)}; Lower {hit.Lower?.ToString("R", CultureInfo.InvariantCulture)}; Upper {hit.Upper?.ToString("R", CultureInfo.InvariantCulture)}; {(SelectedInterval.HasValue ? "visible clipped original interval" : "retained hidden/outside interval; no marker")}."
        : Status;
    public RangeChartInterval? SelectedInterval
    {
        get
        {
            if (SelectedHit is not { } hit || Snapshot is not { } snapshot) return null;
            foreach (var interval in snapshot.Intervals) if (interval.Hit.Equals(hit)) return interval;
            return null;
        }
    }
    public Point? MarkerPoint => IsPlotAvailable && SelectedInterval is { } interval
        ? At(interval.X, interval.Top / 2 + interval.Bottom / 2) : null;
    public event EventHandler? StateChanged;

    public void SetSource(RangeChartSource source)
    { Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(source); SetSnapshot(source.Query(_options)); }
    public void SetOptions(RangeChartOptions options)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(options);
        if (Source is { } source) SetSnapshot(source.Query(options)); else { _options = options; InvalidateFrame(); }
    }
    internal void SetSnapshot(RangeChartSnapshot snapshot)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Status != RangeChartStatus.Ready) throw new ArgumentException("Range snapshot was not admitted.", nameof(snapshot));
        var old = SelectedHit;
        var originals = ImmutableArray.CreateBuilder<RangeChartHit>();
        foreach (var series in snapshot.Source.Series)
            foreach (var sample in series.Samples)
                if (!sample.IsGap) originals.Add(new(snapshot.Source, series.Id, series.SourceVersion, sample));
        Snapshot = snapshot; _options = snapshot.Options; _originals = originals.ToImmutable();
        _selection = old.HasValue ? _originals.IndexOf(old.Value) : -1;
        InvalidateFrame();
    }
    public void ReleaseSource()
    { Dispatcher.UIThread.VerifyAccess(); Snapshot = null; _originals = []; _selection = -1; InvalidateFrame(); }
    public bool SelectAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || !IsEffectivelyEnabled || expectedRevision != FrameRevision || !IsPlotAvailable ||
            !PlotArea.Contains(point) || Snapshot is not { } snapshot) return false;
        var area = PlotArea;
        var hit = RangeChartGeometry.HitTest(snapshot, (point.X - area.X) / area.Width,
            (point.Y - area.Y) / area.Height, area.Width, area.Height);
        _selection = hit.HasValue ? _originals.IndexOf(hit.Value) : -1;
        Changed(); return hit.HasValue;
    }
    public bool TrySelect(RangeChartSource expectedSource, string? seriesId, long key, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || !IsEffectivelyEnabled || expectedRevision != FrameRevision || !ReferenceEquals(Source, expectedSource)) return false;
        var index = -1;
        if (seriesId is not null)
        {
            for (var i = 0; i < _originals.Length; i++)
                if (StringComparer.Ordinal.Equals(_originals[i].SeriesId, seriesId) && _originals[i].Key == key) { index = i; break; }
            if (index < 0) return false;
        }
        if (_selection == index) return true;
        _selection = index; Changed(); return true;
    }
    private void InvalidateFrame() { FrameRevision++; Changed(); }
    private void Changed()
    {
        AutomationProperties.SetHelpText(this, AccessibleSummary);
        ToolTip.SetTip(this, SelectedHit is null ? null : AccessibleSummary);
        InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == BoundsProperty) InvalidateFrame(); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); _attached = true; InvalidateFrame(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _attached = false; _selection = -1; InvalidateFrame(); base.OnDetachedFromVisualTree(e); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsPlotAvailable || !PlotArea.Contains(e.GetPosition(this))) return;
        Focus(); SelectAt(e.GetPosition(this), FrameRevision); e.Handled = true;
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    { base.OnPointerWheelChanged(e); if (IsPlotAvailable && PlotArea.Contains(e.GetPosition(this))) e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_attached || !IsPlotAvailable || !IsEffectivelyEnabled) return;
        switch (e.Key)
        {
            case Key.Right: case Key.Left:
                if (!_originals.IsEmpty) _selection = _selection < 0 ? (e.Key == Key.Right ? 0 : _originals.Length - 1)
                    : Math.Clamp(_selection + (e.Key == Key.Right ? 1 : -1), 0, _originals.Length - 1);
                break;
            case Key.Home: _selection = _originals.IsEmpty ? -1 : 0; break;
            case Key.Escape: _selection = -1; break;
            default: return;
        }
        Changed(); e.Handled = true;
    }
    private Point At(double x, double y) => new(PlotArea.X + x * PlotArea.Width, PlotArea.Y + y * PlotArea.Height);
    private IBrush Brush(int i) => i switch { 0 => Palette.Accent, 1 => Palette.Error, 2 => Brushes.CornflowerBlue,
        3 => Brushes.Orchid, 4 => Brushes.Goldenrod, 5 => Brushes.LimeGreen, 6 => Brushes.DeepPink, _ => Brushes.SlateBlue };
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Palette.Surface, new Rect(Bounds.Size));
        if (IsFocused) context.DrawRectangle(null, new Pen(Palette.Accent, 2), new Rect(Bounds.Size).Deflate(1));
        DrawingText.Draw(context, AxisDescription, new(10, 8), Palette.Text, 12, Math.Max(1, Bounds.Width - 20));
        if (Snapshot is { } snapshot && IsPlotAvailable)
        {
            var area = PlotArea;
            for (var s = 0; s < snapshot.Source.Series.Length; s++)
            {
                var x = 12 + s * Math.Max(50, (Bounds.Width - 24) / snapshot.Source.Series.Length);
                context.FillRectangle(Brush(s), new(x, 33, 8, 8));
                DrawingText.Draw(context, snapshot.Source.Series[s].Label, new(x + 12, 28), Palette.Text, 10,
                    Math.Max(1, (Bounds.Width - 24) / snapshot.Source.Series.Length - 16));
            }
            if (snapshot.XDomain.HasValue && snapshot.YDomain.HasValue)
            {
                foreach (var tick in DualAxisChartGeometry.CreateTicks(snapshot.YDomain))
                {
                    var y = area.Bottom - tick.Position * area.Height;
                    context.DrawLine(new Pen(Palette.GridLine, 1), new(area.Left, y), new(area.Right, y));
                    DrawingText.Draw(context, tick.Value.ToString("G4", CultureInfo.InvariantCulture), new(5, y - 7), Palette.Muted, 10, 67);
                }
                using (context.PushClip(area))
                {
                    foreach (var polygon in snapshot.Polygons)
                    {
                        var geometry = new StreamGeometry();
                        using (var path = geometry.Open())
                        {
                            path.BeginFigure(At(polygon.Vertices[0].X, polygon.Vertices[0].Y), true);
                            for (var i = 1; i < polygon.Vertices.Length; i++) path.LineTo(At(polygon.Vertices[i].X, polygon.Vertices[i].Y));
                            path.EndFigure(true);
                        }
                        using (context.PushOpacity(.45)) context.DrawGeometry(Brush(polygon.SeriesIndex), null, geometry);
                    }
                    foreach (var segment in snapshot.Segments)
                        context.DrawLine(new Pen(Brush(segment.SeriesIndex), segment.UpperBoundary ? 2 : 1),
                            At(segment.Start.X, segment.Start.Y), At(segment.End.X, segment.End.Y));
                    foreach (var bar in snapshot.Bars)
                    {
                        var rect = new Rect(At(bar.Left, bar.Top), new Size(bar.Width * area.Width, bar.Height * area.Height));
                        context.FillRectangle(Brush(bar.SeriesIndex), rect);
                        if (SelectedHit is { } selected && bar.Hit.Equals(selected)) context.DrawRectangle(null, new Pen(Palette.Text, 2), rect);
                    }
                    if (snapshot.Options.Mode == RangeChartMode.LinearArea)
                        foreach (var interval in snapshot.Intervals)
                        {
                            using (context.PushOpacity(.65)) context.DrawLine(new Pen(Brush(interval.SeriesIndex), 1), At(interval.X, interval.Top), At(interval.X, interval.Bottom));
                            context.DrawEllipse(Brush(interval.SeriesIndex), null, At(interval.X, interval.Top), 2, 2);
                            context.DrawEllipse(Brush(interval.SeriesIndex), null, At(interval.X, interval.Bottom), 2, 2);
                        }
                    if (MarkerPoint is { } marker) context.DrawEllipse(null, new Pen(Palette.Text, 2), marker, 7, 7);
                }
                if (snapshot.Options.Mode == RangeChartMode.LinearArea)
                    foreach (var tick in DualAxisChartGeometry.CreateTicks(snapshot.XDomain))
                        DrawingText.Draw(context, tick.Value.ToString("G4", CultureInfo.InvariantCulture), new(area.Left + tick.Position * area.Width, area.Bottom + 8), Palette.Muted, 10, 64);
                else
                {
                    var stride = Math.Max(1, (int)Math.Ceiling(snapshot.Categories.Length / 12d));
                    for (var i = 0; i < snapshot.Categories.Length; i += stride)
                        DrawingText.Draw(context, snapshot.Categories[i].ToString("G4", CultureInfo.InvariantCulture),
                            new(area.Left + (i + .5) * area.Width / snapshot.Categories.Length, area.Bottom + 8), Palette.Muted, 10, 64);
                }
            }
        }
        DrawingText.Draw(context, IsPlotAvailable ? Status : "Enlarge the range chart to see axes and intervals", new(10, Math.Max(0, Bounds.Height - 40)), Palette.Muted, 11, Math.Max(1, Bounds.Width - 20));
        if (SelectedHit is not null) DrawingText.Draw(context, AccessibleSummary, new(10, Math.Max(0, Bounds.Height - 22)), Palette.Text, 10, Math.Max(1, Bounds.Width - 20));
    }
}
