using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Olivia.Core.Charts;
using Olivia.Core.Snapshots;

namespace Olivia.Avalonia;

public enum HistoryRangeHandle { Left, Window, Right }

/// <summary>Single-series overview and release-to-commit range handles. The borrowed main plot owns
/// source admission. The independent bounded overview reuses HistoryPlot's latest-only worker.</summary>
public sealed class HistoryRangeNavigator : global::Avalonia.Controls.Grid
{
    private readonly HistoryPlot _main;
    private readonly HistoryPlot _overview = new() { PointBudget = 160, IsHitTestVisible = false, Focusable = false };
    private readonly SelectionOverlay _overlay;
    private bool _subscribed, _released;
    private long _overviewMainIdentity;
    private HistoryRangeToken? _dragToken;
    private TimeWindow? _dragWindow;
    private HistoryRangeHandle _handle;
    private double _dragX;
    private IPointer? _pointer;

    private sealed class SelectionOverlay(HistoryRangeNavigator owner) : Control
    {
        public override void Render(DrawingContext context) { base.Render(context); owner.DrawSelection(context); }
    }

    public HistoryRangeNavigator(HistoryPlot main)
    {
        ArgumentNullException.ThrowIfNull(main);
        _main = main;
        Focusable = true; MinHeight = 180; ClipToBounds = true; Background = Brushes.Transparent;
        _overview.DisplayFormat = main.DisplayFormat;
        _overlay = new(this) { IsHitTestVisible = false };
        Children.Add(_overview); Children.Add(_overlay);
        _overview.StateChanged += (_, _) => { _overview.Opacity = 1; _overlay.InvalidateVisual(); };
        AutomationProperties.SetName(this, "UTC range navigator. Drag either handle or selected window. Release commits; Escape cancels.");
        AutomationProperties.SetHelpText(this, "Left and right pan; Shift plus arrows adjusts start; Control plus arrows adjusts end; plus/minus zoom; Home selects whole range.");
        GotFocus += (_, _) => _overlay.InvalidateVisual();
        LostFocus += (_, _) => _overlay.InvalidateVisual();
        Subscribe();
        RefreshFromMain();
    }

    public HistoryRangeCoordinator State { get; private set; } = HistoryRangeCoordinator.Empty;
    public TimeWindow? PreviewWindow { get; private set; }
    public bool IsPreviewing => _dragToken is not null;
    public HistoryChartSnapshot? OverviewSnapshot => _overview.Snapshot;
    public SnapshotStoreStatistics OverviewSourceStatistics => _overview.SourceStatistics;
    public Task WhenIdle => _overview.WhenIdle;
    public Rect TrackBounds => _overview.PlotBounds;
    public event EventHandler? RangeChanged;
    public UiPalette Palette { get => _overview.Palette; set { _overview.Palette = value; _overlay.InvalidateVisual(); } }

