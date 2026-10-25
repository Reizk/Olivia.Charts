using System.Globalization;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Olivia.Core.Charts;
using Olivia.Avalonia.Themes;

namespace Olivia.Avalonia;

/// <summary>Measured, unclipped numeric label in its displayed raster's local pixel coordinates.</summary>
public readonly record struct SurfaceAxisLabel(SurfaceAxisDimension Dimension, double Value, string Text, Rect Bounds);

/// <summary>
/// Bounded background CPU surface adapter. The immutable raster and its depth/identity buffer
/// describe the same triangles. Prepared and actually drawn frames are deliberately separate.
/// Detach releases the bitmap and rasters but retains the immutable source and camera.
/// </summary>
public sealed class SurfacePlot : Control
{
    public static readonly StyledProperty<OliviaChartTheme?> ChartThemeProperty =
        AvaloniaProperty.Register<SurfacePlot, OliviaChartTheme?>(nameof(ChartTheme));
    public static readonly StyledProperty<ISolidColorBrush?> DomainBrushProperty =
        AvaloniaProperty.Register<SurfacePlot, ISolidColorBrush?>(nameof(DomainBrush));
    public static readonly StyledProperty<Color?> DomainGridColorProperty =
        AvaloniaProperty.Register<SurfacePlot, Color?>(nameof(DomainGridColor));
    public static readonly StyledProperty<UiPalette> PaletteProperty =
        AvaloniaProperty.Register<SurfacePlot, UiPalette>(nameof(Palette), UiPalette.Dark);
    private SurfaceChartSource? _source;
    private Point3DCamera _camera = DefaultCamera();
    private bool _fitOnNextFrame = true;
    private ImmutableArray<SurfaceAxisLabel> _preparedLabels = [];
    private ImmutableArray<(SurfaceAxisDimension? Dimension, SurfaceAxisReferencePlane? Plane, StreamGeometry Geometry)> _axisDrawings = [];
    private SurfaceRenderingOptions _renderingOptions = SurfaceRenderingOptions.LitPerspective with { EdgeAntialiasing = true };
    private WriteableBitmap? _bitmap;
    private double _minimumValue = -18.5, _maximumValue = 18.5;
    private bool _attached, _showLegend = true, _showProjection;
    private SurfaceProjectionPlacement _projectionPlacement;
    private Point? _selectionPoint, _dragPoint;
    private IPointer? _dragPointer;
    private bool _dragMoved;
    private long _pressRevision, _interactionVersion;
    private long _displayNotificationVersion;
    private long _preparedRevision;
    private RenderRequest? _activeRequest, _pendingRequest;
    private CancellationTokenSource? _renderCancellation;
    private Task _renderingCompletion = Task.CompletedTask;
    private sealed record RenderRequest(long Revision, SurfaceChartSource Source, Point3DCamera Camera,
        int Width, int Height, double Minimum, double Maximum, bool Projection, SurfaceProjectionPlacement Placement,
        SurfaceRenderingOptions Options);
    private InputElement? _inputRoot;
    private Window? _owner;
    private sealed record InputFrame(long Revision, long InteractionVersion);
    private readonly ConditionalWeakTable<RoutedEventArgs, InputFrame> _inputFrames = new();
    private LinearGradientBrush? _legendBrush;
    private (double Value, double Offset, IBrush Brush)[] _legendStops = [];
    private SurfaceMaterial? _legendMaterial;
    private double _legendMinimum = double.NaN, _legendMaximum = double.NaN;
    private AvaloniaObject? _observedDomainBrush;

