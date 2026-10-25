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

/// <summary>
/// Draws admitted stacked bands, not independent zero-filled areas. Every interactive point is an
/// original source observation. Frame and source fences reject delayed input after replacement,
/// layout changes or detach. Presentation never changes original identities or values.
/// </summary>
public sealed class StackedAreaPlot : Control
{
    private StackedAreaChartOptions _options = new();
    private ImmutableArray<StackedAreaChartHit> _originals = [];
    private int _selection = -1;
    private bool _attached;
    private bool _showLabels = true;
    private bool _valuesAsPercent = true;
    private double _transparency = .3;
    private double _labelAngle = 45;
    private double? _hoverYear;
    private UiPalette _palette = UiPalette.Dark;
    private string _chartTitle = "DevAV Sales";
    private Func<double, string> _argumentFormatter = value => value.ToString("G", CultureInfo.InvariantCulture);
    private Func<double, string>? _axisArgumentFormatter;
    private Func<double?, string> _valueFormatter = value => $"${Number(value)}M";
    private Func<double, string> _percentLabelFormatter = value => value.ToString("P2", CultureInfo.InvariantCulture);
    private string _argumentLabel = "Year";
    private string _valueAxisTitle = "Millions of Dollars";
    private bool _reverseLegendItems;

    public StackedAreaPlot()
    {
        Name = "StackedAreaPlot";
        Focusable = true;
        ClipToBounds = true;
        // The host reserves plot space without forcing a tall minimum window on a short display.
        MinHeight = 100;
        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) => InvalidateVisual();
        AutomationProperties.SetName(this,
            "Stacked area chart. Left and right select original observations; Home and End select the first and last; Escape clears. Hide labels for a grouped original-argument crosshair.");
    }

    public StackedAreaChartSnapshot? Snapshot { get; private set; }
    public StackedAreaChartSource? Source => Snapshot?.Source;
    public StackedAreaChartOptions Options => _options;
    public long FrameRevision { get; private set; }
    public StackedAreaChartHit? SelectedHit => _selection >= 0 && _selection < _originals.Length ? _originals[_selection] : null;
    public double? HoverYear => _hoverYear;
    public double? HoverArgument => _hoverYear;
    public bool CrosshairEnabled => !ShowLabels;
    public Rect PlotArea => new(76, 68, Math.Max(0, Bounds.Width - 96), Math.Max(0, Bounds.Height - 116));
    public bool IsPlotAvailable => Bounds.Width >= 240 && PlotArea.Width >= 130 && PlotArea.Height >= 35;
    public UiPalette Palette
    {
        get => _palette;
        set { Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(value); _palette = value; InvalidateVisual(); }
    }
    public string ChartTitle
    {
        get => _chartTitle;
        set { Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(value); _chartTitle = value; Changed(); }
    }
    public Func<double, string> ArgumentFormatter
    {
        get => _argumentFormatter;
        set { Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(value); _argumentFormatter = value; Changed(); }
    }
    /// <summary>Optional compact axis format; original-selection and hover readbacks retain ArgumentFormatter.</summary>
    public Func<double, string>? AxisArgumentFormatter
    {
        get => _axisArgumentFormatter;
        set { Dispatcher.UIThread.VerifyAccess(); _axisArgumentFormatter = value; Changed(); }
    }
    public Func<double?, string> ValueFormatter
    {
        get => _valueFormatter;
        set { Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(value); _valueFormatter = value; Changed(); }
    }
    public Func<double, string> PercentLabelFormatter
    {
        get => _percentLabelFormatter;
        set { Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(value); _percentLabelFormatter = value; Changed(); }
    }
    public string ArgumentLabel
    {
        get => _argumentLabel;
        set { Dispatcher.UIThread.VerifyAccess(); ArgumentException.ThrowIfNullOrWhiteSpace(value); _argumentLabel = value; Changed(); }
    }
    public string ValueAxisTitle
    {
        get => _valueAxisTitle;
        set { Dispatcher.UIThread.VerifyAccess(); ArgumentException.ThrowIfNullOrWhiteSpace(value); _valueAxisTitle = value; Changed(); }
    }
    /// <summary>Reverses the displayed legend only; source order, stack order and colors stay unchanged.</summary>
    public bool ReverseLegendItems
    {
        get => _reverseLegendItems;
        set { Dispatcher.UIThread.VerifyAccess(); if (_reverseLegendItems == value) return; _reverseLegendItems = value; Changed(); }
    }
    public bool ShowLabels
    {
        get => _showLabels;
        set { Dispatcher.UIThread.VerifyAccess(); if (_showLabels == value) return; _showLabels = value; InvalidateFrame(); }
    }
    public bool ValuesAsPercent
    {
        get => _valuesAsPercent;
        set { Dispatcher.UIThread.VerifyAccess(); if (_valuesAsPercent == value) return; _valuesAsPercent = value; InvalidateFrame(); }
    }
    public double Transparency
    {
        get => _transparency;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
            if (_transparency == value) return;
            _transparency = value; InvalidateFrame();
        }
    }
    public double LabelAngle
    {
        get => _labelAngle;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (!double.IsFinite(value) || value is < 0 or > 360) throw new ArgumentOutOfRangeException(nameof(value));
            if (_labelAngle == value) return;
            _labelAngle = value; InvalidateFrame();
        }
    }

    public StackedAreaChartPoint? SelectedPoint
    {
        get
        {
            if (SelectedHit is not { } selected || Snapshot is not { } snapshot) return null;
            foreach (var point in snapshot.Points) if (point.Hit.Equals(selected)) return point;
            return null;
        }
    }
    public Point? MarkerPoint => IsPlotAvailable && SelectedPoint is { } point
        ? At(point.X, (point.Top + point.Bottom) / 2) : null;
    public double? CrosshairX
    {
        get
        {
            if (!CrosshairEnabled || !IsPlotAvailable || _hoverYear is not { } year || Snapshot is not { } snapshot) return null;
            foreach (var point in snapshot.Points) if (point.Hit.X == year) return At(point.X, 0).X;
            return null;
        }
    }
    public ImmutableArray<StackedAreaChartPoint> HoverPoints => _hoverYear is { } year && Snapshot is { } snapshot
        ? snapshot.Points.Where(point => point.Hit.X == year).ToImmutableArray() : [];
    public string Status => Snapshot is not { } snapshot ? "No stacked area source"
        : $"{(snapshot.Options.Mode == StackedAreaChartMode.Normalized100 ? "100% stacked" : "Stacked")} · {snapshot.Source.RawEntryCount} original observations · {snapshot.Points.Length} visible · {snapshot.Source.Series.Length} series · dataset {snapshot.Source.DatasetVersion}"
            + (snapshot.Options.Interpolation switch
            {
                StackedAreaChartInterpolation.Linear => string.Empty,
                StackedAreaChartInterpolation.Step => " · step",
                StackedAreaChartInterpolation.InvertedStep => " · inverted step",
                StackedAreaChartInterpolation.Spline => " · spline · "
                    + (snapshot.Options.SplineAlgorithm == StackedAreaSplineAlgorithm.StrictMinMax ? "Strict Min-Max" : "Cardinal")
                    + " · tension " + snapshot.Options.LineTension.ToString("0.0", CultureInfo.InvariantCulture),
                _ => string.Empty
            });
    public string SelectionReadback
    {
        get
        {
            if (SelectedHit is not { } hit) return $"No selected observation. Click an original {ArgumentLabel.ToLowerInvariant()} band or use Left / Right, Home / End.";
            var detail = DescribeOriginal(hit);
            if (Snapshot is { } snapshot)
                foreach (var contribution in snapshot.Contributions)
                    if (contribution.Hit.Equals(hit))
                    {
                        detail += $" · visible-{ArgumentLabel.ToLowerInvariant()} share {contribution.Proportion.ToString("P2", CultureInfo.InvariantCulture)}";
                        break;
                    }
            return detail + (SelectedPoint.HasValue ? " · visible original" : " · retained hidden/outside observation; no marker");
        }
    }
    public string GroupedReadback
    {
        get
        {
            if (!CrosshairEnabled) return $"Hide labels to inspect grouped original-{ArgumentLabel.ToLowerInvariant()} values with the hover crosshair.";
            if (_hoverYear is not { } year || Snapshot is not { } snapshot) return $"Move over the plot to inspect an original {ArgumentLabel.ToLowerInvariant()}. Missing observations are not invented.";
            var rows = snapshot.Contributions.Where(point => point.Hit.X == year).Select(point =>
                $"{snapshot.Source.Series[point.SeriesIndex].Label}: {ValueFormatter(point.Hit.Y)} ({point.Proportion.ToString("P2", CultureInfo.InvariantCulture)})");
            return $"{ArgumentLabel}: {ArgumentFormatter(year)} · " + string.Join(" · ", rows);
        }
    }
    public string AccessibleSummary => $"{ChartTitle}. {Status}. {SelectionReadback}. {GroupedReadback}";
    public event EventHandler? StateChanged;

    public void SetSource(StackedAreaChartSource source, StackedAreaChartOptions? options = null)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(source);
        AcceptSnapshot(source.Query(options ?? _options));
    }
    public void SetOptions(StackedAreaChartOptions options)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(options);
        if (Source is { } source) AcceptSnapshot(source.Query(options));
        else { _options = options; InvalidateFrame(); }
    }
    private void AcceptSnapshot(StackedAreaChartSnapshot snapshot)
    {
        if (snapshot.Status != StackedAreaChartStatus.Ready)
            throw new ArgumentException($"Stacked area snapshot rejected: {snapshot.Status}. The previous accepted snapshot is unchanged.", nameof(snapshot));
        var selected = ReferenceEquals(Source, snapshot.Source) ? SelectedHit : null;
        var originals = ImmutableArray.CreateBuilder<StackedAreaChartHit>();
        foreach (var series in snapshot.Source.Series)
            foreach (var sample in series.Samples)
                if (!sample.IsGap) originals.Add(new(snapshot.Source, series.Id, series.SourceVersion, sample));
        Snapshot = snapshot;
        _options = snapshot.Options;
        _originals = originals.ToImmutable();
        _selection = selected.HasValue ? _originals.IndexOf(selected.Value) : -1;
        InvalidateFrame();
    }
    public void ReleaseSource()
    {
        Dispatcher.UIThread.VerifyAccess();
        Snapshot = null; _originals = []; _selection = -1;
        InvalidateFrame();
    }
    public bool SelectAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanAcceptInput(expectedRevision) || !PlotArea.Contains(point) || Snapshot is not { } snapshot) return false;
        var area = PlotArea;
        var hit = StackedAreaChartGeometry.HitTest(snapshot, (point.X - area.X) / area.Width,
            (point.Y - area.Y) / area.Height, area.Width, area.Height);
        _selection = hit.HasValue ? _originals.IndexOf(hit.Value) : -1;
        Changed(); return hit.HasValue;
    }
    public bool TrySelect(StackedAreaChartSource expectedSource, string? seriesId, long key, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanAcceptInput(expectedRevision) || !ReferenceEquals(Source, expectedSource)) return false;
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
    public bool HoverAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanAcceptInput(expectedRevision) || !CrosshairEnabled || Snapshot is not { } snapshot) return false;
        if (!PlotArea.Contains(point)) { ClearHover(); return false; }
        double? nearestYear = null;
        var distance = double.PositiveInfinity;
        foreach (var original in snapshot.Points)
        {
            var candidate = Math.Abs(At(original.X, 0).X - point.X);
            if (candidate >= distance) continue;
            distance = candidate; nearestYear = original.Hit.X;
        }
        if (_hoverYear != nearestYear) { _hoverYear = nearestYear; Changed(); }
        return nearestYear.HasValue;
    }
    public void ClearSelection()
    { Dispatcher.UIThread.VerifyAccess(); if (_selection < 0) return; _selection = -1; Changed(); }
    private bool CanAcceptInput(long revision) => _attached && IsEffectivelyEnabled && revision == FrameRevision && IsPlotAvailable;
    private void ClearHover() { if (_hoverYear is null) return; _hoverYear = null; Changed(); }
    private void InvalidateFrame() { FrameRevision++; _hoverYear = null; Changed(); }
    private void Changed()
    {
        AutomationProperties.SetHelpText(this, AccessibleSummary);
        InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == BoundsProperty) InvalidateFrame(); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); _attached = true; InvalidateFrame(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _attached = false; InvalidateFrame(); base.OnDetachedFromVisualTree(e); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsPlotAvailable || !PlotArea.Contains(e.GetPosition(this))) return;
        Focus(); SelectAt(e.GetPosition(this), FrameRevision); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    { base.OnPointerMoved(e); if (CrosshairEnabled) HoverAt(e.GetPosition(this), FrameRevision); }
    protected override void OnPointerExited(PointerEventArgs e)
    { base.OnPointerExited(e); ClearHover(); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    { base.OnPointerWheelChanged(e); if (IsPlotAvailable && PlotArea.Contains(e.GetPosition(this))) e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!CanAcceptInput(FrameRevision)) return;
        switch (e.Key)
        {
            case Key.Left: case Key.Right:
                if (!_originals.IsEmpty) _selection = _selection < 0 ? (e.Key == Key.Right ? 0 : _originals.Length - 1)
                    : Math.Clamp(_selection + (e.Key == Key.Right ? 1 : -1), 0, _originals.Length - 1);
                break;
            case Key.Home: _selection = _originals.IsEmpty ? -1 : 0; break;
            case Key.End: _selection = _originals.Length - 1; break;
            case Key.Escape: _selection = -1; _hoverYear = null; break;
            default: return;
        }
        Changed(); e.Handled = true;
    }

    private Point At(double x, double y) => new(PlotArea.X + x * PlotArea.Width, PlotArea.Y + y * PlotArea.Height);
    private IBrush SeriesBrush(int i) => i switch
    {
        0 => Palette.Accent, 1 => Palette.Error, 2 => Brushes.CornflowerBlue, 3 => Brushes.Orchid,
        4 => Brushes.Goldenrod, 5 => Brushes.LimeGreen, 6 => Brushes.DeepPink, _ => Brushes.SlateBlue
    };
    private static string Number(double? value) => value?.ToString("G", CultureInfo.InvariantCulture) ?? "missing";
    private string DescribeOriginal(StackedAreaChartHit hit)
    {
        var series = hit.Source.Series.First(item => StringComparer.Ordinal.Equals(item.Id, hit.SeriesId));
        return $"{series.Label} · {ArgumentLabel.ToLowerInvariant()} {(hit.X is { } x ? ArgumentFormatter(x) : "missing")} · raw {ValueFormatter(hit.Y)} · key {hit.Key}";
    }
    public string LabelText(StackedAreaChartPoint point) => Options.Mode == StackedAreaChartMode.Normalized100 && ValuesAsPercent
        ? PercentLabelFormatter(point.Proportion) : ValueFormatter(point.Hit.Y);

    /// <summary>Ordinary label angle positions text around the original stack marker; it never rotates text.</summary>
    public Point LabelOrigin(StackedAreaChartPoint point)
    {
        var marker = At(point.X, point.Top);
        if (Options.Mode == StackedAreaChartMode.Normalized100)
            return new(marker.X + 5, At(point.X, (point.Top + point.Bottom) / 2).Y - 6);
        var radians = LabelAngle * Math.PI / 180;
        var direction = Math.Cos(radians);
        return new(marker.X + direction * 15 - (direction < 0 ? 60 : 0), marker.Y - Math.Sin(radians) * 15 - 6);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Palette.Surface, new Rect(Bounds.Size));
        if (IsFocused) context.DrawRectangle(null, new Pen(Palette.Accent, 2), new Rect(Bounds.Size).Deflate(1));
        DrawingText.Draw(context, ChartTitle, new(10, 6), Palette.Text, 16, Math.Max(1, Bounds.Width - 20));
        var units = Options.Mode == StackedAreaChartMode.Normalized100 ? $"Visible-{ArgumentLabel.ToLowerInvariant()} share · 100% stacked" : $"{ValueAxisTitle} · stacked bands";
        DrawingText.Draw(context, units, new(10, 30), Palette.Muted, 11, Math.Max(1, Bounds.Width - 20));
        if (Snapshot is { } snapshot && IsPlotAvailable && snapshot.XDomain.HasValue && snapshot.YDomain.HasValue)
        {
            var area = PlotArea;
            var slot = Math.Max(1, (Bounds.Width - 20) / Math.Max(1, snapshot.Source.Series.Length));
            for (var legendIndex = 0; legendIndex < snapshot.Source.Series.Length; legendIndex++)
            {
                var i = ReverseLegendItems ? snapshot.Source.Series.Length - 1 - legendIndex : legendIndex;
                var series = snapshot.Source.Series[i];
                var visible = snapshot.Options.AllSeries || snapshot.Options.VisibleSeriesIds.Contains(series.Id, StringComparer.Ordinal);
                var x = 10 + legendIndex * slot;
                using (context.PushOpacity(visible ? 1 : .35))
                {
                    context.FillRectangle(SeriesBrush(i), new Rect(x, 51, 8, 8));
                    DrawingText.Draw(context, series.Label, new(x + 12, 47), Palette.Text, 10, Math.Max(1, slot - 16));
                }
            }
            foreach (var tick in snapshot.YTicks)
            {
                var y = area.Bottom - tick.Position * area.Height;
                context.DrawLine(new Pen(Palette.GridLine, 1), new(area.Left, y), new(area.Right, y));
                var label = Options.Mode == StackedAreaChartMode.Normalized100 ? tick.Value.ToString("P0", CultureInfo.InvariantCulture)
                    : tick.Value.ToString("G4", CultureInfo.InvariantCulture);
                DrawingText.Draw(context, label, new(5, y - 7), Palette.Muted, 10, 66);
            }
            using (context.PushClip(area))
            {
                if (snapshot.Options.Interpolation == StackedAreaChartInterpolation.Spline && snapshot.Options.LineTension > 0)
                {
                    // Tessellation pieces share exact edges. Rasterizing each with opacity
                    // leaves antialiased seams, so fill every series in one coverage pass.
                    // Separate closed figures preserve clipped/disjoint support and Cardinal
                    // crossing lobes without expanding vertices or connecting absent intervals.
                    foreach (var series in snapshot.Polygons.GroupBy(polygon => polygon.SeriesIndex))
                    {
                        var geometry = new StreamGeometry();
                        using (var path = geometry.Open())
                        {
                            // Same-series pieces have disjoint X interiors; NonZero retains
                            // either lobe winding, including an inverted Cardinal band.
                            path.SetFillRule(FillRule.NonZero);
                            foreach (var polygon in series)
                            {
                                if (polygon.Vertices.IsEmpty) continue;
                                path.BeginFigure(At(polygon.Vertices[0].X, polygon.Vertices[0].Y), true);
                                for (var i = 1; i < polygon.Vertices.Length; i++) path.LineTo(At(polygon.Vertices[i].X, polygon.Vertices[i].Y));
                                path.EndFigure(true);
                            }
                        }
                        using (context.PushOpacity(1 - Transparency)) context.DrawGeometry(SeriesBrush(series.Key), null, geometry);
                    }
                }
                else
                {
                    foreach (var polygon in snapshot.Polygons)
                    {
                        if (polygon.Vertices.IsEmpty) continue;
                        var geometry = new StreamGeometry();
                        using (var path = geometry.Open())
                        {
                            path.BeginFigure(At(polygon.Vertices[0].X, polygon.Vertices[0].Y), true);
                            for (var i = 1; i < polygon.Vertices.Length; i++) path.LineTo(At(polygon.Vertices[i].X, polygon.Vertices[i].Y));
                            path.EndFigure(true);
                        }
                        using (context.PushOpacity(1 - Transparency)) context.DrawGeometry(SeriesBrush(polygon.SeriesIndex), null, geometry);
                    }
                }
                foreach (var segment in snapshot.Segments)
                    context.DrawLine(new Pen(SeriesBrush(segment.SeriesIndex), segment.UpperBoundary ? 1.5 : 1),
                        At(segment.Start.X, segment.Start.Y), At(segment.End.X, segment.End.Y));
                foreach (var point in snapshot.Points)
                    context.DrawEllipse(SeriesBrush(point.SeriesIndex), null, At(point.X, point.Top), 2, 2);
                if (CrosshairX is { } crosshair)
                {
                    context.DrawLine(new Pen(Palette.Text, 1), new(crosshair, area.Top), new(crosshair, area.Bottom));
                    foreach (var point in HoverPoints)
                        context.DrawEllipse(SeriesBrush(point.SeriesIndex), new Pen(Palette.Text, 1), At(point.X, (point.Top + point.Bottom) / 2), 4, 4);
                }
                if (MarkerPoint is { } marker) context.DrawEllipse(null, new Pen(Palette.Text, 2), marker, 7, 7);
            }
            if (ShowLabels)
            {
                // Bounded, simple label placement. Full grouped readback remains available outside
                // the canvas; the proprietary label-overlap resolver is intentionally not reproduced.
                foreach (var point in snapshot.Points)
                {
                    var origin = LabelOrigin(point);
                    origin = new(Math.Clamp(origin.X, 2, Math.Max(2, Bounds.Width - 70)),
                        Math.Clamp(origin.Y, area.Top + 2, Math.Max(area.Top + 2, area.Bottom - 12)));
                    DrawingText.Draw(context, LabelText(point), origin, Palette.Text, 10, 68);
                }
            }
            var arguments = snapshot.Source.Coordinates.Where(x => x >= snapshot.XDomain.Value.Minimum && x <= snapshot.XDomain.Value.Maximum).ToArray();
            var axisFormatter = AxisArgumentFormatter ?? ArgumentFormatter;
            var tickWidth = AxisArgumentFormatter is null ? 55 : 82;
            var stride = Math.Max(1, (int)Math.Ceiling(arguments.Length / Math.Max(2, area.Width / (AxisArgumentFormatter is null ? 60 : 82))));
            for (var i = 0; i < arguments.Length; i += stride)
            {
                var x = At(DualAxisChartGeometry.NormalizeY(arguments[i], snapshot.XDomain.Value), 0).X;
                DrawingText.Draw(context, axisFormatter(arguments[i]),
                    new(Math.Clamp(x - (AxisArgumentFormatter is null ? 15 : 40), area.Left, Math.Max(area.Left, Bounds.Width - tickWidth - 3)), area.Bottom + 7), Palette.Muted, 10, tickWidth);
            }
        }
        DrawingText.Draw(context, IsPlotAvailable ? Status : $"Enlarge the plot to inspect stacked bands and original {ArgumentLabel.ToLowerInvariant()}s",
            new(10, Math.Max(0, Bounds.Height - 20)), Palette.Muted, 10, Math.Max(1, Bounds.Width - 20));
    }
}
