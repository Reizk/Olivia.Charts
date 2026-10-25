using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Olivia.Core.Charts;

namespace Olivia.Avalonia;

public readonly record struct SunburstBreadcrumb(string? Key, string Label);

/// <summary>Original bounded radial annular sectors, exact raw identities and explicit hierarchy navigation.</summary>
public sealed class SunburstPlot : Control
{
    private SunburstChartSource? _source;
    private SunburstChartOptions _options = new();
    private string? _root;
    private int _selection = -1, _legendStart;
    private bool _attached;
    private UiPalette _palette = UiPalette.Dark;
    private InputElement? _inputRoot;
    private long _inputSequence;
    private sealed record InputFrame(long Revision, long Sequence);
    private readonly ConditionalWeakTable<PointerPressedEventArgs, InputFrame> _pointerFrames = new();
    private readonly ConditionalWeakTable<KeyEventArgs, InputFrame> _keyFrames = new();

    static SunburstPlot()
    {
        PointerPressedEvent.AddClassHandler<TopLevel>((root, e) =>
        {
            if (e.Source is SunburstPlot chart && chart._attached && ReferenceEquals(chart._inputRoot, root))
                chart._pointerFrames.GetValue(e, _ => new InputFrame(chart.FrameRevision, ++chart._inputSequence));
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        KeyDownEvent.AddClassHandler<TopLevel>((root, e) =>
        {
            if (e.Source is SunburstPlot chart && chart._attached && ReferenceEquals(chart._inputRoot, root))
                chart._keyFrames.GetValue(e, _ => new InputFrame(chart.FrameRevision, ++chart._inputSequence));
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }
    public SunburstPlot()
    {
        Focusable = true; ClipToBounds = true; MinHeight = 280;
        GotFocus += (_, _) => InvalidateVisual(); LostFocus += (_, _) => CancelInteraction();
        AutomationProperties.SetName(this, "Bounded hierarchical sunburst. Angular spans encode descendant leaf sums; rings encode depth. Arrows select displayed identities including zero and precision-collapsed values. Enter drills a selected branch; Backspace returns to its parent. Click an identity to select; click an already-selected branch again to drill. Breadcrumbs navigate ancestors. Home and End, PageUp and PageDown navigate the legend. Escape clears selection. Internal raw weights are zero and leaves have finite nonnegative explicit weights.");
    }
    public SunburstChartSnapshot? Snapshot { get; private set; }
    public SunburstChartOptions Options => _options;
    public string? RootKey => _root;
    public string? FocusKey => _root;
    public long FrameRevision { get; private set; }
    public UiPalette Palette
    {
        get => _palette;
        set { Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(value); _palette = value; InvalidateVisual(); }
    }
    private double LegendWidth => Math.Clamp(Bounds.Width * .29, 200, 310);
    public Rect PlotArea => new(16, 110, Math.Max(0, Bounds.Width - LegendWidth - 48), Math.Max(0, Bounds.Height - 200));
    public Rect LegendArea => new(PlotArea.Right + 20, 110, Math.Max(0, Bounds.Width - PlotArea.Right - 32), Math.Max(0, Bounds.Height - 218));
    public Rect BreadcrumbArea => new(16, 57, Math.Max(0, Bounds.Width - 32), 28);
    public int LegendCapacity => (int)Math.Clamp(Math.Floor(LegendArea.Height / 44), 1, 256);
    public int LegendStart => _legendStart;
    public int ItemCount => Snapshot?.Sectors.Length ?? 0;
    public bool IsPlotAvailable => Bounds.Width >= 520 && PlotArea.Height >= 100;
    public TreemapChartHit? SelectedHit => Snapshot is { } s && _selection >= 0 && _selection < ItemCount ? s.Sectors[_selection].Hit : null;
    public IReadOnlyList<SunburstBreadcrumb> SelectedPath
    {
        get
        {
            var path = new List<SunburstBreadcrumb>();
            if (_source is { } source && SelectedHit is { } hit)
                for (string? key = hit.Key; key is not null; key = source.GetParentKey(key))
                    path.Add(new(key, source.Nodes[source.GetNodeIndex(key)].Label));
            path.Reverse(); return path;
        }
    }
    public IReadOnlyList<SunburstBreadcrumb> Breadcrumbs
    {
        get
        {
            var path = new List<SunburstBreadcrumb>();
            if (_source is { } source)
                for (var key = _root; key is not null; key = source.GetParentKey(key))
                    path.Add(new(key, source.Nodes[source.GetNodeIndex(key)].Label));
            path.Reverse(); path.Insert(0, new(null, "All roots")); return path;
        }
    }
    public Rect BreadcrumbRect(int index)
    {
        var count = Breadcrumbs.Count;
        if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
        var a = BreadcrumbArea; var left = a.Left + a.Width * index / count; var right = a.Left + a.Width * (index + 1) / count;
        return new(left, a.Top, right - left, a.Height);
    }
    public string Status => Snapshot is not { } s ? "No source" : s.BudgetStatus == ChartBudgetStatus.InsufficientBudget
        ? $"Budget refused: need {s.RequiredGeometryCount} sectors / {s.RequiredWorkCount} visits; whole sunburst withheld"
        : $"Dataset {s.Source.DatasetVersion} · root {_root ?? "all roots"} · {ItemCount} displayed identities · leaf sum {Total(s.Total)} · depth {_options.DisplayDepth}" + (!s.IsEmpty && !s.HasPositiveWeight ? " · all-zero leaf sums; no angular area" : "");
    public string AccessibleSummary => Status + (_selection >= 0 && Snapshot is { } s && _selection < ItemCount ? ". " + Describe(s.Sectors[_selection], true) + "; selected key path " + string.Join(" / ", SelectedPath.Select(p => p.Key)) : "");
    public event EventHandler? StateChanged;

    public void SetSource(SunburstChartSource source)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(source);
        var snapshot = source.Query(_options); _source = source; _root = null; Snapshot = snapshot; InvalidateFrame();
    }
    public void SetOptions(SunburstChartOptions options)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(options);
        var snapshot = _source?.Query(options, _root); _options = options; Snapshot = snapshot; InvalidateFrame();
    }
    public void SetFocus(string? key) => SetRoot(key);
    /// <summary>Unknown or leaf roots are rejected before publishing any state.</summary>
    public void SetRoot(string? key)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_source is null) throw new InvalidOperationException("A source is required before choosing a root.");
        var snapshot = _source.Query(_options, key); _root = key; Snapshot = snapshot; InvalidateFrame();
    }
    public void ReleaseSource() { Dispatcher.UIThread.VerifyAccess(); _source = null; _root = null; Snapshot = null; InvalidateFrame(); }
    public void CancelInteraction() { Dispatcher.UIThread.VerifyAccess(); InvalidateFrame(); }
    private void InvalidateFrame() { FrameRevision++; _selection = -1; _legendStart = 0; Changed(); }
    private void Changed()
    {
        AutomationProperties.SetHelpText(this, AccessibleSummary); ToolTip.SetTip(this, SelectedHit is null ? null : AccessibleSummary);
        InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty);
    }
    private bool CanInteract(long expectedRevision) => _attached && expectedRevision == FrameRevision && IsPlotAvailable && Snapshot is { BudgetStatus: ChartBudgetStatus.Satisfied };
    public bool SelectItem(int index, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanInteract(expectedRevision) || index < 0 || index >= ItemCount) return false;
        var sequence = ++_inputSequence;
        _selection = index; _legendStart = index / LegendCapacity * LegendCapacity; Changed();
        return _attached && expectedRevision == FrameRevision && sequence == _inputSequence && _selection == index;
    }
    public bool DrillSelected(long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanInteract(expectedRevision) || SelectedHit is not { } hit || _source!.GetChildren(hit.Key).Length == 0) return false;
        return Navigate(hit.Key, expectedRevision);
    }
    public bool GoBack(long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanInteract(expectedRevision) || _root is null) return false;
        return Navigate(_source!.GetParentKey(_root), expectedRevision);
    }
    public bool SelectBreadcrumb(int index, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess(); var breadcrumbs = Breadcrumbs;
        if (!CanInteract(expectedRevision) || index < 0 || index >= breadcrumbs.Count) return false;
        return Navigate(breadcrumbs[index].Key, expectedRevision);
    }
    private bool Navigate(string? key, long expectedRevision)
    {
        if (!CanInteract(expectedRevision)) return false;
        var source = _source; var sequence = ++_inputSequence;
        SetRoot(key); var publishedRevision = expectedRevision + 1;
        return _attached && FrameRevision == publishedRevision && sequence == _inputSequence && ReferenceEquals(source, _source) && StringComparer.Ordinal.Equals(_root, key);
    }
    public bool SelectAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanInteract(expectedRevision) || !double.IsFinite(point.X) || !double.IsFinite(point.Y)) return false;
        var legend = LegendArea;
        if (point.X >= legend.Left && point.X < legend.Right && point.Y >= legend.Top && point.Y < legend.Bottom)
        {
            var row = (int)((point.Y - legend.Top) / 44);
            return row < LegendCapacity && SelectItem(_legendStart + row, expectedRevision);
        }
        var hit = HitTest(point);
        if (hit >= 0) return SelectItem(hit, expectedRevision);
        ++_inputSequence; _selection = -1; Changed(); return false;
    }
    public Point PlotCenter => PlotArea.Center;
    public double PlotRadius => Math.Min(PlotArea.Width, PlotArea.Height) / 2;
    public Point ProjectPolar(double turn, double radius)
    {
        var angle = turn * (2 * Math.PI); var c = PlotCenter; var r = radius * PlotRadius;
        // Canonical cardinal endpoints avoid a synthetic seam from sin(2π).
        var t = turn - Math.Floor(turn);
        if (t == 0) return new(c.X + r, c.Y);
        if (t == .25) return new(c.X, c.Y + r);
        if (t == .5) return new(c.X - r, c.Y);
        if (t == .75) return new(c.X, c.Y - r);
        return new(c.X + Math.Cos(angle) * r, c.Y + Math.Sin(angle) * r);
    }
    public Point SectorCenter(SunburstChartSector sector) => ProjectPolar(
        (sector.StartTurn + sector.EndTurn) / 2, (sector.InnerRadius + sector.OuterRadius) / 2);
    /// <summary>Screen-space ownership uses projected radial endpoints, rather than an inverse
    /// normalized radius. Exact reprojection of a shared start ray owns that boundary before
    /// atan2 fallback; no tolerance or minimum angular hit width is introduced.</summary>
    public int HitTest(Point point)
    {
        if (Snapshot is not { } snapshot || PlotRadius <= 0 || !double.IsFinite(point.X) || !double.IsFinite(point.Y)) return -1;
        var center = PlotCenter; var dx = point.X - center.X; var dy = point.Y - center.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance == 0) return -1;
        // Compare against projected endpoints on the pointer ray. This retains exact cardinal
        // radial edges even when center subtraction followed by inverse scaling would round inward.
        bool AtOrOutside(double radius)
        {
            if (dy == 0) return dx > 0 ? point.X >= ProjectPolar(0, radius).X : point.X <= ProjectPolar(.5, radius).X;
            if (dx == 0) return dy > 0 ? point.Y >= ProjectPolar(.25, radius).Y : point.Y <= ProjectPolar(.75, radius).Y;
            var scale = radius * PlotRadius / distance;
            if (Math.Abs(dx) >= Math.Abs(dy))
            {
                var edge = center.X + dx * scale;
                return dx >= 0 ? point.X >= edge : point.X <= edge;
            }
            var verticalEdge = center.Y + dy * scale;
            return dy >= 0 ? point.Y >= verticalEdge : point.Y <= verticalEdge;
        }
        var turn = Math.Atan2(dy, dx) / (2 * Math.PI); if (turn < 0) turn += 1;
        if (turn >= 1 || (turn == 0 && dy < 0 && dx > 0)) turn = Math.BitDecrement(1d);
        // Reproject at the measured radius. Equality means the pointer is literally the
        // screen-coordinate representation of this ray, not merely close to it. This avoids
        // letting inverse atan2 round a representable shared boundary into its predecessor.
        var normalizedRadius = distance / PlotRadius;
        foreach (var sector in snapshot.Sectors)
            if (sector.Status == SunburstSectorStatus.Visible && AtOrOutside(sector.InnerRadius)
                && !AtOrOutside(sector.OuterRadius) && ProjectPolar(sector.StartTurn, normalizedRadius) == point)
                turn = sector.StartTurn;
        for (var i = 0; i < snapshot.Sectors.Length; i++)
        {
            var sector = snapshot.Sectors[i];
            if (sector.Status == SunburstSectorStatus.Visible && AtOrOutside(sector.InnerRadius)
                && !AtOrOutside(sector.OuterRadius) && turn >= sector.StartTurn && turn < sector.EndTurn) return i;
        }
        return -1;
    }
    public StreamGeometry SectorGeometry(SunburstChartSector sector)
    {
        var geometry = new StreamGeometry();
        if (sector.Status != SunburstSectorStatus.Visible) return geometry;
        using var path = geometry.Open();
        var start = sector.StartTurn; var end = sector.EndTurn; var middle = (start + end) / 2;
        var outer = new Size(sector.OuterRadius * PlotRadius, sector.OuterRadius * PlotRadius);
        var inner = new Size(sector.InnerRadius * PlotRadius, sector.InnerRadius * PlotRadius);
        if (end - start == 1)
        {
            // Separate oppositely wound closed contours keep the hole and avoid painting a
            // radial seam through full-circle rings when the same path is outlined.
            path.BeginFigure(ProjectPolar(start, sector.OuterRadius), true);
            path.ArcTo(ProjectPolar(middle, sector.OuterRadius), outer, 0, false, SweepDirection.Clockwise);
            path.ArcTo(ProjectPolar(end, sector.OuterRadius), outer, 0, false, SweepDirection.Clockwise);
            path.EndFigure(true);
            path.BeginFigure(ProjectPolar(end, sector.InnerRadius), true);
            path.ArcTo(ProjectPolar(middle, sector.InnerRadius), inner, 0, false, SweepDirection.CounterClockwise);
            path.ArcTo(ProjectPolar(start, sector.InnerRadius), inner, 0, false, SweepDirection.CounterClockwise);
            path.EndFigure(true); return geometry;
        }
        path.BeginFigure(ProjectPolar(start, sector.OuterRadius), true);
        // Two arcs also represent a complete circle, for which identical arc endpoints alone are insufficient.
        path.ArcTo(ProjectPolar(middle, sector.OuterRadius), outer, 0, false, SweepDirection.Clockwise);
        path.ArcTo(ProjectPolar(end, sector.OuterRadius), outer, 0, false, SweepDirection.Clockwise);
        path.LineTo(ProjectPolar(end, sector.InnerRadius));
        path.ArcTo(ProjectPolar(middle, sector.InnerRadius), inner, 0, false, SweepDirection.CounterClockwise);
        path.ArcTo(ProjectPolar(start, sector.InnerRadius), inner, 0, false, SweepDirection.CounterClockwise);
        path.EndFigure(true); return geometry;
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); _attached = true; _inputRoot = TopLevel.GetTopLevel(this); InvalidateFrame(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _attached = false; _inputRoot = null; _pointerFrames.Clear(); _keyFrames.Clear(); InvalidateFrame(); base.OnDetachedFromVisualTree(e); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == BoundsProperty) InvalidateFrame(); }
    private bool IsCurrent(InputFrame frame) => _attached && frame.Revision == FrameRevision && frame.Sequence == _inputSequence;
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var hasFrame = _pointerFrames.TryGetValue(e, out var frame); _pointerFrames.Remove(e); base.OnPointerPressed(e);
        if (!hasFrame || !IsCurrent(frame!) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(); if (!IsCurrent(frame!)) { e.Handled = true; return; }
        var point = e.GetPosition(this); var breadcrumbs = BreadcrumbArea;
        if (point.X >= breadcrumbs.Left && point.X < breadcrumbs.Right && point.Y >= breadcrumbs.Top && point.Y < breadcrumbs.Bottom)
        {
            for (var i = 0; i < Breadcrumbs.Count; i++)
            {
                var r = BreadcrumbRect(i);
                if (point.X >= r.Left && point.X < r.Right) { SelectBreadcrumb(i, frame!.Revision); break; }
            }
        }
        else
        {
            var oldSelection = _selection;
            if (SelectAt(point, frame!.Revision) && oldSelection == _selection) DrillSelected(frame.Revision);
        }
        e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var hasFrame = _keyFrames.TryGetValue(e, out var frame); _keyFrames.Remove(e); base.OnKeyDown(e);
        if (!hasFrame || !IsCurrent(frame!)) return;
        if (e.Key == Key.Escape) { CancelInteraction(); e.Handled = true; return; }
        if (e.Key == Key.Enter) { DrillSelected(frame!.Revision); e.Handled = true; return; }
        if (e.Key == Key.Back) { GoBack(frame!.Revision); e.Handled = true; return; }
        var count = ItemCount; if (count == 0) return;
        var index = e.Key switch
        {
            Key.Right or Key.Down => _selection < 0 ? 0 : Math.Min(count - 1, _selection + 1),
            Key.Left or Key.Up => _selection < 0 ? count - 1 : Math.Max(0, _selection - 1),
            Key.Home => 0, Key.End => count - 1,
            Key.PageDown => Math.Min(count - 1, Math.Max(0, _selection) + LegendCapacity),
            Key.PageUp => Math.Max(0, _selection - LegendCapacity), _ => -1
        };
        if (index >= 0) { SelectItem(index, frame!.Revision); e.Handled = true; }
    }
    private IBrush NodeBrush(int index) => (index % 8) switch
    { 0 => Palette.Accent, 1 => Palette.Error, 2 => Brushes.CornflowerBlue, 3 => Brushes.Orchid, 4 => Brushes.Goldenrod, 5 => Brushes.LimeGreen, 6 => Brushes.DeepPink, _ => Brushes.SlateBlue };
    private static string Raw(double? weight) => weight?.ToString("R", CultureInfo.InvariantCulture) ?? "missing";
    private static string Total(TreemapWeightTotal total) => total.IsOverflow ? "overflow (finite raw leaves)" : total.RoundedValue.ToString("G6", CultureInfo.InvariantCulture);
    private static string AreaStatus(SunburstSectorStatus status) => status switch
    {
        SunburstSectorStatus.Zero => "zero; no area",
        SunburstSectorStatus.Collapsed => "collapsed positive; no angular span",
        _ => "positive angular span; may be subpixel"
    };
    private static string Describe(SunburstChartSector r, bool full) =>
        (full ? $"Node {r.Hit.Label}; key {r.Hit.Key}; parent {r.Hit.ParentKey ?? "none"}; " : "") +
        $"raw {Raw(r.Hit.Weight)} · leaf sum {Total(r.Hit.Aggregate)} · {AreaStatus(r.Status)} · {(r.IsLeaf ? "leaf" : "branch; Enter to drill")}" +
        (full ? $"; {r.Hit.LeafCount} descendant leaves; leaf range [{r.Hit.LeafStart}, {r.Hit.LeafStart + r.Hit.LeafCount}); aggregate {(r.Hit.Aggregate.IsExact ? "exact binary64" : "rounded or overflow")}" : "");
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Palette.Surface, new Rect(Bounds.Size));
        if (IsFocused) context.DrawRectangle(null, new Pen(Palette.Accent, 2), new Rect(Bounds.Size).Deflate(1));
        DrawingText.Draw(context, "HIERARCHICAL SUNBURST · angular leaf-sum proportions", new(12, 10), Palette.Text, 15, Math.Max(1, Bounds.Width - 24));
        DrawingText.Draw(context, "Clockwise from right · equal ring widths; angular weighting · area varies by depth", new(12, 34), Palette.Muted, 11, Math.Max(1, Bounds.Width - 24));
        var crumbs = Breadcrumbs;
        for (var i = 0; i < crumbs.Count; i++)
        {
            var r = BreadcrumbRect(i); context.FillRectangle(i == crumbs.Count - 1 ? Palette.Selection : Palette.GridLine, r.Deflate(1));
            DrawingText.Draw(context, (i == 0 ? "" : "› ") + crumbs[i].Label, new(r.Left + 6, r.Top + 6), Palette.Text, 11, Math.Max(1, r.Width - 12));
        }
        if (IsPlotAvailable && Snapshot is { } s)
        {
            var a = PlotArea;
            if (s.BudgetStatus == ChartBudgetStatus.Satisfied && s.Sectors.Length > 0)
            {
                using (context.PushClip(a))
                {
                    foreach (var r in s.Sectors)
                        if (r.Status == SunburstSectorStatus.Visible)
                            context.DrawGeometry(NodeBrush(r.NodeIndex), null, SectorGeometry(r));
                    // Outlines overlay angular-weighted geometry; they do not reserve padding or alter areas.
                    foreach (var r in s.Sectors)
                        if (r.Status == SunburstSectorStatus.Visible)
                            context.DrawGeometry(null, new Pen(Palette.Surface, r.Depth == 0 ? 2 : 1), SectorGeometry(r));
                    if (_selection >= 0 && _selection < ItemCount && s.Sectors[_selection].Status == SunburstSectorStatus.Visible)
                        context.DrawGeometry(null, new Pen(Palette.Text, 2), SectorGeometry(s.Sectors[_selection]));
                }
                DrawLegend(context, s);
            }
            else DrawingText.Draw(context, s.BudgetStatus == ChartBudgetStatus.Satisfied ? "Empty hierarchy" : "Sunburst withheld: insufficient budget", new(a.Left, a.Top + 12), Palette.Text, 13, Math.Max(1, a.Width));
            DrawingText.Draw(context, "Displayed raw identities", new(LegendArea.Left, LegendArea.Top - 20), Palette.Text, 11, Math.Max(1, LegendArea.Width));
        }
        DrawingText.Draw(context, IsPlotAvailable ? Status : "Enlarge chart to at least 520 × 300 for readable areas and identities", new(12, Math.Max(0, Bounds.Height - 66)), Palette.Muted, 11, Math.Max(1, Bounds.Width - 24));
        DrawingText.Draw(context, "Arrows / Home / End / PgUp / PgDn · Enter: drill · Backspace: parent · click selected branch: drill · Escape: clear", new(12, Math.Max(0, Bounds.Height - 44)), Palette.Muted, 10, Math.Max(1, Bounds.Width - 24));
        if (SelectedHit is not null) DrawingText.Draw(context, AccessibleSummary, new(12, Math.Max(0, Bounds.Height - 24)), Palette.Text, 10, Math.Max(1, Bounds.Width - 24));
    }
    private void DrawLegend(DrawingContext context, SunburstChartSnapshot s)
    {
        var a = LegendArea;
        for (var i = _legendStart; i < Math.Min(ItemCount, _legendStart + LegendCapacity); i++)
        {
            var r = s.Sectors[i]; var y = a.Top + (i - _legendStart) * 44;
            if (_selection == i) context.FillRectangle(Palette.Selection, new Rect(a.Left, y, a.Width, 42));
            context.FillRectangle(NodeBrush(r.NodeIndex), new Rect(a.Left + 3, y + 9, 8, 8));
            DrawingText.Draw(context, $"{new string('›', r.Depth)} {r.Hit.Label} · {r.Hit.Key}", new(a.Left + 16, y + 2), Palette.Text, 11, Math.Max(1, a.Width - 20));
            DrawingText.Draw(context, Describe(r, false), new(a.Left + 16, y + 21), Palette.Muted, 10, Math.Max(1, a.Width - 20));
        }
        DrawingText.Draw(context, $"{_legendStart + (ItemCount > 0 ? 1 : 0)}–{Math.Min(ItemCount, _legendStart + LegendCapacity)} / {ItemCount} identities · colors repeat", new(a.Left, a.Bottom + 4), Palette.Muted, 10, Math.Max(1, a.Width));
    }
}
