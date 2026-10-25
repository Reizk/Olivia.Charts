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

/// <summary>Original bounded matrix view. Raw values and missing cells retain their exact keyed identity.</summary>
public sealed class HeatmapPlot : Control
{
    private HeatmapChartSource? _source;
    private HeatmapChartOptions _options = new();
    private int _selection = -1;
    private bool _attached;
    private UiPalette _palette = UiPalette.Dark;
    private InputElement? _inputRoot;
    private sealed record PointerFrame(long Revision);
    private readonly ConditionalWeakTable<PointerPressedEventArgs, PointerFrame> _pointerFrames = new();
    private readonly ConditionalWeakTable<KeyEventArgs, PointerFrame> _keyFrames = new();

    static HeatmapPlot()
    {
        // Class handlers run before instance tunnel callbacks on the root. They retain no
        // chart references; event-keyed weak tables isolate nested events and are cleared on detach.
        PointerPressedEvent.AddClassHandler<TopLevel>((root, e) =>
        {
            if (e.Source is HeatmapPlot chart && chart._attached && ReferenceEquals(chart._inputRoot, root)) chart.CapturePointer(root, e);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        KeyDownEvent.AddClassHandler<TopLevel>((root, e) =>
        {
            if (e.Source is HeatmapPlot chart && chart._attached && ReferenceEquals(chart._inputRoot, root)) chart.CaptureKey(root, e);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }
    public HeatmapPlot()
    {
        Focusable = true; ClipToBounds = true; MinHeight = 200;
        GotFocus += (_, _) => InvalidateVisual(); LostFocus += (_, _) => CancelInteraction();
        AutomationProperties.SetName(this, "Bounded matrix heatmap. Arrow keys select cells; Home and End select first and last; Escape clears. Missing cells have a cross and explicit missing text in selection.");
    }
    public HeatmapChartSnapshot? Snapshot { get; private set; }
    public HeatmapChartOptions Options => _options;
    public long FrameRevision { get; private set; }
    public UiPalette Palette
    {
        get => _palette;
        set { Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(value); _palette = value; InvalidateVisual(); }
    }
    public Rect PlotArea => new(132, 90, Math.Max(0, Bounds.Width - 290), Math.Max(0, Bounds.Height - 200));
    public Rect LegendArea => new(PlotArea.Right + 20, PlotArea.Top, 20, PlotArea.Height);
    public bool IsPlotAvailable => Bounds.Width >= 460 && PlotArea.Height >= 40;
    public HeatmapChartHit? SelectedHit => Snapshot is { } s && _selection >= 0 && _selection < s.Cells.Length ? s.Cells[_selection].Hit : null;
    public string Status => Snapshot is not { } s ? "No source" : s.BudgetStatus == ChartBudgetStatus.InsufficientBudget
        ? $"Budget refused: need {s.RequiredCellCount} cells / {s.RequiredWorkCount} visits; no partial matrix"
        : $"Dataset {s.DatasetVersion} · {s.Source.Rows.Length} rows × {s.Source.Columns.Length} columns · {s.Cells.Length} cells · {s.Cells.Count(c => c.Status == HeatmapCellStatus.Missing)} missing";
    public string AccessibleSummary => SelectedHit is { } h
        ? $"{Status}. Row {h.Row.Label} [key {h.RowKey}]; column {h.Column.Label} [key {h.ColumnKey}]; raw value {Raw(h.Value)}; dataset version {h.DatasetVersion}"
        : Status;
    private static string Raw(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "missing (no numeric value)";
    private static SolidColorBrush Brush(HeatmapColor color) => new(Color.FromRgb(color.R, color.G, color.B));
    public event EventHandler? StateChanged;

    public void SetSource(HeatmapChartSource source)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(source);
        var snapshot = source.Query(_options); _source = source; Snapshot = snapshot; InvalidateFrame();
    }
    public void SetOptions(HeatmapChartOptions options)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(options);
        var snapshot = _source?.Query(options); _options = options; Snapshot = snapshot; InvalidateFrame();
    }
    public void ReleaseSource() { Dispatcher.UIThread.VerifyAccess(); _source = null; Snapshot = null; InvalidateFrame(); }
    public void CancelInteraction() { Dispatcher.UIThread.VerifyAccess(); InvalidateFrame(); }
    private void InvalidateFrame() { FrameRevision++; _selection = -1; Changed(); }
    private void Changed()
    {
        AutomationProperties.SetHelpText(this, AccessibleSummary);
        ToolTip.SetTip(this, SelectedHit is null ? null : AccessibleSummary);
        InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty); // Never mutate state after client callbacks.
    }
    public bool SelectCell(int index, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || !IsPlotAvailable || Snapshot is not { } s || index < 0 || index >= s.Cells.Length) return false;
        _selection = index; Changed(); return _attached && expectedRevision == FrameRevision && _selection == index;
    }
    public bool SelectAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || !IsPlotAvailable || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || Snapshot is not { } s) return false;
        var a = PlotArea;
        // Half-open cells exclude the outer right/bottom boundary, matching Core hit testing.
        if (point.X < a.Left || point.X >= a.Right || point.Y < a.Top || point.Y >= a.Bottom) return false;
        // Compare the very same projected endpoints used to draw. Inverse normalization can
        // round a shared pixel-space edge back into its preceding cell at rational boundaries.
        for (var index = 0; index < s.Cells.Length; index++)
        {
            var cell = s.Cells[index];
            var left = a.Left + cell.Left * a.Width; var right = a.Left + cell.Right * a.Width;
            var top = a.Top + cell.Top * a.Height; var bottom = a.Top + cell.Bottom * a.Height;
            if (point.X >= left && point.X < right && point.Y >= top && point.Y < bottom)
                return SelectCell(index, expectedRevision);
        }
        return false;
    }
    private void CapturePointer(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, this)) _pointerFrames.GetValue(e, _ => new PointerFrame(FrameRevision));
    }
    private void CaptureKey(object? sender, KeyEventArgs e)
    {
        if (ReferenceEquals(e.Source, this)) _keyFrames.GetValue(e, _ => new PointerFrame(FrameRevision));
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e); _attached = true; _inputRoot = TopLevel.GetTopLevel(this);
        InvalidateFrame();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false; _inputRoot = null;
        _pointerFrames.Clear(); _keyFrames.Clear(); InvalidateFrame(); base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change); if (change.Property == BoundsProperty) InvalidateFrame();
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var hasFrame = _pointerFrames.TryGetValue(e, out var frame); _pointerFrames.Remove(e); base.OnPointerPressed(e);
        // Root-captured event identity fences focus callbacks, nested events and detach/reattach.
        if (!hasFrame || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !PlotArea.Contains(e.GetPosition(this))) return;
        Focus(); SelectAt(e.GetPosition(this), frame!.Revision); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var hasFrame = _keyFrames.TryGetValue(e, out var frame); _keyFrames.Remove(e); base.OnKeyDown(e);
        if (!hasFrame || !_attached || frame!.Revision != FrameRevision) return;
        if (e.Key == Key.Escape) { CancelInteraction(); e.Handled = true; return; }
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)) return;
        if (Snapshot is { Cells.Length: > 0 } s)
        {
            var index = Math.Max(0, _selection); var columns = s.Source.Columns.Length;
            var row = index / columns; var column = index % columns;
            index = e.Key switch
            {
                Key.Home => 0, Key.End => s.Cells.Length - 1,
                Key.Left => row * columns + Math.Max(0, column - 1),
                Key.Right => row * columns + Math.Min(columns - 1, column + (_selection < 0 ? 0 : 1)),
                Key.Up => Math.Max(0, row - 1) * columns + column,
                Key.Down => Math.Min(s.Source.Rows.Length - 1, row + (_selection < 0 ? 0 : 1)) * columns + column,
                _ => index
            };
            SelectCell(index, frame.Revision);
        }
        e.Handled = true;
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Palette.Surface, new Rect(Bounds.Size));
        if (IsFocused) context.DrawRectangle(null, new Pen(Palette.Accent, 2), new Rect(Bounds.Size).Deflate(1));
        DrawingText.Draw(context, "SYNTHETIC MATRIX · exact raw values · bounded 64 × 64", new(10, 8), Palette.Text, 12, Math.Max(1, Bounds.Width - 20));
        DrawingText.Draw(context, "Rows ↓   Columns →   Color scale = finite values only; legend labels rounded; × = missing", new(10, 30), Palette.Muted, 11, Math.Max(1, Bounds.Width - 20));
        if (Snapshot is { } s && IsPlotAvailable)
        {
            var a = PlotArea;
            if (s.BudgetStatus == ChartBudgetStatus.Satisfied && s.Cells.Length > 0)
            {
                using (context.PushClip(a))
                {
                    foreach (var cell in s.Cells)
                    {
                        var r = CellRect(cell, a); context.FillRectangle(Brush(cell.Color), r);
                        if (r.Width >= 14 && r.Height >= 14) context.DrawRectangle(null, new Pen(Palette.Surface, 1), r);
                        if (cell.Status == HeatmapCellStatus.Missing) DrawMissing(context, r);
                    }
                    if (_selection >= 0 && _selection < s.Cells.Length)
                    {
                        var r = CellRect(s.Cells[_selection], a);
                        context.DrawRectangle(null, new Pen(Brushes.Black, 3), r.Deflate(1.5));
                        context.DrawRectangle(null, new Pen(Brushes.White, 1), r.Deflate(1.5));
                    }
                }
                DrawAxes(context, s, a); DrawLegend(context, s);
                context.DrawRectangle(null, new Pen(Palette.Muted, 1), a);
            }
            else DrawingText.Draw(context, s.BudgetStatus == ChartBudgetStatus.Satisfied ? "Empty matrix" : "Matrix withheld: insufficient budget", new(a.Left, a.Top + 12), Palette.Text, 13, a.Width);
        }
        DrawingText.Draw(context, IsPlotAvailable ? Status : "Enlarge chart to show matrix, axes and color scale", new(10, Math.Max(0, Bounds.Height - 65)), Palette.Muted, 11, Math.Max(1, Bounds.Width - 20));
        DrawingText.Draw(context, "Click or use ← ↑ ↓ →; Home / End; Escape clears. Dense axis labels are sampled.", new(10, Math.Max(0, Bounds.Height - 45)), Palette.Muted, 10, Math.Max(1, Bounds.Width - 20));
        if (SelectedHit is not null) DrawingText.Draw(context, AccessibleSummary, new(10, Math.Max(0, Bounds.Height - 24)), Palette.Text, 10, Math.Max(1, Bounds.Width - 20));
    }
    private static Rect CellRect(HeatmapChartCell cell, Rect area)
    {
        var left = area.Left + cell.Left * area.Width; var right = area.Left + cell.Right * area.Width;
        var top = area.Top + cell.Top * area.Height; var bottom = area.Top + cell.Bottom * area.Height;
        return new(left, top, right - left, bottom - top);
    }
    private void DrawMissing(DrawingContext context, Rect r)
    {
        var margin = Math.Min(6, Math.Min(r.Width, r.Height) / 4); var cross = r.Deflate(margin);
        context.DrawLine(new Pen(Brushes.Black, 1), cross.TopLeft, cross.BottomRight);
        context.DrawLine(new Pen(Brushes.Black, 1), cross.TopRight, cross.BottomLeft);
        if (r.Width >= 64 && r.Height >= 30)
        {
            var label = new Rect(r.Center.X - 28, r.Center.Y - 8, 56, 16);
            context.FillRectangle(Brush(HeatmapChartPalette.MissingColor), label);
            DrawingText.Draw(context, "Missing", label.TopLeft + new Vector(7, 0), Brushes.Black, 10, 48);
        }
    }
    private void DrawAxes(DrawingContext context, HeatmapChartSnapshot snapshot, Rect a)
    {
        var rows = snapshot.Source.Rows; var columns = snapshot.Source.Columns;
        var rowStride = Math.Max(1, (int)Math.Ceiling(rows.Length * 18d / a.Height));
        var columnStride = Math.Max(1, (int)Math.Ceiling(columns.Length * 86d / a.Width));
        for (var row = 0; row < rows.Length; row += rowStride)
            DrawingText.Draw(context, rows[row].Label, new(10, a.Top + (row + .5) / rows.Length * a.Height - 7), Palette.Text, 11, a.Left - 18);
        for (var column = 0; column < columns.Length; column += columnStride)
        {
            var left = a.Left + (double)column / columns.Length * a.Width;
            DrawingText.Draw(context, columns[column].Label, new(left + 3, a.Top - 23), Palette.Text, 11, Math.Max(1, Math.Min(columnStride, columns.Length - column) * a.Width / columns.Length - 6));
        }
        DrawingText.Draw(context, "Column labels / stable keys in selection", new(a.Left, a.Bottom + 10), Palette.Muted, 10, a.Width);
    }
    private void DrawLegend(DrawingContext context, HeatmapChartSnapshot snapshot)
    {
        var a = LegendArea;
        DrawingText.Draw(context, "Value (rounded)", new(a.Left, a.Top - 23), Palette.Text, 11, 124);
        if (snapshot.Legend.Length > 0)
        {
            // One strip per logical pixel, capped independently of matrix size. Cells and ramp use the same Core palette.
            var strips = (int)Math.Clamp(Math.Ceiling(a.Height), 1, 1024);
            for (var i = 0; i < strips; i++)
            {
                var position = snapshot.Legend.Length == 1 ? snapshot.Legend[0].Position : 1 - (i + .5) / strips;
                context.FillRectangle(Brush(HeatmapChartPalette.ColorAt(position)), new Rect(a.Left, a.Top + (double)i / strips * a.Height, a.Width, a.Height / strips));
            }
            for (var index = 0; index < snapshot.Legend.Length; index++)
            {
                // Keep endpoints legible in a short viewport; full exact values remain in selection.
                if (index > 0 && index < snapshot.Legend.Length - 1 && a.Height < (snapshot.Legend.Length - 1) * 18) continue;
                var tick = snapshot.Legend[index];
                var y = a.Bottom - tick.Position * a.Height;
                context.DrawLine(new Pen(Palette.Text, 1), new(a.Right, y), new(a.Right + 4, y));
                DrawingText.Draw(context, tick.Value.ToString("G4", CultureInfo.InvariantCulture), new(a.Right + 7, Math.Clamp(y - 7, a.Top, a.Bottom - 14)), Palette.Text, 10, 108);
            }
        }
        else DrawingText.Draw(context, "No finite values", new(a.Left, a.Top), Palette.Muted, 10, 130);
        DrawingText.Draw(context, "× Missing", new(a.Left, a.Bottom + 10), Palette.Text, 10, 130);
    }
}
