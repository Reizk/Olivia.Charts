using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Olivia.Core.Charts;

namespace Olivia.Avalonia;

/// <summary>Bounded CPU orthographic XYZ points. No provider, timer or GPU engine is retained.</summary>
public sealed class Point3DPlot : Control
{
    private Point3DSource? _source;
    private Point3DCamera _camera = new();
    private UiPalette _palette = UiPalette.Dark;
    private int _budget = 2048;
    private int _selection = -1;
    private bool _attached;
    public Point3DPlot()
    {
        Focusable = true; ClipToBounds = true; MinHeight = 180;
        GotFocus += (_, _) => InvalidateFrame(); LostFocus += (_, _) => InvalidateFrame();
        AutomationProperties.SetName(this, "CPU orthographic XYZ point chart. Left and right select; A and D orbit yaw; W and S orbit pitch; Shift arrows pan; plus and minus zoom; Home resets; Escape clears.");
    }
    public Point3DSnapshot? Snapshot { get; private set; }
    public Point3DCamera Camera => _camera;
    public long FrameRevision { get; private set; }
    public Point3DHit? SelectedHit => Snapshot is { } s && _selection >= 0 && _selection < s.Points.Length ? s.Points[_selection].Hit : null;
    public UiPalette Palette
    {
        get => _palette;
        set { Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(value); _palette = value; InvalidateFrame(); }
    }
    public Rect PlotArea => double.IsFinite(Bounds.Width) && double.IsFinite(Bounds.Height)
        ? new(20, 65, Math.Max(0, Bounds.Width - 40), Math.Max(0, Bounds.Height - 130)) : new(20, 65, 0, 0);
    public bool IsPlotAvailable => PlotArea.Width is >= 1 and <= 8192 && PlotArea.Height is >= 1 and <= 8192 && Bounds.Width >= 180 && Bounds.Height >= 180;
    public string Status => Snapshot is not { } s ? "No source" : s.BudgetStatus == ChartBudgetStatus.InsufficientBudget
        ? $"Budget exceeded: need {s.RequiredPointCount} points; no partial geometry shown"
        : $"{s.Source.Id} · version {s.Source.Version} · {s.Points.Length} points · {s.Gaps.Length} gaps · {s.ClippedCount} clipped";
    public string CameraDescription => FormattableString.Invariant($"Yaw {_camera.Yaw:G4} · pitch {_camera.Pitch:G4} · target ({_camera.Target.X:G4}, {_camera.Target.Y:G4}, {_camera.Target.Z:G4}) · half-height {_camera.HalfHeight:G4} · distance {_camera.Distance:G4} · near/far {_camera.Near:G4}/{_camera.Far:G4}");
    public string AccessibleSummary => SelectedHit is { } h
        ? $"{Status}. {h.SourceId}; version {h.Version}; key {h.Key}; X {h.X?.ToString("R", CultureInfo.InvariantCulture)}; Y {h.Y?.ToString("R", CultureInfo.InvariantCulture)}; Z {h.Z?.ToString("R", CultureInfo.InvariantCulture)}. {CameraDescription}"
        : $"{Status}. {CameraDescription}";
    public event EventHandler? StateChanged;
    private Point3DViewport Viewport() => IsPlotAvailable ? new(PlotArea.Width, PlotArea.Height) : new(0, 0);
    public void SetSource(Point3DSource source)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(source);
        var snapshot = source.Query(_camera, Viewport(), _budget); _source = source; Snapshot = snapshot; InvalidateFrame();
    }
    public void SetCamera(Point3DCamera camera)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(camera);
        var snapshot = _source?.Query(camera, Viewport(), _budget); _camera = camera; Snapshot = snapshot; InvalidateFrame();
    }
    public void SetGeometryBudget(int geometryBudget)
    {
        Dispatcher.UIThread.VerifyAccess(); if (geometryBudget < 0 || geometryBudget > 2048) throw new ArgumentOutOfRangeException(nameof(geometryBudget));
        var snapshot = _source?.Query(_camera, Viewport(), geometryBudget); _budget = geometryBudget; Snapshot = snapshot; InvalidateFrame();
    }
    public void ReleaseSource() { Dispatcher.UIThread.VerifyAccess(); _source = null; Snapshot = null; InvalidateFrame(); }
    public void ResetCamera() => SetCamera(new());
    public void Orbit(double yawDelta, double pitchDelta)
    {
        Dispatcher.UIThread.VerifyAccess(); CheckFinite(yawDelta, nameof(yawDelta)); CheckFinite(pitchDelta, nameof(pitchDelta));
        SetCamera(Copy(Math.Clamp(_camera.Yaw + Math.Clamp(yawDelta, -2 * Math.PI, 2 * Math.PI), -Math.PI, Math.PI), Math.Clamp(_camera.Pitch + Math.Clamp(pitchDelta, -Math.PI, Math.PI), -Math.PI / 2, Math.PI / 2), _camera.Target, _camera.HalfHeight));
    }
    /// <summary>Moves target along camera right/up; dx/dy are normalized world distances.</summary>
    public void Pan(double dx, double dy)
    {
        Dispatcher.UIThread.VerifyAccess(); CheckFinite(dx, nameof(dx)); CheckFinite(dy, nameof(dy));
        dx = Math.Clamp(dx, -16, 16); dy = Math.Clamp(dy, -16, 16);
        var cy = Math.Cos(_camera.Yaw); var sy = Math.Sin(_camera.Yaw); var sp = Math.Sin(_camera.Pitch); var cp = Math.Cos(_camera.Pitch);
        var t = _camera.Target;
        SetCamera(Copy(_camera.Yaw, _camera.Pitch, new(Math.Clamp(t.X + dx * cy - dy * sy * sp, -4, 4), Math.Clamp(t.Y + dy * cp, -4, 4), Math.Clamp(t.Z - dx * sy - dy * cy * sp, -4, 4)), _camera.HalfHeight));
    }
    public void Zoom(double factor)
    {
        Dispatcher.UIThread.VerifyAccess(); CheckFinite(factor, nameof(factor)); if (factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        SetCamera(Copy(_camera.Yaw, _camera.Pitch, _camera.Target, Math.Clamp(_camera.HalfHeight / factor, .01, 16)));
    }
    private Point3DCamera Copy(double yaw, double pitch, Point3DVector target, double height) => new(yaw, pitch, target, height, _camera.Distance, _camera.Near, _camera.Far);
    private static void CheckFinite(double value, string name) { if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(name); }
    private void Requery() { Snapshot = _source?.Query(_camera, Viewport(), _budget); InvalidateFrame(); }
    private void InvalidateFrame() { FrameRevision++; _selection = -1; Changed(); }
    private void Changed()
    {
        AutomationProperties.SetHelpText(this, AccessibleSummary); ToolTip.SetTip(this, SelectedHit is null ? null : AccessibleSummary);
        InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty);
    }
    public bool SelectAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || !IsPlotAvailable || !PlotArea.Contains(point) || Snapshot is not { } s) return false;
        var hit = Point3DChartGeometry.HitTest(s, point.X - PlotArea.X, point.Y - PlotArea.Y, 5);
        _selection = -1;
        if (hit is { } identity)
            for (var i = 0; i < s.Points.Length; i++)
                if (s.Points[i].Hit.Equals(identity)) { _selection = i; break; }
        Changed();
        return hit is not null && _attached && expectedRevision == FrameRevision && ReferenceEquals(s, Snapshot) && SelectedHit == hit;
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) { base.OnPropertyChanged(change); if (change.Property == BoundsProperty) Requery(); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _attached = true; Requery(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _attached = false; InvalidateFrame(); base.OnDetachedFromVisualTree(e); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_attached || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsPlotAvailable || !PlotArea.Contains(e.GetPosition(this))) return;
        var snapshot = Snapshot; Focus();
        if (ReferenceEquals(snapshot, Snapshot)) SelectAt(e.GetPosition(this), FrameRevision);
        e.Handled = true;
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!_attached || !IsPlotAvailable || !PlotArea.Contains(e.GetPosition(this)) || !double.IsFinite(e.Delta.Y) || e.Delta.Y == 0) return;
        Zoom(e.Delta.Y > 0 ? 1.25 : .8); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (!_attached) return;
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0 && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            Pan(e.Key == Key.Left ? -.1 : e.Key == Key.Right ? .1 : 0, e.Key == Key.Down ? -.1 : e.Key == Key.Up ? .1 : 0); e.Handled = true; return;
        }
        switch (e.Key)
        {
            case Key.Left: case Key.Right:
                if (IsPlotAvailable && Snapshot is { Points.Length: > 0 } s) { _selection = _selection < 0 ? (e.Key == Key.Right ? 0 : s.Points.Length - 1) : Math.Clamp(_selection + (e.Key == Key.Right ? 1 : -1), 0, s.Points.Length - 1); Changed(); } break;
            case Key.A: Orbit(-.15, 0); break; case Key.D: Orbit(.15, 0); break;
            case Key.W: Orbit(0, .15); break; case Key.S: Orbit(0, -.15); break;
            case Key.Add: case Key.OemPlus: Zoom(1.25); break;
            case Key.Subtract: case Key.OemMinus: Zoom(.8); break;
            case Key.Home: ResetCamera(); break;
            case Key.Escape: _selection = -1; Changed(); break;
            default: return;
        }
        e.Handled = true;
    }
    // Stable exact colors make painter order observable independently of the theme.
    public static IBrush PointBrush(int sourceIndex) => (sourceIndex % 4) switch { 0 => Brushes.CornflowerBlue, 1 => Brushes.Orange, 2 => Brushes.LimeGreen, _ => Brushes.Orchid };
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Palette.Surface, new Rect(Bounds.Size));
        if (IsFocused) context.DrawRectangle(null, new Pen(Palette.Accent, 2), new Rect(Bounds.Size).Deflate(1));
        DrawingText.Draw(context, "CPU orthographic 3D point data · X right, Y up, Z towards eye at reset", new(10, 8), Palette.Text, 12, Math.Max(1, Bounds.Width - 20));
        DrawingText.Draw(context, "Uniform XYZ normalization · far → near paint · equal depth: source order", new(10, 30), Palette.Muted, 11, Math.Max(1, Bounds.Width - 20));
        var area = PlotArea;
        if (IsPlotAvailable && Snapshot is { } s)
        {
            context.DrawRectangle(null, new Pen(Palette.GridLine, 1), area);
            using (context.PushClip(area))
            {
                foreach (var p in s.Points) context.DrawEllipse(PointBrush(p.SourceIndex), null, new(area.X + p.X, area.Y + p.Y), 5, 5);
                // Accessibility overlay only: this ring is not a depth-tested data glyph or hit target.
                if (_selection >= 0 && _selection < s.Points.Length)
                {
                    var p = s.Points[_selection]; context.DrawEllipse(null, new Pen(Palette.Text, 2), new(area.X + p.X, area.Y + p.Y), 8, 8);
                }
            }
        }
        DrawingText.Draw(context, IsPlotAvailable ? Status : "Resize chart: control ≥180 × 180; drawable viewport 1–8192 per dimension", new(10, Math.Max(0, Bounds.Height - 43)), Palette.Muted, 11, Math.Max(1, Bounds.Width - 20));
        DrawingText.Draw(context, SelectedHit is null ? CameraDescription : AccessibleSummary, new(10, Math.Max(0, Bounds.Height - 23)), Palette.Text, 10, Math.Max(1, Bounds.Width - 20));
    }
}
