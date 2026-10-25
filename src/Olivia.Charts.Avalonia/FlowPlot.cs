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

/// <summary>Explicit projected corners. Rect's origin-plus-size reconstruction may shift an
/// endpoint by one ULP, so painted/hit node boundaries use these values directly.</summary>
public readonly record struct FlowNodeProjection(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public Point Center => new(Left + Width / 2, Top + Height / 2);
    public bool Contains(Point point) => point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
}

/// <summary>Original bounded weighted DAG adapter. Filled strips preserve the same weight width
/// at both endpoints; every raw node and edge remains addressable, including zero/collapsed edges.</summary>
public sealed class FlowPlot : Control
{
    private FlowChartSource? _source;
    private FlowChartOptions _options = new();
    private int _selection = -1, _legendStart;
    private bool _attached;
    private UiPalette _palette = UiPalette.Dark;
    private InputElement? _inputRoot;
    private long _inputSequence;
    private sealed record InputFrame(long Revision, long Sequence);
    private readonly ConditionalWeakTable<PointerPressedEventArgs, InputFrame> _pointerFrames = new();
    private readonly ConditionalWeakTable<KeyEventArgs, InputFrame> _keyFrames = new();

    static FlowPlot()
    {
        PointerPressedEvent.AddClassHandler<TopLevel>((root, e) =>
        {
            if (e.Source is FlowPlot chart && chart._attached && ReferenceEquals(chart._inputRoot, root))
                chart._pointerFrames.GetValue(e, _ => new InputFrame(chart.FrameRevision, ++chart._inputSequence));
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        KeyDownEvent.AddClassHandler<TopLevel>((root, e) =>
        {
            if (e.Source is FlowPlot chart && chart._attached && ReferenceEquals(chart._inputRoot, root))
                chart._keyFrames.GetValue(e, _ => new InputFrame(chart.FrameRevision, ++chart._inputSequence));
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }
    public FlowPlot()
    {
        Focusable = true; ClipToBounds = true; MinHeight = 240;
        GotFocus += (_, _) => InvalidateVisual(); LostFocus += (_, _) => CancelInteraction();
        AutomationProperties.SetName(this, "Bounded weighted directed acyclic flow chart. Filled ribbons use one global weight scale. Arrow keys select every node, then every edge; Home and End select first and last; PageUp and PageDown page the identity legend; Escape clears. Null, negative and non-finite weights and cycles are rejected. Internal conservation is reported explicitly, never invented. Crossings and skip-layer overlaps do not create connections. Node bodies include padding; ribbon widths encode weights.");
    }
    public FlowChartSnapshot? Snapshot { get; private set; }
    public FlowChartOptions Options => _options;
    public long FrameRevision { get; private set; }
    public UiPalette Palette
    {
        get => _palette;
        set { Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(value); _palette = value; InvalidateVisual(); }
    }
    public Rect PlotArea => new(16, 76, Math.Max(0, Bounds.Width - LegendWidth - 48), Math.Max(0, Bounds.Height - 162));
    private double LegendWidth => Math.Clamp(Bounds.Width * .29, 200, 310);
    public Rect LegendArea => new(PlotArea.Right + 20, 76, Math.Max(0, Bounds.Width - PlotArea.Right - 32), Math.Max(0, Bounds.Height - 182));
    public int LegendCapacity => (int)Math.Clamp(Math.Floor(LegendArea.Height / 44), 1, FlowChartSource.MaximumGeometry);
    public int LegendStart => _legendStart;
    public int ItemCount => Snapshot is { } s ? s.Source.Nodes.Length + s.Source.Edges.Length : 0;
    public bool IsPlotAvailable => Bounds.Width >= 520 && PlotArea.Height >= 100;
    public FlowChartHit? SelectedHit => Snapshot is { } s && _selection >= 0 && _selection < ItemCount
        ? FlowChartGeometry.GetHit(s, _selection) : null;
    public string Status => Snapshot is not { } s ? "No source" : s.BudgetStatus == ChartBudgetStatus.InsufficientBudget
        ? $"Budget refused: need {s.RequiredGeometryCount} shapes / {s.RequiredWorkCount} visits; whole flow withheld"
        : $"Dataset {s.DatasetVersion} · {s.Source.Nodes.Length} nodes · {s.Source.Edges.Length} edges · {s.Nodes.Count(n => n.BalanceStatus == FlowBalanceStatus.Imbalanced)} internal imbalances";
    public string AccessibleSummary => _selection >= 0 && Snapshot is { } s && _selection < ItemCount
        ? Status + ". " + Describe(s, _selection, full: true) : Status;
    public event EventHandler? StateChanged;
    public void SetSource(FlowChartSource source)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(source);
        var snapshot = source.Query(_options); _source = source; Snapshot = snapshot; InvalidateFrame();
    }
    public void SetOptions(FlowChartOptions options)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(options);
        var snapshot = _source?.Query(options); _options = options; Snapshot = snapshot; InvalidateFrame();
    }
    public void ReleaseSource() { Dispatcher.UIThread.VerifyAccess(); _source = null; Snapshot = null; InvalidateFrame(); }
    public void CancelInteraction() { Dispatcher.UIThread.VerifyAccess(); InvalidateFrame(); }
    private void InvalidateFrame() { FrameRevision++; _selection = -1; _legendStart = 0; Changed(); }
    private void Changed()
    {
        AutomationProperties.SetHelpText(this, AccessibleSummary);
        ToolTip.SetTip(this, SelectedHit is null ? null : AccessibleSummary);
        InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty);
    }
    public bool SelectItem(int index, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || !IsPlotAvailable || Snapshot is not { BudgetStatus: ChartBudgetStatus.Satisfied } || index < 0 || index >= ItemCount) return false;
        var interaction = ++_inputSequence;
        _selection = index; _legendStart = index / LegendCapacity * LegendCapacity; Changed();
        return _attached && expectedRevision == FrameRevision && interaction == _inputSequence && _selection == index;
    }
    public bool SelectAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || !IsPlotAvailable || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || Snapshot is not { BudgetStatus: ChartBudgetStatus.Satisfied } s) return false;
        var legend = LegendArea;
        if (point.X >= legend.Left && point.X < legend.Right && point.Y >= legend.Top && point.Y < legend.Bottom)
        {
            var row = (int)((point.Y - legend.Top) / 44);
            return row < LegendCapacity && SelectItem(_legendStart + row, expectedRevision);
        }
        var a = PlotArea;
        if (point.X >= a.Left && point.X < a.Right && point.Y >= a.Top && point.Y < a.Bottom)
        {
            // Hit the same projected paths that are painted, avoiding inverse rounding at endpoints.
            foreach (var node in s.Nodes)
            {
                var r = ProjectNode(node);
                if (r.Width > 0 && r.Height > 0 && r.Contains(point))
                    return SelectItem(node.NodeIndex, expectedRevision);
            }
            for (var i = s.Ribbons.Length - 1; i >= 0; i--)
            {
                var ribbon = s.Ribbons[i];
                if (ribbon.Status == FlowRibbonStatus.Visible && FlowChartGeometry.ContainsRibbon(point.X, point.Y,
                    a.Left + ribbon.StartX * a.Width, a.Left + ribbon.EndX * a.Width,
                    a.Top + ribbon.SourceTop * a.Height, a.Top + ribbon.TargetTop * a.Height,
                    a.Top + ribbon.SourceBottom * a.Height, a.Top + ribbon.TargetBottom * a.Height))
                    return SelectItem(s.Source.Nodes.Length + ribbon.EdgeIndex, expectedRevision);
            }
        }
        ++_inputSequence; _selection = -1; Changed(); return false;
    }
    public FlowNodeProjection ProjectNode(FlowChartNode node)
    {
        var a = PlotArea;
        return new(a.Left + node.Left * a.Width, a.Top + node.Top * a.Height,
            a.Left + node.Right * a.Width, a.Top + node.Bottom * a.Height);
    }
    /// <summary>Convenient approximate bounding box only. Painting and hit testing use ProjectNode's exact corners.</summary>
    public Rect NodeRect(FlowChartNode node)
    {
        var p = ProjectNode(node); return new(p.Left, p.Top, p.Width, p.Height);
    }
    public StreamGeometry NodeGeometry(FlowChartNode node)
    {
        var p = ProjectNode(node); var geometry = new StreamGeometry();
        using var path = geometry.Open();
        path.BeginFigure(new(p.Left, p.Top), true); path.LineTo(new(p.Right, p.Top));
        path.LineTo(new(p.Right, p.Bottom)); path.LineTo(new(p.Left, p.Bottom)); path.EndFigure(true);
        return geometry;
    }
    public StreamGeometry RibbonGeometry(FlowChartRibbon ribbon)
    {
        var a = PlotArea; var x0 = a.Left + ribbon.StartX * a.Width; var x1 = a.Left + ribbon.EndX * a.Width;
        var y0 = a.Top + ribbon.SourceTop * a.Height; var y1 = a.Top + ribbon.TargetTop * a.Height;
        var geometry = new StreamGeometry();
        using var path = geometry.Open();
        path.BeginFigure(new(x0, y0), true); path.LineTo(new(x1, y1));
        path.LineTo(new(x1, a.Top + ribbon.TargetBottom * a.Height));
        path.LineTo(new(x0, a.Top + ribbon.SourceBottom * a.Height)); path.EndFigure(true);
        return geometry;
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); _attached = true; _inputRoot = TopLevel.GetTopLevel(this); InvalidateFrame(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false; _inputRoot = null; _pointerFrames.Clear(); _keyFrames.Clear(); InvalidateFrame(); base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == BoundsProperty) InvalidateFrame(); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var hasFrame = _pointerFrames.TryGetValue(e, out var frame); _pointerFrames.Remove(e); base.OnPointerPressed(e);
        if (!hasFrame || !IsCurrent(frame!) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        if (IsCurrent(frame!)) SelectAt(e.GetPosition(this), frame!.Revision);
        e.Handled = true;
    }
    private bool IsCurrent(InputFrame frame) => _attached && frame.Revision == FrameRevision && frame.Sequence == _inputSequence;
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var hasFrame = _keyFrames.TryGetValue(e, out var frame); _keyFrames.Remove(e); base.OnKeyDown(e);
        if (!hasFrame || !IsCurrent(frame!)) return;
        if (e.Key == Key.Escape) { CancelInteraction(); e.Handled = true; return; }
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
    private static string Total(FlowWeightTotal total) => total.IsOverflow ? "overflow (finite raw weights)" : total.RoundedValue.ToString("G6", CultureInfo.InvariantCulture);
    private static string Conservation(FlowChartNode node) => node.BalanceStatus switch
    { FlowBalanceStatus.Balanced => "balanced exactly", FlowBalanceStatus.Imbalanced => "IMBALANCED", _ => node.Kind.ToString().ToLowerInvariant() + "; boundary balance not required" };
    private static string Describe(FlowChartSnapshot s, int index, bool full)
    {
        if (index < s.Source.Nodes.Length)
        {
            var raw = s.Source.Nodes[index]; var node = s.Nodes[index];
            return (full ? $"Node {raw.Label}; key {raw.Key}; " : "") + $"{Conservation(node)} · in {Total(node.Incoming)} / out {Total(node.Outgoing)} (rounded totals)"
                + (full ? $"; incoming binary64 sum {(node.Incoming.IsExact ? "exactly representable" : "rounded or overflow")}; outgoing binary64 sum {(node.Outgoing.IsExact ? "exactly representable" : "rounded or overflow")}" : "");
        }
        var edgeIndex = index - s.Source.Nodes.Length; var edge = s.Source.Edges[edgeIndex];
        var geometry = s.Ribbons[edgeIndex].Status switch
        { FlowRibbonStatus.Zero => "zero weight; no ribbon", FlowRibbonStatus.Collapsed => "positive weight below geometry precision; no ribbon", _ => "filled weighted ribbon" };
        return (full ? $"Edge key {edge.Key}; {edge.SourceKey} → {edge.TargetKey}; " : "") + $"raw weight {Raw(edge.Weight)} · {geometry}";
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Palette.Surface, new Rect(Bounds.Size));
        if (IsFocused) context.DrawRectangle(null, new Pen(Palette.Accent, 2), new Rect(Bounds.Size).Deflate(1));
        DrawingText.Draw(context, "WEIGHTED DAG · filled flow ribbons", new(12, 10), Palette.Text, 15, Math.Max(1, Bounds.Width - 24));
        DrawingText.Draw(context, "One global width scale · source-node color · 2^-48 geometry quantum; smaller positive flows collapse", new(12, 34), Palette.Muted, 11, Math.Max(1, Bounds.Width - 24));
        if (IsPlotAvailable && Snapshot is { } s)
        {
            var a = PlotArea;
            if (s.BudgetStatus == ChartBudgetStatus.Satisfied && s.Nodes.Length > 0)
            {
                using (context.PushClip(a))
                {
                    foreach (var ribbon in s.Ribbons)
                        if (ribbon.Status == FlowRibbonStatus.Visible)
                            context.DrawGeometry(NodeBrush(ribbon.SourceNodeIndex), _selection == s.Source.Nodes.Length + ribbon.EdgeIndex ? new Pen(Palette.Text, 2) : null, RibbonGeometry(ribbon));
                    foreach (var node in s.Nodes)
                    {
                        var r = ProjectNode(node); if (r.Height <= 0 || r.Width <= 0) continue;
                        context.DrawGeometry(NodeBrush(node.NodeIndex), new Pen(node.BalanceStatus == FlowBalanceStatus.Imbalanced ? Palette.Error : Palette.Surface, 2), NodeGeometry(node));
                        if (_selection == node.NodeIndex) context.DrawGeometry(null, new Pen(Palette.Text, 2), NodeGeometry(node));
                    }
                }
                // Labels live above the plot, so they cannot cover weighted paths or become hits.
                var layers = s.Nodes.GroupBy(n => n.Layer).OrderBy(g => g.Key);
                var stride = Math.Max(1, (int)Math.Ceiling(s.LayerCount * 64d / Math.Max(1, a.Width)));
                foreach (var layer in layers.Where(g => g.Key % stride == 0))
                {
                    var x = a.Left + layer.First().Left * a.Width;
                    DrawingText.Draw(context, $"Layer {layer.Key + 1}", new(Math.Min(x, a.Right - 50), a.Top - 20), Palette.Muted, 10, 70);
                }
                DrawLegend(context, s);
            }
            else DrawingText.Draw(context, s.BudgetStatus == ChartBudgetStatus.Satisfied ? "Empty flow graph" : "Flow withheld: insufficient budget", new(a.Left, a.Top + 12), Palette.Text, 13, Math.Max(1, a.Width));
            DrawingText.Draw(context, "Node / edge identities", new(LegendArea.Left, LegendArea.Top - 20), Palette.Text, 11, Math.Max(1, LegendArea.Width));
        }
        DrawingText.Draw(context, IsPlotAvailable ? Status : "Enlarge chart to at least 520 × 262 for readable flow and legend", new(12, Math.Max(0, Bounds.Height - 65)), Palette.Muted, 11, Math.Max(1, Bounds.Width - 24));
        DrawingText.Draw(context, "Arrows: all identities · Home / End · PgUp / PgDn · Escape clears · dense layer labels sampled", new(12, Math.Max(0, Bounds.Height - 44)), Palette.Muted, 10, Math.Max(1, Bounds.Width - 24));
        if (SelectedHit is not null) DrawingText.Draw(context, AccessibleSummary, new(12, Math.Max(0, Bounds.Height - 24)), Palette.Text, 10, Math.Max(1, Bounds.Width - 24));
    }
    private void DrawLegend(DrawingContext context, FlowChartSnapshot s)
    {
        var a = LegendArea;
        for (var index = _legendStart; index < Math.Min(ItemCount, _legendStart + LegendCapacity); index++)
        {
            var y = a.Top + (index - _legendStart) * 44; var isNode = index < s.Source.Nodes.Length;
            var colorIndex = isNode ? index : s.Ribbons[index - s.Source.Nodes.Length].SourceNodeIndex;
            if (_selection == index) context.FillRectangle(Palette.Selection, new Rect(a.Left, y, a.Width, 42));
            context.FillRectangle(NodeBrush(colorIndex), new Rect(a.Left + 3, y + 9, 8, 8));
            var title = isNode ? $"{s.Source.Nodes[index].Label} · {s.Source.Nodes[index].Key}"
                : $"{s.Source.Edges[index - s.Source.Nodes.Length].Key} · {s.Source.Edges[index - s.Source.Nodes.Length].SourceKey} → {s.Source.Edges[index - s.Source.Nodes.Length].TargetKey}";
            DrawingText.Draw(context, (isNode ? "N " : "E ") + title, new(a.Left + 16, y + 2), Palette.Text, 11, Math.Max(1, a.Width - 20));
            DrawingText.Draw(context, Describe(s, index, full: false), new(a.Left + 16, y + 21), Palette.Muted, 10, Math.Max(1, a.Width - 20));
        }
        DrawingText.Draw(context, $"{_legendStart + (ItemCount > 0 ? 1 : 0)}–{Math.Min(ItemCount, _legendStart + LegendCapacity)} / {ItemCount} identities · colors repeat", new(a.Left, a.Bottom + 4), Palette.Muted, 10, Math.Max(1, a.Width));
    }
}
