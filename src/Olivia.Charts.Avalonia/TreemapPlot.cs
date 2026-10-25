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

/// <summary>Paint and input share explicit projected endpoints; Rect origin-plus-size is only a convenience.</summary>
public readonly record struct TreemapProjection(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public Point Center => new(Left + Width / 2, Top + Height / 2);
    public bool Contains(Point p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
}

public readonly record struct TreemapBreadcrumb(string? Key, string Label);

/// <summary>Original bounded slice-and-dice rectangles, exact raw identities and explicit hierarchy navigation.</summary>
public sealed class TreemapPlot : Control
{
    private TreemapChartSource? _source;
    private TreemapChartOptions _options = new();
    private string? _root;
    private int _selection = -1, _legendStart;
    private bool _attached;
    private UiPalette _palette = UiPalette.Dark;
    private InputElement? _inputRoot;
    private long _inputSequence;
    private sealed record InputFrame(long Revision, long Sequence);
    private readonly ConditionalWeakTable<PointerPressedEventArgs, InputFrame> _pointerFrames = new();
    private readonly ConditionalWeakTable<KeyEventArgs, InputFrame> _keyFrames = new();

    static TreemapPlot()
    {
        PointerPressedEvent.AddClassHandler<TopLevel>((root, e) =>
        {
            if (e.Source is TreemapPlot chart && chart._attached && ReferenceEquals(chart._inputRoot, root))
                chart._pointerFrames.GetValue(e, _ => new InputFrame(chart.FrameRevision, ++chart._inputSequence));
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        KeyDownEvent.AddClassHandler<TopLevel>((root, e) =>
        {
            if (e.Source is TreemapPlot chart && chart._attached && ReferenceEquals(chart._inputRoot, root))
                chart._keyFrames.GetValue(e, _ => new InputFrame(chart.FrameRevision, ++chart._inputSequence));
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }
    public TreemapPlot()
    {
        Focusable = true; ClipToBounds = true; MinHeight = 280;
        GotFocus += (_, _) => InvalidateVisual(); LostFocus += (_, _) => CancelInteraction();
        AutomationProperties.SetName(this, "Bounded hierarchical treemap. Rectangular areas encode descendant leaf sums. Arrows select displayed identities including zero and precision-collapsed values. Enter drills a selected branch; Backspace returns to its parent. Click an identity to select; click an already-selected branch again to drill. Breadcrumbs navigate ancestors. Home and End, PageUp and PageDown navigate the legend. Escape clears selection. Internal raw weights are zero and leaves have finite nonnegative explicit weights.");
    }
    public TreemapChartSnapshot? Snapshot { get; private set; }
    public TreemapChartOptions Options => _options;
    public string? RootKey => _root;
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
    public int ItemCount => Snapshot?.Rectangles.Length ?? 0;
    public bool IsPlotAvailable => Bounds.Width >= 520 && PlotArea.Height >= 100;
    public TreemapChartHit? SelectedHit => Snapshot is { } s && _selection >= 0 && _selection < ItemCount ? s.Rectangles[_selection].Hit : null;
    public IReadOnlyList<TreemapBreadcrumb> Breadcrumbs
    {
        get
        {
            var path = new List<TreemapBreadcrumb>();
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
        ? $"Budget refused: need {s.RequiredGeometryCount} rectangles / {s.RequiredWorkCount} visits; whole treemap withheld"
        : $"Dataset {s.Source.DatasetVersion} · root {_root ?? "all roots"} · {ItemCount} displayed identities · leaf sum {Total(s.Total)} · depth {_options.DisplayDepth}" + (!s.IsEmpty && !s.HasPositiveWeight ? " · all-zero leaf sums; no rectangle area" : "");
    public string AccessibleSummary => Status + (_selection >= 0 && Snapshot is { } s && _selection < ItemCount ? ". " + Describe(s.Rectangles[_selection], true) : "");
    public event EventHandler? StateChanged;

    public void SetSource(TreemapChartSource source)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(source);
        var snapshot = source.Query(_options); _source = source; _root = null; Snapshot = snapshot; InvalidateFrame();
    }
    public void SetOptions(TreemapChartOptions options)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(options);
        var snapshot = _source?.Query(options, _root); _options = options; Snapshot = snapshot; InvalidateFrame();
    }
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
        var a = PlotArea;
        if (point.X >= a.Left && point.X < a.Right && point.Y >= a.Top && point.Y < a.Bottom)
            for (var i = ItemCount - 1; i >= 0; i--)
            {
                var rectangle = Snapshot!.Rectangles[i]; var p = ProjectRectangle(rectangle);
                if (rectangle.Status == TreemapRectangleStatus.Visible && p.Width > 0 && p.Height > 0 && p.Contains(point)) return SelectItem(i, expectedRevision);
            }
        ++_inputSequence; _selection = -1; Changed(); return false;
    }
    public TreemapProjection ProjectRectangle(TreemapChartRectangle rectangle)
    {
        var a = PlotArea;
        return new(a.Left + rectangle.Left * a.Width, a.Top + rectangle.Top * a.Height,
            a.Left + rectangle.Right * a.Width, a.Top + rectangle.Bottom * a.Height);
    }
    public Rect RectangleBounds(TreemapChartRectangle rectangle)
    { var p = ProjectRectangle(rectangle); return new(p.Left, p.Top, p.Width, p.Height); }
    public StreamGeometry RectangleGeometry(TreemapChartRectangle rectangle)
    {
        var p = ProjectRectangle(rectangle); var geometry = new StreamGeometry();
        using var path = geometry.Open(); path.BeginFigure(new(p.Left, p.Top), true);
        path.LineTo(new(p.Right, p.Top)); path.LineTo(new(p.Right, p.Bottom)); path.LineTo(new(p.Left, p.Bottom)); path.EndFigure(true); return geometry;
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
    private static string AreaStatus(TreemapRectangleStatus status) => status switch
    {
        TreemapRectangleStatus.Zero => "zero; no area",
        TreemapRectangleStatus.Collapsed => "collapsed positive; no grid area",
        _ => "positive grid area; may be subpixel"
    };
    private static string Describe(TreemapChartRectangle r, bool full) =>
        (full ? $"Node {r.Hit.Label}; key {r.Hit.Key}; parent {r.Hit.ParentKey ?? "none"}; " : "") +
        $"raw {Raw(r.Hit.Weight)} · leaf sum {Total(r.Hit.Aggregate)} · {AreaStatus(r.Status)} · {(r.IsLeaf ? "leaf" : "branch; Enter to drill")}" +
        (full ? $"; {r.Hit.LeafCount} descendant leaves; leaf range [{r.Hit.LeafStart}, {r.Hit.LeafStart + r.Hit.LeafCount}); aggregate {(r.Hit.Aggregate.IsExact ? "exact binary64" : "rounded or overflow")}" : "");
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Palette.Surface, new Rect(Bounds.Size));
        if (IsFocused) context.DrawRectangle(null, new Pen(Palette.Accent, 2), new Rect(Bounds.Size).Deflate(1));
        DrawingText.Draw(context, "HIERARCHICAL TREEMAP · rectangular leaf-sum area", new(12, 10), Palette.Text, 15, Math.Max(1, Bounds.Width - 24));
        DrawingText.Draw(context, "Alternating slices · fixed 2^-48 grid · precision-collapsed items retain identity", new(12, 34), Palette.Muted, 11, Math.Max(1, Bounds.Width - 24));
        var crumbs = Breadcrumbs;
        for (var i = 0; i < crumbs.Count; i++)
        {
            var r = BreadcrumbRect(i); context.FillRectangle(i == crumbs.Count - 1 ? Palette.Selection : Palette.GridLine, r.Deflate(1));
            DrawingText.Draw(context, (i == 0 ? "" : "› ") + crumbs[i].Label, new(r.Left + 6, r.Top + 6), Palette.Text, 11, Math.Max(1, r.Width - 12));
        }
        if (IsPlotAvailable && Snapshot is { } s)
        {
            var a = PlotArea;
            if (s.BudgetStatus == ChartBudgetStatus.Satisfied && s.Rectangles.Length > 0)
            {
                using (context.PushClip(a))
                {
                    foreach (var r in s.Rectangles)
                        if (r.Status == TreemapRectangleStatus.Visible && (r.IsLeaf || r.Depth + 1 >= _options.DisplayDepth))
                            context.DrawGeometry(NodeBrush(r.NodeIndex), null, RectangleGeometry(r));
                    // Outlines overlay the proportional geometry; they do not reserve padding or alter areas.
                    foreach (var r in s.Rectangles)
                        if (r.Status == TreemapRectangleStatus.Visible)
                            context.DrawGeometry(null, new Pen(Palette.Surface, r.Depth == 0 ? 2 : 1), RectangleGeometry(r));
                    if (_selection >= 0 && _selection < ItemCount && s.Rectangles[_selection].Status == TreemapRectangleStatus.Visible)
                        context.DrawGeometry(null, new Pen(Palette.Text, 2), RectangleGeometry(s.Rectangles[_selection]));
                }
                DrawLegend(context, s);
            }
            else DrawingText.Draw(context, s.BudgetStatus == ChartBudgetStatus.Satisfied ? "Empty hierarchy" : "Treemap withheld: insufficient budget", new(a.Left, a.Top + 12), Palette.Text, 13, Math.Max(1, a.Width));
            DrawingText.Draw(context, "Displayed raw identities", new(LegendArea.Left, LegendArea.Top - 20), Palette.Text, 11, Math.Max(1, LegendArea.Width));
        }
        DrawingText.Draw(context, IsPlotAvailable ? Status : "Enlarge chart to at least 520 × 300 for readable areas and identities", new(12, Math.Max(0, Bounds.Height - 66)), Palette.Muted, 11, Math.Max(1, Bounds.Width - 24));
        DrawingText.Draw(context, "Arrows / Home / End / PgUp / PgDn · Enter: drill · Backspace: parent · click selected branch: drill · Escape: clear", new(12, Math.Max(0, Bounds.Height - 44)), Palette.Muted, 10, Math.Max(1, Bounds.Width - 24));
        if (SelectedHit is not null) DrawingText.Draw(context, AccessibleSummary, new(12, Math.Max(0, Bounds.Height - 24)), Palette.Text, 10, Math.Max(1, Bounds.Width - 24));
    }
    private void DrawLegend(DrawingContext context, TreemapChartSnapshot s)
    {
        var a = LegendArea;
        for (var i = _legendStart; i < Math.Min(ItemCount, _legendStart + LegendCapacity); i++)
        {
            var r = s.Rectangles[i]; var y = a.Top + (i - _legendStart) * 44;
            if (_selection == i) context.FillRectangle(Palette.Selection, new Rect(a.Left, y, a.Width, 42));
            context.FillRectangle(NodeBrush(r.NodeIndex), new Rect(a.Left + 3, y + 9, 8, 8));
            DrawingText.Draw(context, $"{new string('›', r.Depth)} {r.Hit.Label} · {r.Hit.Key}", new(a.Left + 16, y + 2), Palette.Text, 11, Math.Max(1, a.Width - 20));
            DrawingText.Draw(context, Describe(r, false), new(a.Left + 16, y + 21), Palette.Muted, 10, Math.Max(1, a.Width - 20));
        }
        DrawingText.Draw(context, $"{_legendStart + (ItemCount > 0 ? 1 : 0)}–{Math.Min(ItemCount, _legendStart + LegendCapacity)} / {ItemCount} identities · colors repeat", new(a.Left, a.Bottom + 4), Palette.Muted, 10, Math.Max(1, a.Width));
    }
}