    static SurfacePlot()
    {
        // Stamp the frame before root instance tunnel callbacks can replace it.
        // Class handlers retain no plot; event-keyed weak tables isolate nested input.
        PointerPressedEvent.AddClassHandler<TopLevel>((root, e) => CaptureRootInput(root, e),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerReleasedEvent.AddClassHandler<TopLevel>((root, e) => CaptureRootInput(root, e),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerMovedEvent.AddClassHandler<TopLevel>((root, e) => CaptureRootInput(root, e),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerWheelChangedEvent.AddClassHandler<TopLevel>((root, e) => CaptureRootInput(root, e),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        KeyDownEvent.AddClassHandler<TopLevel>((root, e) => CaptureRootInput(root, e),
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void CaptureRootInput(TopLevel root, RoutedEventArgs e)
    {
        if (e.Source is SurfacePlot plot && plot._attached && ReferenceEquals(plot._inputRoot, root))
            plot.CaptureInput(e);
    }

    public SurfacePlot()
    {
        Focusable = true;
        ClipToBounds = true;
        // The raster is in logical pixels; interpolate when the desktop compositor scales
        // it at high DPI or when retaining the previous frame during a resize.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.LowQuality);
        MinHeight = 180;
        AutomationProperties.SetName(this,
            "Filled CPU surface. Left drag rotates, arrows pan, wheel or plus and minus zoom, Home resets, click selects the displayed triangle.");
    }

    public SurfaceChartSource? Source => _source;
    public Point3DCamera Camera => _camera;
    public SurfaceRenderingOptions RenderingOptions => _renderingOptions;
    public double HeightSideMarginFraction
    {
        get => _renderingOptions.HeightSideMarginFraction;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (value == HeightSideMarginFraction) return;
            var options = _renderingOptions with { HeightSideMarginFraction = value };
            options.HeightRange(_minimumValue, _maximumValue);
            _renderingOptions = options;
            _fitOnNextFrame = true;
            EndDrag(); PrepareFrame();
        }
    }
    public bool PerspectiveEnabled
    {
        get => _renderingOptions.Perspective;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (value == PerspectiveEnabled) return;
            _renderingOptions = _renderingOptions with { Perspective = value };
            EndDrag(); PrepareFrame();
        }
    }
    public bool LightingEnabled
    {
        get => _renderingOptions.Lighting;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (value == LightingEnabled) return;
            _renderingOptions = _renderingOptions with { Lighting = value };
            EndDrag(); PrepareFrame();
        }
    }
    public SurfaceRasterSnapshot? Snapshot { get; private set; }
    public SurfaceRasterSnapshot? DisplayedSnapshot { get; private set; }
    public SurfaceAxisReferenceSnapshot? AxisReference { get; private set; }
    public SurfaceAxisReferenceSnapshot? DisplayedAxisReference { get; private set; }
    public ImmutableArray<SurfaceAxisLabel> DisplayedAxisLabels { get; private set; } = [];
    /// <summary>At most seven retained draw batches (three lit grid faces and four edge groups).</summary>
    public int PreparedAxisDrawBatchCount => _axisDrawings.Length;
    public long FrameRevision { get; private set; }
    public long DisplayedFrameRevision { get; private set; }
    public SurfaceRasterHit? SelectedHit { get; private set; }
    public bool HasBitmap => _bitmap is not null;
    public bool IsAttached => _attached;
    /// <summary>At most one worker and one replaceable pending request. UI-thread access only.</summary>
    public bool IsRendering => _activeRequest is not null || _pendingRequest is not null;
    public long PreparedFrameRevision => _preparedRevision;
    /// <summary>After ReleaseSource/detach, joins the cancelled worker without blocking the UI.</summary>
    public Task RenderingCompletion => _renderingCompletion;
    public string? RasterError { get; private set; }
    public double MinimumValue => _minimumValue;
    public double MaximumValue => _maximumValue;
    public event EventHandler? StateChanged;

    public UiPalette Palette
    {
        get => GetValue(PaletteProperty);
        set { ArgumentNullException.ThrowIfNull(value); SetValue(PaletteProperty, value); }
    }

    /// <summary>Optional immutable data palette. Null keeps legacy paint and no domain planes.</summary>
    public OliviaChartTheme? ChartTheme
    { get => GetValue(ChartThemeProperty); set => SetValue(ChartThemeProperty, value); }
    /// <summary>Optional scoped solid-brush override of the theme's domain material.</summary>
    public ISolidColorBrush? DomainBrush
    { get => GetValue(DomainBrushProperty); set => SetValue(DomainBrushProperty, value); }
    /// <summary>Optional unlit grid paint; each far-face batch applies its frame's diffuse light.</summary>
    public Color? DomainGridColor
    { get => GetValue(DomainGridColorProperty); set => SetValue(DomainGridColorProperty, value); }

    private void RefreshDomainBrushSubscription()
    {
        var eligible = _attached && InheritanceParent is not null;
        var brush = eligible ? DomainBrush as AvaloniaObject : null;
        if (ReferenceEquals(_observedDomainBrush, brush)) return;
        if (_observedDomainBrush is not null)
        {
            _observedDomainBrush.PropertyChanged -= OnDomainBrushPropertyChanged;
        }
        _observedDomainBrush = brush;
        if (_observedDomainBrush is not null) _observedDomainBrush.PropertyChanged += OnDomainBrushPropertyChanged;
    }

    private void UpdateMaterial(bool scheduleFrame = true)
    {
        RefreshDomainBrushSubscription();
        SurfaceColor Convert(Color color, double opacity = 1) =>
            new(color.R, color.G, color.B, (byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1)));
        SurfaceMaterial? material = null;
        if (ChartTheme is { } theme)
        {
            var domain = DomainBrush ?? theme.DomainBrush;
            material = new(Convert(theme.SeriesColors[0]), Convert(theme.SeriesColors[1]),
                Convert(theme.SeriesColors[2]), Convert(domain.Color, domain.Opacity));
        }
        if (Equals(material, _renderingOptions.Material)) return;
        _renderingOptions = _renderingOptions with { Material = material };
        // Inheritance is removed before Parent/attachment flags change. Capture the fallback
        // paint without publishing a transient frame in that nested resource notification.
        if (!scheduleFrame || !_attached || InheritanceParent is null) return;
        EndDrag(); PrepareFrame(preserveSelection: true);
    }

    private void OnDomainBrushPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e) => UpdateMaterial();

    public bool ShowLegend
    {
        get => _showLegend;
        set { Dispatcher.UIThread.VerifyAccess(); if (_showLegend == value) return; _showLegend = value; Changed(); }
    }

    public bool ShowProjection
    {
        get => _showProjection;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (_showProjection == value) return;
            _showProjection = value; EndDrag(); PrepareFrame();
        }
    }
    public SurfaceProjectionPlacement ProjectionPlacement
    {
        get => _projectionPlacement;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (_projectionPlacement == value) return;
            _projectionPlacement = value; EndDrag(); PrepareFrame();
        }
    }

    public Rect PlotArea => double.IsFinite(Bounds.Width) && double.IsFinite(Bounds.Height)
        ? new(12, 32, Math.Max(0, Math.Floor(Bounds.Width - 140)), Math.Max(0, Math.Floor(Bounds.Height - 92)))
        : new(12, 32, 0, 0);

    public bool IsPlotAvailable
    {
        get
        {
            var area = PlotArea;
            return Bounds.Width >= 180 && Bounds.Height >= 180 && area.Width is >= 1 and <= 2048 &&
                area.Height is >= 1 and <= 2048 && area.Width * area.Height <= 1_048_576;
        }
    }

    public string Status => RasterError ?? (_source is not { } source ? "No source" :
        $"{source.XCount} × {source.YCount} original vertices · source v{source.Version} · " +
        (_preparedRevision == FrameRevision && Snapshot is { } raster
            ? $"{raster.Triangles.Length.ToString(CultureInfo.InvariantCulture)} triangles" : "preparing frame"));
    public string CameraDescription => FormattableString.Invariant(
        $"Yaw {_camera.Yaw:0.00} · pitch {_camera.Pitch:0.00} · target ({_camera.Target.X:0.00}, {_camera.Target.Y:0.00}, {_camera.Target.Z:0.00}) · half-height {_camera.HalfHeight:0.00}");
    public string SelectionDescription => SelectedHit is { } hit
        ? FormattableString.Invariant($"{hit.SourceId} v{hit.Version} · triangle {hit.TriangleIndex} · original keys {hit.A.Key}, {hit.B.Key}, {hit.C.Key} · interpolated X {hit.InterpolatedX:G6}, Y {hit.InterpolatedY:G6}, Z {hit.InterpolatedValue:G6}")
        : "Click a displayed surface pixel to inspect its original triangle and interpolated value. Projection and axis-reference pixels are not source samples.";
    public string DescribeAxisRange(SurfaceAxisDimension dimension)
    {
        if (!Enum.IsDefined(dimension)) throw new ArgumentOutOfRangeException(nameof(dimension));
        var title = dimension == SurfaceAxisDimension.Z ? "Z height" : $"{dimension} arguments";
        // A newer prepared frame is not on screen yet. Retain the last displayed domain
        // until Render atomically advances its owner; do not flicker labels every timer tick.
        if (DisplayedAxisReference is not { } axes || !ReferenceEquals(axes.Raster, DisplayedSnapshot))
            return $"{title}: awaiting displayed frame";
        var axis = axes.Axes.Single(item => item.Dimension == dimension);
        return FormattableString.Invariant($"{title} [{axis.Minimum:G6}, {axis.Maximum:G6}] · source v{axes.Raster.Source.Version}");
    }
    public string AccessibleSummary => $"{Status}. {SelectionDescription} {CameraDescription}. " +
        $"{DescribeAxisRange(SurfaceAxisDimension.X)}; {DescribeAxisRange(SurfaceAxisDimension.Y)}; {DescribeAxisRange(SurfaceAxisDimension.Z)}";

    public void SetSource(SurfaceChartSource source, double minimumValue, double maximumValue)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(source);
        CheckFinite(minimumValue, nameof(minimumValue)); CheckFinite(maximumValue, nameof(maximumValue));
        if (minimumValue >= maximumValue) throw new ArgumentOutOfRangeException(nameof(maximumValue));
        _renderingOptions.HeightRange(minimumValue, maximumValue);
        if (ReferenceEquals(source, _source) && minimumValue == _minimumValue && maximumValue == _maximumValue) return;
        _source = source; _minimumValue = minimumValue; _maximumValue = maximumValue;
        PrepareFrame();
    }

    public void SetCamera(Point3DCamera camera)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(camera);
        _fitOnNextFrame = false; _camera = camera;
        PrepareFrame();
    }

    public void ResetCamera()
    {
        Dispatcher.UIThread.VerifyAccess();
        _fitOnNextFrame = true; _camera = DefaultCamera(); PrepareFrame();
    }
    // Reference default model rotation (-40 degrees about Y, then +20 about X),
    // expressed as the inverse camera basis. Target-plane extent gives a 45-degree vertical FOV.
    private static Point3DCamera DefaultCamera() => new(yaw: 40 * Math.PI / 180, pitch: 20 * Math.PI / 180,
        halfHeight: 4 * Math.Tan(Math.PI / 8));
    public void Orbit(double yawDelta, double pitchDelta)
    {
        Dispatcher.UIThread.VerifyAccess(); CheckFinite(yawDelta, nameof(yawDelta)); CheckFinite(pitchDelta, nameof(pitchDelta));
        SetCamera(Copy(Math.Clamp(_camera.Yaw + Math.Clamp(yawDelta, -2 * Math.PI, 2 * Math.PI), -Math.PI, Math.PI),
            Math.Clamp(_camera.Pitch + Math.Clamp(pitchDelta, -Math.PI, Math.PI), -Math.PI / 2, Math.PI / 2), _camera.Target, _camera.HalfHeight));
    }

    /// <summary>Pan in the camera's right/up plane; distances are normalized world units.</summary>
    public void Pan(double dx, double dy)
    {
        Dispatcher.UIThread.VerifyAccess(); CheckFinite(dx, nameof(dx)); CheckFinite(dy, nameof(dy));
        dx = Math.Clamp(dx, -16, 16); dy = Math.Clamp(dy, -16, 16);
        var cy = Math.Cos(_camera.Yaw); var sy = Math.Sin(_camera.Yaw);
        var sp = Math.Sin(_camera.Pitch); var cp = Math.Cos(_camera.Pitch); var target = _camera.Target;
        SetCamera(Copy(_camera.Yaw, _camera.Pitch, new(
            Math.Clamp(target.X + dx * cy - dy * sy * sp, -4, 4),
            Math.Clamp(target.Y + dy * cp, -4, 4),
            Math.Clamp(target.Z - dx * sy - dy * cy * sp, -4, 4)), _camera.HalfHeight));
    }

    public void Zoom(double factor)
    {
        Dispatcher.UIThread.VerifyAccess(); CheckFinite(factor, nameof(factor));
        if (factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        SetCamera(Copy(_camera.Yaw, _camera.Pitch, _camera.Target, Math.Clamp(_camera.HalfHeight / factor, .01, 16)));
    }

    private Point3DCamera Copy(double yaw, double pitch, Point3DVector target, double halfHeight) =>
        new(yaw, pitch, target, halfHeight, _camera.Distance, _camera.Near, _camera.Far);
    private static void CheckFinite(double value, string parameter)
    { if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(parameter); }

    public void ReleaseSource()
    {
        Dispatcher.UIThread.VerifyAccess();
        _source = null;
        ++FrameRevision;
        EndDrag();
        ReleaseRaster();
        RasterError = null;
        Changed();
    }

    private void PrepareFrame(bool preserveSelection = false)
    {
        ++FrameRevision;
        if (!preserveSelection) { SelectedHit = null; _selectionPoint = null; }
        RasterError = null;
        // Keep the last immutable frame visible while its replacement is computed.
        // Its own revision remains unchanged, so old pixels cannot select new data.
        _pendingRequest = null;
        _renderCancellation?.Cancel();
        if (!_attached || _source is null)
        {
            ReleaseRaster();
        }
        else if (!IsPlotAvailable)
        {
            ReleaseRaster();
            RasterError = "Resize chart: drawable raster must be 1–2048 px per side and at most 1,048,576 pixels. No downsampling is applied.";
        }
        else
        {
            var area = PlotArea;
            if (_fitOnNextFrame)
            {
                var heightRange = _renderingOptions.HeightRange(_minimumValue, _maximumValue);
                _camera = SurfaceCameraLayout.Create(_source, heightRange.Minimum, heightRange.Maximum,
                    area.Width, area.Height, PerspectiveEnabled);
                _fitOnNextFrame = false;
            }
            _pendingRequest = new(FrameRevision, _source, _camera, (int)area.Width, (int)area.Height,
                _minimumValue, _maximumValue, _showProjection, _projectionPlacement, _renderingOptions);
            StartPendingFrame();
        }
        Changed();
    }

    private void StartPendingFrame()
    {
        if (_activeRequest is not null || _pendingRequest is not { } request) return;
        _pendingRequest = null;
        _activeRequest = request;
        _renderCancellation = new();
        // The worker owns only immutable inputs, never the control or its native bitmap.
        _renderingCompletion = RenderInBackground(new(this), request, _renderCancellation.Token);
    }

    private static Task RenderInBackground(WeakReference<SurfacePlot> owner, RenderRequest request, CancellationToken token) =>
        Task.Run(() =>
        {
            SurfaceRasterSnapshot? raster = null;
            SurfaceAxisReferenceSnapshot? axes = null;
            string? error = null;
            try
            {
                raster = SurfaceRasterizer.Render(request.Source, request.Camera, request.Width, request.Height,
                    request.Minimum, request.Maximum, request.Projection, request.Placement, request.Options, token);
                token.ThrowIfCancellationRequested();
                axes = SurfaceAxisReference.Build(raster, includeGraduations: true, token);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { error = $"Surface rendering failed: {exception.Message}"; }
            if (token.IsCancellationRequested) { raster = null; axes = null; }
            Dispatcher.UIThread.Post(() =>
            {
                if (owner.TryGetTarget(out var plot)) plot.CompleteFrame(request, raster, axes, error);
            }, DispatcherPriority.Normal);
        });

    private void CompleteFrame(RenderRequest request, SurfaceRasterSnapshot? raster,
        SurfaceAxisReferenceSnapshot? axes, string? error)
    {
        if (!ReferenceEquals(request, _activeRequest)) return;
        _activeRequest = null;
        _renderCancellation?.Dispose(); _renderCancellation = null;
        var current = _attached && request.Revision == FrameRevision && ReferenceEquals(request.Source, _source);
        if (current)
        {
            RasterError = error;
            if (raster is not null && axes is not null && error is null)
            {
                UpdateBitmap(raster);
                _preparedLabels = BuildLabels(axes);
                _axisDrawings = BuildAxisDrawings(axes);
                Snapshot = raster; AxisReference = axes; _preparedRevision = request.Revision;
                if (_selectionPoint is { } point)
                {
                    // Recoloring keeps a source selection only if that same pixel is still
                    // selectable. A newly opaque plane must not leave a pick-through marker.
                    var x = (int)Math.Floor(point.X - PlotArea.X); var y = (int)Math.Floor(point.Y - PlotArea.Y);
                    SelectedHit = axes.ContainsPixel(x, y) || _preparedLabels.Any(label =>
                        label.Bounds.Contains(new Point(x + .5, y + .5))) ? null : raster.HitTest(x, y);
                    if (SelectedHit is null) _selectionPoint = null;
                }
            }
        }
        // Start the one latest request before external observers can reenter the control.
        StartPendingFrame();
        if (current) Changed();
    }

    private void UpdateBitmap(SurfaceRasterSnapshot snapshot)
    {
        if (_bitmap is null || _bitmap.PixelSize.Width != snapshot.Width || _bitmap.PixelSize.Height != snapshot.Height)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            _bitmap = new WriteableBitmap(new PixelSize(snapshot.Width, snapshot.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        }
        // The array is borrowed read-only, not copied or exposed. Native stride can exceed
        // width*4, so transfer each row into the locked framebuffer using its actual RowBytes.
        var pixels = ImmutableCollectionsMarshal.AsArray(snapshot.Pixels)!;
        using var framebuffer = _bitmap.Lock();
        var rowBytes = checked(snapshot.Width * 4);
        for (var row = 0; row < snapshot.Height; row++)
            Marshal.Copy(pixels, row * rowBytes, IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes), rowBytes);
    }

    private void ReleaseRaster()
    {
        _pendingRequest = null;
        _renderCancellation?.Cancel();
        _preparedRevision = 0;
        _bitmap?.Dispose(); _bitmap = null;
        Snapshot = null; DisplayedSnapshot = null; AxisReference = null; DisplayedAxisReference = null; DisplayedFrameRevision = 0;
        _preparedLabels = []; DisplayedAxisLabels = [];
        _axisDrawings = [];
        SelectedHit = null; _selectionPoint = null;
    }

    public bool SelectAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || expectedRevision != DisplayedFrameRevision ||
            !IsPlotAvailable || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !PlotArea.Contains(point) ||
            DisplayedSnapshot is not { } displayed || !ReferenceEquals(displayed, Snapshot) ||
            !ReferenceEquals(displayed.Source, _source)) return false;
        var area = PlotArea;
        var x = (int)Math.Floor(point.X - area.X); var y = (int)Math.Floor(point.Y - area.Y);
        if (DisplayedAxisReference is not { } axes || !ReferenceEquals(axes, AxisReference) ||
            !ReferenceEquals(axes.Raster, displayed)) return false;
        var hit = axes.ContainsPixel(x, y) || DisplayedAxisLabels.Any(label => label.Bounds.Contains(new Point(x + .5, y + .5)))
            ? null : displayed.HitTest(x, y);
        SelectedHit = hit; _selectionPoint = hit is null ? null : point;
        Changed();
        return hit is not null && _attached && FrameRevision == expectedRevision &&
            ReferenceEquals(displayed, DisplayedSnapshot) && ReferenceEquals(displayed, Snapshot) && SelectedHit == hit;
    }

    private void Changed()
    {
        // A synchronous state update supersedes any pending render-completion notice.
        ++_displayNotificationVersion;
        InvalidateVisual();
        var revision = FrameRevision;
        AutomationProperties.SetHelpText(this, AccessibleSummary);
        if (revision != FrameRevision) return;
        ToolTip.SetTip(this, SelectedHit is null ? null : SelectionDescription);
        if (revision == FrameRevision) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChartThemeProperty || change.Property == DomainBrushProperty) UpdateMaterial();
        else if ((change.Property == PaletteProperty || change.Property == DomainGridColorProperty) && _attached && InheritanceParent is not null) Changed();
        if (change.Property == BoundsProperty) PrepareFrame();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = true;
        _inputRoot = TopLevel.GetTopLevel(this);
        _owner = _inputRoot as Window;
        if (_owner is not null) _owner.Deactivated += OwnerDeactivated;
        base.OnAttachedToVisualTree(e);
        // Rebind and recapture the current brush. The brush may have changed while detached.
        UpdateMaterial(scheduleFrame: false);
        PrepareFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_observedDomainBrush is not null)
        {
            _observedDomainBrush.PropertyChanged -= OnDomainBrushPropertyChanged;
            _observedDomainBrush = null;
        }
        _attached = false;
        if (_owner is not null) _owner.Deactivated -= OwnerDeactivated;
        _owner = null; _inputRoot = null;
        _inputFrames.Clear();
        EndDrag();
        ++FrameRevision;
        ReleaseRaster();
        Changed();
        base.OnDetachedFromVisualTree(e);
    }

    private void CaptureInput(RoutedEventArgs e)
    {
        if (ReferenceEquals(e.Source, this)) _inputFrames.GetValue(e, _ => new(FrameRevision, _interactionVersion));
    }
    private InputFrame? TakeInput(RoutedEventArgs e)
    { _inputFrames.TryGetValue(e, out var frame); _inputFrames.Remove(e); return frame; }
    private bool IsCurrent(InputFrame? frame) => frame is not null && _attached && _source is not null &&
        frame.Revision == FrameRevision && frame.InteractionVersion == _interactionVersion;
    private void OwnerDeactivated(object? sender, EventArgs e) => EndDrag();
    private void EndDrag()
    {
        ++_interactionVersion;
        var pointer = _dragPointer;
        _dragPointer = null; _dragPoint = null; _dragMoved = false;
        if (pointer?.Captured == this) pointer.Capture(null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var frame = TakeInput(e);
        base.OnPointerPressed(e);
        if (!IsCurrent(frame) || !IsPlotAvailable || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            !PlotArea.Contains(e.GetPosition(this))) return;
        e.Handled = true;
        Focus();
        if (!IsCurrent(frame)) return;
        ++_interactionVersion;
        _dragPointer = e.Pointer; _dragPoint = e.GetPosition(this); _dragMoved = false;
        _pressRevision = frame!.Revision;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var frame = TakeInput(e);
        base.OnPointerMoved(e);
        if (!IsCurrent(frame) || !ReferenceEquals(e.Pointer, _dragPointer) || _dragPoint is not { } previous || !IsPlotAvailable) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { EndDrag(); return; }
        var point = e.GetPosition(this);
        var dx = point.X - previous.X; var dy = point.Y - previous.Y;
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 3)) return;
        _dragPoint = point; _dragMoved = true;
        e.Handled = true;
        Orbit(dx * .008, -dy * .008);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var frame = TakeInput(e);
        base.OnPointerReleased(e);
        if (!ReferenceEquals(e.Pointer, _dragPointer)) return;
        if (!IsCurrent(frame)) { EndDrag(); return; }
        var select = !_dragMoved; var revision = _pressRevision; var point = e.GetPosition(this);
        EndDrag();
        e.Handled = true;
        if (select) SelectAt(point, revision);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(e.Pointer, _dragPointer)) EndDrag();
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var frame = TakeInput(e);
        base.OnPointerWheelChanged(e);
        if (!IsCurrent(frame) || !IsPlotAvailable || !PlotArea.Contains(e.GetPosition(this)) ||
            !double.IsFinite(e.Delta.Y) || e.Delta.Y == 0) return;
        e.Handled = true;
        Zoom(e.Delta.Y > 0 ? 1.25 : .8);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var frame = TakeInput(e);
        base.OnKeyDown(e);
        if (!IsCurrent(frame)) return;
        switch (e.Key)
        {
            case Key.Left: Pan(-.1, 0); break;
            case Key.Right: Pan(.1, 0); break;
            case Key.Up: Pan(0, .1); break;
            case Key.Down: Pan(0, -.1); break;
            case Key.Add: case Key.OemPlus: Zoom(1.25); break;
            case Key.Subtract: case Key.OemMinus: Zoom(.8); break;
            case Key.Home: ResetCamera(); break;
            case Key.Escape: EndDrag(); SelectedHit = null; _selectionPoint = null; Changed(); break;
            default: return;
        }
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Palette.Surface, new Rect(Bounds.Size));
        if (IsFocused) context.DrawRectangle(null, new Pen(Palette.Accent, 2), new Rect(Bounds.Size).Deflate(1));
        var displayedOptions = Snapshot?.RenderingOptions ?? RenderingOptions;
        DrawingText.Draw(context, $"FILLED SURFACE · {(displayedOptions.Perspective ? "perspective" : "orthographic")} · {(displayedOptions.Lighting ? "lit" : "unlit")} · X / Y arguments, Z height",
            new(12, 8), Palette.Text, 12, Math.Max(1, Bounds.Width - 24));
        var area = PlotArea;
        var revision = _preparedRevision;
        var prepared = Snapshot;
        var axes = AxisReference;
        var displayedChanged = false;
        if (_attached && IsPlotAvailable && prepared is not null && axes is not null &&
            ReferenceEquals(axes.Raster, prepared) && _bitmap is { } bitmap)
        {
            using (context.PushClip(area))
            {
                context.DrawImage(bitmap, new Rect(0, 0, prepared.Width, prepared.Height), area);
                // Spans already use this immutable frame's six-plane projection and per-pixel
                // depth test. Never paint a whole unoccluded line over the rendered surface.
                // A resize may still be displaying the prior raster while its replacement is
                // computed. Scale every annotation with that same bitmap, not with new geometry.
                using (context.PushTransform(new Matrix(area.Width / prepared.Width, 0, 0,
                    area.Height / prepared.Height, area.X, area.Y)))
                {
                    foreach (var drawing in _axisDrawings)
                    {
                        IBrush brush = AxisBrush(drawing.Dimension);
                        var gridColor = DomainGridColor;
                        if (gridColor is null && Palette.GridLine is ISolidColorBrush fallback)
                            gridColor = Color.FromArgb((byte)Math.Round(fallback.Color.A * Math.Clamp(fallback.Opacity, 0, 1)),
                                fallback.Color.R, fallback.Color.G, fallback.Color.B);
                        if (drawing.Plane is { } plane && gridColor is { } grid)
                        {
                            var color = axes.ShadeDomainColor(new(grid.R, grid.G, grid.B, grid.A), plane);
                            brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
                        }
                        context.DrawGeometry(brush, null, drawing.Geometry);
                    }
                    foreach (var label in _preparedLabels)
                        DrawingText.Draw(context, label.Text, label.Bounds.Position, AxisBrush(label.Dimension), 11);
                }
                if (_selectionPoint is { } point && SelectedHit is not null)
                {
                    context.DrawEllipse(null, new Pen(Brushes.White, 4), point, 5, 5);
                    context.DrawEllipse(null, new Pen(Brushes.Black, 2), point, 5, 5);
                }
            }
            // DrawImage was submitted for exactly this prepared raster. No data selection
            // can use a source or camera prepared after this render's revision.
            if (_attached && revision == _preparedRevision && ReferenceEquals(prepared, Snapshot) && ReferenceEquals(axes, AxisReference))
            {
                displayedChanged = !ReferenceEquals(DisplayedSnapshot, prepared) || !ReferenceEquals(DisplayedAxisReference, axes);
                DisplayedAxisReference = axes;
                DisplayedAxisLabels = _preparedLabels;
                DisplayedSnapshot = prepared;
                DisplayedFrameRevision = revision;
            }
            if (ShowLegend) RenderLegend(context, area, prepared);
        }
        else
        {
            DrawingText.Draw(context, RasterError ?? (_source is null ? "No surface source" : IsRendering ? "Preparing surface…" : "Waiting for a drawable viewport"),
                new(20, Math.Max(48, Bounds.Height / 2)), Palette.Error, 12, Math.Max(1, Bounds.Width - 40));
        }
        DrawingText.Draw(context, Status, new(12, Math.Max(0, Bounds.Height - 46)), Palette.Muted, 11, Math.Max(1, Bounds.Width - 24));
        DrawingText.Draw(context, SelectedHit is null ? CameraDescription : SelectionDescription,
            new(12, Math.Max(0, Bounds.Height - 25)), Palette.Text, 10, Math.Max(1, Bounds.Width - 24));
        // Notify once when the paired frame actually changed. Do not invalidate/rebuild from
        // Render: range readouts must not label a prepared-but-unseen raster as displayed.
        if (displayedChanged && _attached && revision == FrameRevision &&
            ReferenceEquals(prepared, DisplayedSnapshot) && ReferenceEquals(axes, DisplayedAxisReference))
        {
            QueueDisplayedNotification(revision);
        }
    }

    private void QueueDisplayedNotification(long revision)
    {
        var version = ++_displayNotificationVersion;
        var weak = new WeakReference<SurfacePlot>(this);
        // Observers can update layout. Neither accessibility properties nor external
        // observers may be notified inside Render. Do not retain the plot or its rasters.
        Dispatcher.UIThread.Post(() =>
        {
            if (!weak.TryGetTarget(out var plot) ||
                !plot.IsDisplayedNotificationCurrent(version, revision)) return;
            AutomationProperties.SetHelpText(plot, plot.AccessibleSummary);
            // A property observer may close, detach, or prepare another frame.
            if (plot.IsDisplayedNotificationCurrent(version, revision))
                plot.StateChanged?.Invoke(plot, EventArgs.Empty);
        }, DispatcherPriority.Background);
    }

    private bool IsDisplayedNotificationCurrent(long version, long revision) =>
        _attached && version == _displayNotificationVersion &&
        revision == FrameRevision && revision == DisplayedFrameRevision &&
        DisplayedSnapshot is not null && DisplayedAxisReference is not null &&
        ReferenceEquals(DisplayedSnapshot, Snapshot) &&
        ReferenceEquals(DisplayedAxisReference, AxisReference) &&
        ReferenceEquals(DisplayedAxisReference.Raster, DisplayedSnapshot);

    public IBrush AxisBrush(SurfaceAxisDimension? dimension) => dimension is null ? Palette.GridLine : Palette.Text;

    private static ImmutableArray<SurfaceAxisLabel> BuildLabels(SurfaceAxisReferenceSnapshot axes)
    {
        var raster = axes.Raster;
        var result = ImmutableArray.CreateBuilder<SurfaceAxisLabel>();
        var formats = new Dictionary<SurfaceAxisDimension, string>();
        foreach (var group in axes.Ticks.GroupBy(t => t.Dimension))
            formats[group.Key] = new[] { "G6", "G9", "G12", "G15", "G17" }.FirstOrDefault(format =>
                group.Select(t => t.Value.ToString(format, CultureInfo.InvariantCulture)).Distinct().Count() == group.Count()) ?? "G17";
        foreach (var tick in axes.Ticks)
        {
            var text = tick.Value.ToString(formats[tick.Dimension], CultureInfo.InvariantCulture);
            var measured = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 11, Brushes.White);
            var width = Math.Ceiling(measured.Width) + 2; var height = Math.Ceiling(measured.Height) + 2;
            // Offset the whole text footprint past the tick, not just its origin.
            var dx = tick.OutwardX; var dy = tick.OutwardY;
            var separation = 9 + Math.Abs(dx) * width / 2 + Math.Abs(dy) * height / 2;
            var rect = new Rect(Math.Floor(tick.Anchor.X + dx * separation - width / 2),
                Math.Floor(tick.Anchor.Y + dy * separation - height / 2), width, height);
            if (rect.X < 1 || rect.Y < 1 || rect.Right >= raster.Width || rect.Bottom >= raster.Height ||
                result.Any(label => label.Bounds.Inflate(3).Intersects(rect))) continue;
            var clear = true;
            for (var y = (int)rect.Y; y < rect.Bottom && clear; y++)
                for (var x = (int)rect.X; x < rect.Right; x++)
                    if (!double.IsPositiveInfinity(raster.Depths[y * raster.Width + x])) { clear = false; break; }
            if (clear) result.Add(new(tick.Dimension, tick.Value, text, rect));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<(SurfaceAxisDimension? Dimension, SurfaceAxisReferencePlane? Plane, StreamGeometry Geometry)> BuildAxisDrawings(
        SurfaceAxisReferenceSnapshot axes)
    {
        var result = ImmutableArray.CreateBuilder<(SurfaceAxisDimension?, SurfaceAxisReferencePlane?, StreamGeometry)>();
        foreach (var group in axes.Spans.GroupBy(span => (span.Kind == SurfaceAxisStrokeKind.Grid ? null : span.Dimension, span.Plane)))
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.SetFillRule(FillRule.NonZero);
                foreach (var span in group)
                {
                    path.BeginFigure(new(span.StartX, span.Y), true);
                    path.LineTo(new(span.EndX, span.Y)); path.LineTo(new(span.EndX, span.Y + 1));
                    path.LineTo(new(span.StartX, span.Y + 1)); path.EndFigure(true);
                }
            }
            result.Add((group.Key.Item1, group.Key.Plane, geometry));
        }
        return result.ToImmutable();
    }

    private void RenderLegend(DrawingContext context, Rect area, SurfaceRasterSnapshot raster)
    {
        // Use the displayed raster's immutable material, including while a new theme is pending.
        if (_legendBrush is null || !Equals(_legendMaterial, raster.RenderingOptions.Material) ||
            _legendMinimum != raster.MinimumValue || _legendMaximum != raster.MaximumValue)
        {
            _legendMaterial = raster.RenderingOptions.Material;
            _legendMinimum = raster.MinimumValue; _legendMaximum = raster.MaximumValue;
            var values = _legendMaterial is null
                ? new[] { _legendMinimum, _legendMinimum / 2 + _legendMaximum / 2, _legendMaximum }.Distinct().ToArray()
                : new[] { _legendMinimum, 0, _legendMaximum }.Distinct().Order().ToArray();
            var low = values[0]; var high = values[^1];
            double Offset(double value) => low == high ? .5 :
                (value / 2 - low / 2) / (high / 2 - low / 2);
            Color ColorAt(double value)
            {
                var color = SurfaceRasterizer.ColorAt(value, _legendMinimum, _legendMaximum, _legendMaterial);
                return Color.FromArgb(color.A, color.R, color.G, color.B);
            }
            _legendStops = values.Select(value => (value, Offset(value), (IBrush)new SolidColorBrush(ColorAt(value)))).ToArray();
            _legendBrush = new LinearGradientBrush { StartPoint = new(0, 1, RelativeUnit.Relative), EndPoint = new(0, 0, RelativeUnit.Relative) };
            // Exact knots for the legacy piecewise-linear rainbow; material stops use Min/0/Max.
            var gradientValues = _legendMaterial is null
                ? new[] { 0, .125, .375, .625, .875, 1 }.Select(t => low * (1 - t) + high * t)
                : values;
            foreach (var value in gradientValues)
                _legendBrush.GradientStops.Add(new GradientStop(ColorAt(value), Offset(value)));
        }
        const double width = 10;
        // Reference legend uses half the available height, with half-label end insets.
        // A reserved gutter keeps gradient/ticks/text outside the selectable surface.
        var height = Math.Max(1, area.Height / 2 - 14);
        var x = area.Right + 12; var y = area.Top + 12;
        context.FillRectangle(_legendBrush, new Rect(x, y, width, height));
        for (var i = 0; i < _legendStops.Length; i++)
        {
            var stop = _legendStops[i]; var offset = (1 - stop.Offset) * height;
            // Keep the two endpoint labels when a middle stop would overlap either one.
            if (i > 0 && i < _legendStops.Length - 1 && (offset < 16 || height - offset < 16)) continue;
            context.FillRectangle(stop.Brush, new Rect(x + width, y + offset - 1, 10, 2));
            DrawingText.Draw(context, stop.Value.ToString("G4", CultureInfo.InvariantCulture),
                new(x + 30, y + offset - 7), Palette.Text, 11, 72);
        }
    }
}