    private void Subscribe()
    {
        if (_subscribed || _released) return;
        _main.SourceChanged += SynchronizeSource;
        _main.ViewChanged += SynchronizeView;
        _subscribed = true;
    }
    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _main.SourceChanged -= SynchronizeSource;
        _main.ViewChanged -= SynchronizeView;
        _subscribed = false;
    }
    private void SynchronizeSource(object? sender, HistorySourceChangedEventArgs e)
    {
        if (_released || e.SourceIdentity != _main.SourceIdentity) return;
        CancelPreview();
        // Only a live accepted-source event may apply full-domain follow. Reattachment always
        // trusts the main plot's latest request, including navigation performed while detached.
        if (!e.ResetView && _main.RequestRevision == e.RequestRevision
            && State.SourceIdentity == e.PreviousIdentity && State.FollowsWhole
            && _main.AcceptedSeries?.Extent is { } extent)
            _main.SetViewWindow(extent);
        RefreshFromMain();
    }
    private void SynchronizeView(object? sender, EventArgs e)
    {
        if (_released) return;
        CancelPreview(); RefreshFromMain();
    }
    private void RefreshFromMain()
    {
        if (_released || _main.SourceIdentity == 0) return;
        var identity = _main.SourceIdentity;
        var next = State.AcceptSource(identity, _main.AcceptedSeries?.Extent);
        if (_main.ViewWindow is { } selected) next = next.Select(selected);
        if (_overviewMainIdentity != identity)
        {
            _overviewMainIdentity = identity;
            _overview.Opacity = 0;
            if (_main.AcceptedSeries is { } source)
            {
                if (_overview.SetSeries(source) != SnapshotPublishStatus.Published) _overview.ReleaseSource();
            }
            else _overview.ReleaseSource();
        }
        Publish(next);
    }
    private void Publish(HistoryRangeCoordinator next)
    {
        if (ReferenceEquals(State, next)) return;
        State = next;
        _overlay.InvalidateVisual();
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool CommitWindow(TimeWindow window, HistoryRangeToken? token = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_released) return false;
        // Detached controls do not receive source/view events. Refresh before checking a caller's
        // token so a formerly valid token can never modify a newer authoritative request.
        RefreshFromMain();
        var next = State.Select(window, token);
        if (ReferenceEquals(next, State)) return false;
        _main.SetViewWindow(next.SelectedWindow!.Value);
        // Observers may replace the source or navigate again; never publish the pre-event token.
        RefreshFromMain();
        return true;
    }
    public void CancelPreview()
    {
        Dispatcher.UIThread.VerifyAccess();
        _dragToken = null; _dragWindow = null; PreviewWindow = null;
        var pointer = _pointer; _pointer = null;
        pointer?.Capture(null);
        _overlay.InvalidateVisual();
    }
    public void ReleaseSource()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_released) return;
        _released = true; CancelPreview(); Unsubscribe(); _overview.ReleaseSource();
        State = HistoryRangeCoordinator.Empty;
        _overlay.InvalidateVisual();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(); RefreshFromMain();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPreview(); Unsubscribe();
        base.OnDetachedFromVisualTree(e);
    }
    private double X(long ticks) => State.Whole is { DurationTicks: > 0 } whole
        ? TrackBounds.Left + (double)((decimal)(ticks - whole.StartTicks) / whole.DurationTicks) * TrackBounds.Width
        : TrackBounds.Left;
    private long Delta(double pixels) => State.Whole is { } whole
        ? (long)Math.Clamp((decimal)(pixels / Math.Max(1, TrackBounds.Width)) * whole.DurationTicks,
            -TimeWindow.MaximumExclusiveTicks, TimeWindow.MaximumExclusiveTicks) : 0;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_released || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        if (State.Whole is not { DurationTicks: > 1 } || State.SelectedWindow is not { } selected) return;
        var point = e.GetPosition(this);
        if (point.Y < TrackBounds.Top - 8 || point.Y > TrackBounds.Bottom + 8) return;
        var left = X(selected.StartTicks); var right = X(selected.EndTicks);
        var distanceLeft = Math.Abs(point.X - left); var distanceRight = Math.Abs(point.X - right);
        if (Math.Min(distanceLeft, distanceRight) <= 10)
            _handle = distanceLeft <= distanceRight ? HistoryRangeHandle.Left : HistoryRangeHandle.Right;
        else if (point.X > left && point.X < right) _handle = HistoryRangeHandle.Window;
        else return;
        _dragToken = State.Token; _dragWindow = selected; PreviewWindow = selected; _dragX = point.X;
        _pointer = e.Pointer; e.Pointer.Capture(this);
        _overlay.InvalidateVisual(); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragToken is not { } token || _dragWindow is not { } original || State.Whole is not { } whole) return;
        if (token != State.Token) { CancelPreview(); return; }
        var delta = Delta(e.GetPosition(this).X - _dragX);
        PreviewWindow = _handle switch
        {
            HistoryRangeHandle.Left => new TimeWindow((long)Math.Clamp((decimal)original.StartTicks + delta,
                whole.StartTicks, Math.Max(whole.StartTicks, original.EndTicks - 1)), original.EndTicks),
            HistoryRangeHandle.Right => new TimeWindow(original.StartTicks, (long)Math.Clamp((decimal)original.EndTicks + delta,
                Math.Min(whole.EndTicks, original.StartTicks + 1), whole.EndTicks)),
            _ => original.Pan(delta, whole)
        };
        _overlay.InvalidateVisual(); e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var token = _dragToken; var preview = PreviewWindow;
        CancelPreview();
        if (token is { } expected && preview is { } window) CommitWindow(window, expected);
        base.OnPointerReleased(e);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { CancelPreview(); base.OnPointerCaptureLost(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { CancelPreview(); e.Handled = true; return; }
        if (State.SelectedWindow is not { } selected || State.Whole is not { } whole || whole.IsEmpty) return;
        CancelPreview();
        var step = Math.Max(1, selected.DurationTicks / 10);
        TimeWindow next;
        switch (e.Key)
        {
            case Key.Home: next = whole; break;
            case Key.Add: case Key.OemPlus: next = selected.Zoom(1.5, selected.StartTicks + selected.DurationTicks / 2, whole); break;
            case Key.Subtract: case Key.OemMinus: next = selected.Zoom(1 / 1.5, selected.StartTicks + selected.DurationTicks / 2, whole); break;
            case Key.Left: case Key.Right:
                var delta = e.Key == Key.Left ? -step : step;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    next = new((long)Math.Clamp((decimal)selected.StartTicks + delta, whole.StartTicks, Math.Max(whole.StartTicks, selected.EndTicks - 1)), selected.EndTicks);
                else if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                    next = new(selected.StartTicks, (long)Math.Clamp((decimal)selected.EndTicks + delta, Math.Min(whole.EndTicks, selected.StartTicks + 1), whole.EndTicks));
                else next = selected.Pan(delta, whole);
                break;
            default: return;
        }
        CommitWindow(next); e.Handled = true;
    }
    private void DrawSelection(DrawingContext context)
    {
        if (IsFocused) context.DrawRectangle(null, new Pen(Palette.Accent, 2), new Rect(Bounds.Size).Deflate(1));
        // Replace the borrowed plot renderer's interaction hint: this overview never pans or selects points.
        context.FillRectangle(Palette.Surface, new Rect(0, Bounds.Height - 28, Bounds.Width, 28));
        DrawingText.Draw(context, State.Whole is null ? "No source range"
            : "Fixed whole-source overview · release handles to apply · Escape cancels",
            new(16, Bounds.Height - 22), Palette.Text, 11, Math.Max(1, Bounds.Width - 32));
        if ((PreviewWindow ?? State.SelectedWindow) is not { } selected) return;
        var left = X(selected.StartTicks); var right = X(selected.EndTicks); var track = TrackBounds;
        var brush = new SolidColorBrush(Color.FromArgb(42, 80, 160, 230));
        context.DrawRectangle(brush, new Pen(Palette.Accent, 2), new Rect(left, track.Top, Math.Max(1, right - left), track.Height));
        foreach (var x in new[] { left, right })
            context.FillRectangle(Palette.Accent, new Rect(x - 3, track.Top - 4, 6, track.Height + 8));
    }
}
