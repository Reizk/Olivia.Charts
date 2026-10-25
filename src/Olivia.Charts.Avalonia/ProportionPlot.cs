using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Olivia.Core.Charts;

namespace Olivia.Avalonia;

/// <summary>Bounded original pie/ring adapter. Every raw category remains keyboard and legend addressable.</summary>
public sealed class ProportionPlot : Control
{
    private ProportionChartSource? _source;
    private ProportionChartOptions _options = new();
    private int _selection = -1, _legendStart;
    private bool _attached;
    public ProportionPlot()
    {
        Focusable = true; ClipToBounds = true; MinHeight = 220;
        GotFocus += (_, _) => InvalidateVisual(); LostFocus += (_, _) => CancelInteraction();
        AutomationProperties.SetName(this, "Proportion chart. Arrow keys select all raw categories including zero and tiny weights. Home and End select first and last; PageUp and PageDown move legend page; Escape clears. Percentages are approximate.");
    }
    public ProportionChartSnapshot? Snapshot { get; private set; }
    public ProportionChartOptions Options => _options;
    public long FrameRevision { get; private set; }
    public UiPalette Palette { get; set; } = UiPalette.Dark;
    public ProportionChartHit? SelectedHit => Snapshot is { } snapshot && _selection >= 0 && _selection < snapshot.Source.Categories.Length ? ProportionChartGeometry.GetHit(snapshot, _selection) : null;
    public int LegendStart => _legendStart;
    public int LegendCapacity => Math.Max(1, (int)Math.Max(0, Bounds.Height - 112) / 44);
    public Rect LegendArea => new(Bounds.Width * .53, 40, Math.Max(0, Bounds.Width * .47 - 12), Math.Max(0, Bounds.Height - 112));
    public Point CircleCenter => new(Bounds.Width * .265, (Bounds.Height - 30) / 2);
    public double CircleRadius => Math.Max(0, Math.Min(Bounds.Width * .245, (Bounds.Height - 112) / 2));
    public bool IsPlotAvailable => Bounds.Width >= 480 && Bounds.Height >= 230;
    public string Status => Snapshot is not { } s ? "No source" : s.BudgetStatus == ChartBudgetStatus.InsufficientBudget
        ? $"Budget refused: {s.RequiredSliceCount} categories; no partial chart"
        : $"Dataset {s.Source.DatasetVersion} · {s.Source.Categories.Length} categories · " + (s.IsEmpty ? "No positive weight" : s.TotalOverflow ? "Total exceeds finite double range" : "Raw weights retained");
    public string AccessibleSummary => SelectedHit is { } hit ? $"{Status}. {hit.Label}; key {hit.Key}; weight {hit.Weight!.Value.ToString("R", CultureInfo.InvariantCulture)}; {Percentage(_selection)}; {GeometryDescription(_selection)}" : Status;
    public event EventHandler? StateChanged;
    public void SetSource(ProportionChartSource source)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(source);
        var snapshot = source.Query(_options); _source = source; Snapshot = snapshot; InvalidateFrame();
    }
    public void SetOptions(ProportionChartOptions options)
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
        InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty); // Never mutate after external callbacks.
    }
    public string GeometryDescription(int index) => Snapshot is { } s && index >= 0 && index < s.Slices.Length
        ? s.Slices[index].Status switch { ProportionSliceStatus.Collapsed => "positive weight; angle below geometry precision", ProportionSliceStatus.Zero => "zero weight; no sector", _ => "visible sector" }
        : "geometry unavailable";
    public string Percentage(int index)
    {
        if (Snapshot is not { } s || index < 0 || index >= s.Source.Categories.Length) return "unavailable";
        if (s.Source.Categories[index].Weight == 0) return "0%";
        if (s.BudgetStatus == ChartBudgetStatus.InsufficientBudget) return "percentage unavailable";
        var percent = s.Slices[index].Fraction * 100;
        return percent < .000001 ? "<0.000001%" : "≈" + percent.ToString("0.######", CultureInfo.InvariantCulture) + "%";
    }
    public bool SelectCategory(int index, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || !IsPlotAvailable || Snapshot is not { } s || index < 0 || index >= s.Source.Categories.Length || s.BudgetStatus == ChartBudgetStatus.InsufficientBudget) return false;
        _selection = index; _legendStart = index / LegendCapacity * LegendCapacity; Changed();
        return expectedRevision == FrameRevision && _selection == index && _attached;
    }
    public bool SelectAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || !IsPlotAvailable || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || Snapshot is not { } s) return false;
        if (LegendArea.Contains(point))
        {
            var row = (int)((point.Y - LegendArea.Y) / 44);
            return row < LegendCapacity && SelectCategory(_legendStart + row, expectedRevision);
        }
        var hit = ProportionChartGeometry.HitTest(s, (point.X - CircleCenter.X) / CircleRadius, (point.Y - CircleCenter.Y) / CircleRadius);
        if (hit is null) { _selection = -1; Changed(); return false; }
        return SelectCategory(Array.FindIndex(s.Source.Categories.ToArray(), c => c.Key == hit.Value.Key), expectedRevision);
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _attached = true; InvalidateFrame(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _attached = false; InvalidateFrame(); base.OnDetachedFromVisualTree(e); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) { base.OnPropertyChanged(change); if (change.Property == BoundsProperty) InvalidateFrame(); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var revision = FrameRevision; Focus(); SelectAt(e.GetPosition(this), revision); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (e.Key == Key.Escape) { CancelInteraction(); e.Handled = true; return; }
        if (Snapshot is not { Source.Categories.Length: > 0 } s) return;
        var index = e.Key switch
        {
            Key.Right or Key.Down => _selection < 0 ? 0 : Math.Min(s.Source.Categories.Length - 1, _selection + 1),
            Key.Left or Key.Up => _selection < 0 ? s.Source.Categories.Length - 1 : Math.Max(0, _selection - 1),
            Key.Home => 0, Key.End => s.Source.Categories.Length - 1,
            Key.PageDown => Math.Min(s.Source.Categories.Length - 1, Math.Max(0, _selection) + LegendCapacity),
            Key.PageUp => Math.Max(0, _selection - LegendCapacity), _ => -1
        };
        if (index >= 0) { SelectCategory(index, FrameRevision); e.Handled = true; }
    }
    private IBrush Brush(int index) => (index % 8) switch { 0 => Palette.Accent, 1 => Palette.Error, 2 => Brushes.CornflowerBlue, 3 => Brushes.Orchid, 4 => Brushes.Goldenrod, 5 => Brushes.LimeGreen, 6 => Brushes.DeepPink, _ => Brushes.SlateBlue };
    private Point Position(double angle, double radius) => new(CircleCenter.X + Math.Cos(angle) * radius, CircleCenter.Y + Math.Sin(angle) * radius);
    private StreamGeometry Sector(ProportionChartSlice slice)
    {
        var geometry = new StreamGeometry(); var radius = CircleRadius; var inner = radius * _options.InnerRadius;
        using var path = geometry.Open();
        path.BeginFigure(Position(slice.StartAngle, radius), true);
        // Split into <= pi arcs, including a full circle. A single coincident-endpoint arc cannot draw a circle.
        var steps = Math.Max(1, (int)Math.Ceiling(slice.SweepAngle / Math.PI));
        for (var i = 1; i <= steps; i++) path.ArcTo(Position(slice.StartAngle + slice.SweepAngle * i / steps, radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
        if (inner > 0)
        {
            path.LineTo(Position(slice.EndAngle, inner));
            for (var i = 1; i <= steps; i++) path.ArcTo(Position(slice.EndAngle - slice.SweepAngle * i / steps, inner), new Size(inner, inner), 0, false, SweepDirection.CounterClockwise);
        }
        else path.LineTo(CircleCenter);
        path.EndFigure(true); return geometry;
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Palette.Surface, new Rect(Bounds.Size));
        if (IsFocused) context.DrawRectangle(null, new Pen(Palette.Accent, 2), new Rect(Bounds.Size).Deflate(1));
        DrawingText.Draw(context, _options.InnerRadius > 0 ? "DONUT · raw category weights" : "PIE · raw category weights", new(12, 10), Palette.Text, 14, Math.Max(1, Bounds.Width - 24));
        if (IsPlotAvailable && Snapshot is { } s)
        {
            foreach (var slice in s.Slices)
                if (slice.Status == ProportionSliceStatus.Visible && slice.SweepAngle > 0)
                    context.DrawGeometry(Brush(slice.CategoryIndex), _selection == slice.CategoryIndex ? new Pen(Palette.Text, 3) : null, Sector(slice));
            if (s.IsEmpty) DrawingText.Draw(context, "No positive weight", new(16, CircleCenter.Y), Palette.Muted, 14, Math.Max(1, Bounds.Width * .5 - 24));
            for (var i = _legendStart; i < Math.Min(s.Source.Categories.Length, _legendStart + LegendCapacity); i++)
            {
                var c = s.Source.Categories[i]; var y = LegendArea.Y + (i - _legendStart) * 44;
                if (_selection == i) context.FillRectangle(Palette.Selection, new Rect(LegendArea.X, y, LegendArea.Width, 42));
                context.FillRectangle(Brush(i), new Rect(LegendArea.X + 5, y + 8, 9, 9));
                DrawingText.Draw(context, $"{c.Label} · key {c.Key}", new(LegendArea.X + 20, y + 2), Palette.Text, 12, Math.Max(1, LegendArea.Width - 24));
                DrawingText.Draw(context, $"{c.Weight!.Value.ToString("R", CultureInfo.InvariantCulture)} · {Percentage(i)}" + (i < s.Slices.Length && s.Slices[i].Status == ProportionSliceStatus.Collapsed ? " · no representable sector" : ""), new(LegendArea.X + 20, y + 21), Palette.Muted, 11, Math.Max(1, LegendArea.Width - 24));
            }
            DrawingText.Draw(context, $"Legend {_legendStart + (s.Source.Categories.Length > 0 ? 1 : 0)}–{Math.Min(s.Source.Categories.Length, _legendStart + LegendCapacity)} / {s.Source.Categories.Length} · arrows / PgUp / PgDn", new(LegendArea.X, Bounds.Height - 65), Palette.Muted, 10, Math.Max(1, LegendArea.Width));
        }
        DrawingText.Draw(context, IsPlotAvailable ? Status : "Enlarge chart to at least 480 × 230 for readable geometry and legend", new(12, Math.Max(0, Bounds.Height - 43)), Palette.Muted, 11, Math.Max(1, Bounds.Width - 24));
        if (SelectedHit is not null) DrawingText.Draw(context, AccessibleSummary, new(12, Math.Max(0, Bounds.Height - 24)), Palette.Text, 10, Math.Max(1, Bounds.Width - 24));
    }
}
