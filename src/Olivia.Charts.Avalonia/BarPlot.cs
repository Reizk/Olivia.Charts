using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Olivia.Core.Charts;

namespace Olivia.Avalonia;

/// <summary>Original bounded categorical bars. Queries are synchronous (at most 2048 entries),
/// so no worker or pending source is retained. A revision fences geometry-dependent interaction.</summary>
public sealed class BarPlot : Control
{
    private BarChartSource? _source;
    private BarChartOptions _options = new();
    private int _selection = -1;
    private bool _attached;
    public BarPlot()
    {
        Focusable = true; ClipToBounds = true; MinHeight = 180;
        GotFocus += (_, _) => InvalidateVisual(); LostFocus += (_, _) => InvalidateVisual();
        AutomationProperties.SetName(this, "Bar chart. Left and right select bars; plus and minus zoom categories; Home fits; Escape clears selection.");
    }
    public BarChartSnapshot? Snapshot { get; private set; }
    public long FrameRevision { get; private set; }
    public BarChartOptions Options => _options;
    public BarChartHit? SelectedHit => Snapshot is { } snapshot && _selection >= 0 && _selection < snapshot.Bars.Length ? snapshot.Bars[_selection].Hit : null;
    public UiPalette Palette { get; set; } = UiPalette.Dark;
    public Rect PlotArea => new(76, 62, Math.Max(0, Bounds.Width - 94), Math.Max(0, Bounds.Height - 132));
    public bool IsPlotAvailable => Bounds.Width >= Math.Max(174, (Snapshot?.Source.Series.Length ?? 0) * 85) && PlotArea.Height >= 35;
    public string AxisDescription => _options.Layout == BarChartLayout.FullStacked ? "Percent · positive and negative totals normalized separately · equal category slots"
        : _options.Layout == BarChartLayout.Stacked ? "Cumulative value · signed stacks · equal category slots"
        : Snapshot?.Domain is { Minimum: > 0 } or { Maximum: < 0 }
        ? "Value · clipped range (zero offscreen) · equal category slots" : "Value · zero baseline · equal category slots";
    public string Status => Snapshot is not { } snapshot ? "No source"
        : snapshot.StackStatus is { } status && status != BarChartStackStatus.Ready ? $"Stack refused: {status}; no partial geometry shown"
        : snapshot.BudgetStatus == ChartBudgetStatus.InsufficientBudget
        ? $"Budget exceeded: need {snapshot.RequiredBarCount} bars; no partial geometry shown"
        : $"Dataset {snapshot.Source.DatasetVersion} · {snapshot.Bars.Length} bars · {snapshot.Gaps.Length} gaps · {_options.Layout}";
    public string AccessibleSummary => SelectedHit is { } hit
        ? $"{Status}. {hit.SeriesId}; {hit.Category.Label}; key {hit.Key}; value {hit.Value?.ToString("R", CultureInfo.InvariantCulture)}; {ContributionText(hit)}series version {hit.SeriesSourceVersion}; UTC ticks {hit.Category.UtcTicks?.ToString(CultureInfo.InvariantCulture) ?? "none"}"
        : Status;
    private string ContributionText(BarChartHit hit)
    {
        if (Snapshot is not { StackStatus: BarChartStackStatus.Ready } snapshot) return "";
        foreach (var entry in snapshot.Contributions)
            if (entry.Hit.SeriesId == hit.SeriesId && entry.Hit.Key == hit.Key)
                return $"cumulative [{entry.Start.ToString("R", CultureInfo.InvariantCulture)}, {entry.End.ToString("R", CultureInfo.InvariantCulture)}]; "
                    + (entry.Share is { } share ? $"share {share.ToString("R", CultureInfo.InvariantCulture)}%; " : "");
        return "";
    }
    // Host owns an already admitted immutable query result; no second query or public skip gate.
    internal void SetSnapshot(BarChartSnapshot snapshot)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.StackStatus != BarChartStackStatus.Ready || snapshot.Options.Layout is not (BarChartLayout.Stacked or BarChartLayout.FullStacked))
            throw new ArgumentException("Expected an admitted stack snapshot.", nameof(snapshot));
        _source = snapshot.Source; _options = snapshot.Options; Snapshot = snapshot; InvalidateFrame();
    }
    public event EventHandler? StateChanged;
    public void SetSource(BarChartSource source)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(source); var snapshot = source.Query(_options); _source = source; Snapshot = snapshot; InvalidateFrame();
    }
    public void SetOptions(BarChartOptions options)
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
        AutomationProperties.SetHelpText(this, AccessibleSummary); ToolTip.SetTip(this, SelectedHit is null ? null : AccessibleSummary);
        InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty);
    }
    /// <summary>Rejects delayed geometry events, even when a replacement reuses dataset versions or keys.</summary>
    public bool SelectAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || !IsPlotAvailable || !PlotArea.Contains(point) || Snapshot is not { } snapshot) return false;
        var area = PlotArea;
        var hit = BarChartGeometry.HitTest(snapshot, (point.X - area.X) / area.Width, (point.Y - area.Y) / area.Height, 2 / area.Width, 2 / area.Height);
        _selection = hit is null ? -1 : Array.FindIndex(snapshot.Bars.ToArray(), bar => bar.Hit.Equals(hit.Value));
        Changed(); return hit is not null;
    }
    /// <summary>Versioned stable category selection for linked views. Missing/gap bars stay explicitly unselectable.</summary>
    public bool TrySelectCategory(BarChartSource expectedSource, string? categoryKey, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(expectedSource);
        if (!_attached || !IsEffectivelyEnabled || expectedRevision != FrameRevision
            || Snapshot is not { } snapshot || !ReferenceEquals(snapshot.Source, expectedSource)) return false;
        var index = categoryKey is null ? -1 : Array.FindIndex(snapshot.Bars.ToArray(), bar =>
            StringComparer.Ordinal.Equals(bar.Hit.CategoryKey, categoryKey));
        if (categoryKey is not null && index < 0) return false;
        if (_selection == index) return true;
        _selection = index; var revision = ++FrameRevision; Changed();
        return _attached && IsEffectivelyEnabled && revision == FrameRevision && ReferenceEquals(Snapshot, snapshot)
            && _selection == index;
    }
    /// <summary>Selects one exact series/sample in the expected frame, including zero bars.
    /// A null key clears the marker; missing/gap/out-of-viewport samples are not invented.</summary>
    public bool TrySelectSample(BarChartSource expectedSource, string? seriesId, long? key, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(expectedSource);
        if (!_attached || !IsEffectivelyEnabled || expectedRevision != FrameRevision
            || Snapshot is not { } snapshot || !ReferenceEquals(snapshot.Source, expectedSource)) return false;
        var index = key is null ? -1 : Array.FindIndex(snapshot.Bars.ToArray(), bar =>
            StringComparer.Ordinal.Equals(bar.Hit.SeriesId, seriesId) && bar.Hit.Key == key.Value);
        if (key is not null && index < 0) return false;
        if (_selection == index) return true;
        _selection = index; var revision = ++FrameRevision; Changed();
        return _attached && IsEffectivelyEnabled && revision == FrameRevision && ReferenceEquals(Snapshot, snapshot)
            && _selection == index;
    }
    public void Zoom(double factor)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        if (Snapshot is not { CategoryCount: > 0 } snapshot || _source is null) return;
        var count = Math.Clamp((int)Math.Clamp(Math.Ceiling(snapshot.CategoryCount / factor), 1, 256), 1, _source.Categories.Length);
        var start = Math.Clamp(snapshot.CategoryStart + (snapshot.CategoryCount - count) / 2, 0, _source.Categories.Length - count);
        SetOptions(new(_options.Layout, start, count, _options.GeometryBudget, _options.FixedDomain, _options.StackWorkBudget));
    }
    public void ResetView() => SetOptions(new(_options.Layout, geometryBudget: _options.GeometryBudget, fixedDomain: _options.FixedDomain, stackWorkBudget: _options.StackWorkBudget));
    public void Pan(int categories)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (Snapshot is not { CategoryCount: > 0 } snapshot || _source is null) return;
        var start = (int)Math.Clamp((long)snapshot.CategoryStart + categories, 0, _source.Categories.Length - snapshot.CategoryCount);
        SetOptions(new(_options.Layout, start, snapshot.CategoryCount, _options.GeometryBudget, _options.FixedDomain, _options.StackWorkBudget));
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
                if (Snapshot is { Bars.Length: > 0 } snapshot) { _selection = _selection < 0 ? (e.Key == Key.Right ? 0 : snapshot.Bars.Length - 1) : Math.Clamp(_selection + (e.Key == Key.Right ? 1 : -1), 0, snapshot.Bars.Length - 1); Changed(); } break;
            case Key.Add: case Key.OemPlus: Zoom(1.5); break;
            case Key.Subtract: case Key.OemMinus: Zoom(1 / 1.5); break;
            case Key.PageDown: Pan(1); break;
            case Key.PageUp: Pan(-1); break;
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
                context.FillRectangle(Brush(i), new Rect(x, 33, 8, 8));
                DrawingText.Draw(context, snapshot.Source.Series[i].Label, new(x + 12, 28), Palette.Text, 10, Math.Max(1, (Bounds.Width - 24) / snapshot.Source.Series.Length - 16));
            }
            foreach (var tick in snapshot.Ticks)
            {
                var y = area.Bottom - tick.Position * area.Height;
                context.DrawLine(new Pen(Palette.GridLine, 1), new(area.Left, y), new(area.Right, y));
                DrawingText.Draw(context, tick.Value.ToString("G4", CultureInfo.InvariantCulture) + (_options.Layout == BarChartLayout.FullStacked ? "%" : ""), new(5, y - 7), Palette.Muted, 10, 67);
            }
            using (context.PushClip(area))
            {
                if (snapshot.Domain is { Minimum: <= 0, Maximum: >= 0 })
                {
                    var baseline = area.Y + snapshot.BaselinePosition * area.Height;
                    context.DrawLine(new Pen(Palette.Text, 1), new(area.Left, baseline), new(area.Right, baseline));
                }
                foreach (var bar in snapshot.Bars)
                {
                    var rectangle = new Rect(area.X + bar.Left * area.Width, area.Y + bar.Top * area.Height, bar.Width * area.Width, bar.Height * area.Height);
                    // A thin marker denotes zero-height geometry, including finite values that
                    // round to zero height at extreme scales. Hit metadata always keeps the raw value.
                    if (bar.Height == 0) context.DrawLine(new Pen(Brush(bar.SeriesIndex), 2), rectangle.TopLeft, rectangle.TopRight);
                    else context.FillRectangle(Brush(bar.SeriesIndex), rectangle);
                    if (SelectedHit is { } hit && hit.Equals(bar.Hit)) context.DrawRectangle(null, new Pen(Palette.Text, 2), rectangle.Inflate(2));
                }
            }
            // Labels are independently bounded to twelve; source categories are never reordered or omitted from geometry.
            var stride = Math.Max(1, (int)Math.Ceiling(snapshot.CategoryCount / 12d));
            for (var i = 0; i < snapshot.CategoryCount; i += stride)
            {
                var width = area.Width / snapshot.CategoryCount * stride;
                DrawingText.Draw(context, snapshot.Source.Categories[snapshot.CategoryStart + i].Label, new(area.Left + i * area.Width / snapshot.CategoryCount, area.Bottom + 8), Palette.Muted, 10, Math.Max(1, width - 3));
            }
        }
        DrawingText.Draw(context, IsPlotAvailable ? Status : "Enlarge the chart to show bars and labels", new(10, Math.Max(0, Bounds.Height - 40)), Palette.Muted, 11, Math.Max(1, Bounds.Width - 20));
        if (SelectedHit is not null) DrawingText.Draw(context, AccessibleSummary, new(10, Math.Max(0, Bounds.Height - 22)), Palette.Text, 10, Math.Max(1, Bounds.Width - 20));
    }
}
