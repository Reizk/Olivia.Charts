using System.Collections.Immutable;
using System.Globalization;
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
using Olivia.Core.Imaging;
using Olivia.Avalonia.Themes;

namespace Olivia.Avalonia;

/// <summary>Exact on-control line consumed by the crosshair renderer; no line crosses pane gaps.</summary>
public readonly record struct PriceCrosshairLine(Point Start, Point End);
/// <summary>Exact unclamped visible-series marker consumed by the crosshair renderer.</summary>
public readonly record struct PriceCrosshairMarker(PriceCrosshairPane Pane, Point Point);
/// <summary>The exact complete-note box and unchanged chart-local DIP anchor consumed by Render.</summary>
public readonly record struct PriceTextAnnotationBox(long Id, Rect Bounds, Point AnchorPoint);
/// <summary>The exact bounded image box and unchanged chart-local DIP anchor consumed by Render.</summary>
public readonly record struct PriceImageAnnotationBox(long Id, Rect Bounds, Point AnchorPoint);
/// <summary>The exact clipped main-pane segment, finite or positive-X extended, consumed by rendering and DIP hit testing.</summary>
public readonly record struct PriceTrendLineSegment(long Id, Point Start, Point End);
/// <summary>One exact clipped horizontal retracement level consumed by rendering and hit testing.</summary>
public readonly record struct PriceRetracementLevel(long Id, double Ratio, double Price, Point Start, Point End);
/// <summary>One exact XY-clipped fan ray; SecondPrice is the derived price at the second anchor X, not a constant ray price.</summary>
public readonly record struct PriceFanLevel(long Id, double Ratio, double? SecondPrice, Point Start, Point End);
/// <summary>One arc level's exact clipped polyline segments in chart DIPs; the same segments render and hit-test.</summary>
public readonly record struct PriceArcLevel(long Id, double Ratio, double RadiusDips, ImmutableArray<PriceCrosshairLine> Segments);

/// <summary>Bounded immutable OHLC series, raw ordinal or compressed UTC aggregation with retained provenance.</summary>
public sealed class PricePlot : Control
{
    public static readonly StyledProperty<UiPalette> PaletteProperty =
        AvaloniaProperty.Register<PricePlot, UiPalette>(nameof(Palette), UiPalette.Dark);
    public static readonly StyledProperty<OliviaChartTheme?> ChartThemeProperty =
        AvaloniaProperty.Register<PricePlot, OliviaChartTheme?>(nameof(ChartTheme));
    private IBrush[] _seriesBrushes = [];
    private readonly record struct BollingerSegment(HistoryChartLine Line, bool IsMiddle);
    private sealed record PriceFrame(PriceChartSource? Source, PriceChartOptions Options,
        PriceChartSnapshot? Snapshot, bool BollingerEnabled, PriceBollingerSnapshot? BollingerBands,
        ImmutableArray<BollingerSegment> BollingerSegments, bool WilliamsREnabled,
        PriceWilliamsRSnapshot? WilliamsRValues, ImmutableArray<HistoryChartLine> WilliamsRSegments,
        bool CciEnabled, PriceCciSnapshot? CciValues, ImmutableArray<HistoryChartLine> CciSegments,
        bool VolumeEnabled, PriceVolumeSnapshot? VolumeValues, PriceTimeAggregationSnapshot? Aggregation,
        PriceCurrentCloseSnapshot? CurrentClose);
    private PriceFrame _frame = new(null, new(), null, false, null, [], false, null, [], false, null, [], false, null, null, null);
    private readonly PriceIndicatorCache _indicatorCache = new();
    private static readonly IBrush VolumeBrush = new SolidColorBrush(Color.FromRgb(128, 128, 128));
    private static readonly IBrush CurrentCloseBrush = new SolidColorBrush(Color.FromRgb(247, 138, 9));
    private const double CurrentCloseGutter = 132;
    private sealed record CurrentCloseBadge(Rect Bounds, Point AxisPoint, FormattedText Text);
    private long _badgeRevision = -1;
    private CurrentCloseBadge? _badge;
    private string _badgePlacement = "NoSource";
    private PriceCrosshairSnapshot? _crosshair;
    private ImmutableArray<PriceCrosshairLine> _crosshairLines = [];
    private ImmutableArray<PriceCrosshairMarker> _crosshairHighlights = [];
    private sealed record CrosshairLabel(Rect Bounds, FormattedText Text);
    private CrosshairLabel? _crosshairLabel;
    private string _crosshairLabelPlacement = "NoHover";
    private static readonly HistoryValueDomain WilliamsRDomain = new(-100, 0);
    private static readonly HistoryValueDomain CciDisplayDomain = new(-150, 150);
    private const double PaneGap = 28;
    private PriceChartSource? _source => _frame.Source;
    private PriceChartOptions _options => _frame.Options;
    private int _selection = -1;
    private long _statusRevision = -1;
    private string _cachedStatus = "";
    private string? _selectionDetails;
    // Volume may select original bars whose OHLC is wholly outside the fixed price domain.
    // Keep it separate from the candle-only keyboard/index selection contract.
    private PriceChartHit? _volumeSelection;
    private bool _attached;
    private readonly PriceTextAnnotations _textAnnotations = new();
    private bool _textAnnotationPlacementArmed;
    private long _textAnnotationGeneration;
    private sealed record TextAnnotationLayout(PriceTextAnnotationBox Box, FormattedText Text);
    private ImmutableArray<TextAnnotationLayout> _textAnnotationLayouts = [];
    private ImmutableArray<PriceTextAnnotationBox> _textAnnotationBoxes = [];
    private long _textAnnotationLayoutRevision = -1;
    private Size _textAnnotationLayoutSize;
    private Rect _textAnnotationLayoutArea;
    private UiPalette? _textAnnotationLayoutPalette;
    private bool _textAnnotationLayoutAvailable;
    private bool _textAnnotationLayoutHasSource;
    private bool _textAnnotationLayoutAttached;
    private string _textAnnotationLayoutStatus = "NoSource";
    private readonly PriceImageAnnotations _imageAnnotations = new();
    private bool _imageAnnotationPlacementArmed;
    private long _imageAnnotationGeneration;
    private sealed record ImageAnnotationLayout(PriceImageAnnotationBox Box, RasterImage Image);
    private sealed record ImageAnnotationBitmap(RasterImage Image, WriteableBitmap Bitmap);
    private ImmutableArray<ImageAnnotationLayout> _imageAnnotationLayouts = [];
    private ImmutableArray<PriceImageAnnotationBox> _imageAnnotationBoxes = [];
    private readonly Dictionary<long, ImageAnnotationBitmap> _imageAnnotationBitmaps = new();
    private long _imageAnnotationLayoutRevision = -1;
    private Size _imageAnnotationLayoutSize;
    private Rect _imageAnnotationLayoutArea;
    private bool _imageAnnotationLayoutAvailable;
    private bool _imageAnnotationLayoutHasSource;
    private bool _imageAnnotationLayoutAttached;
    private string _imageAnnotationLayoutStatus = "NoSource";
    private readonly PriceTrendLines _trendLines = new();
    private readonly PriceFibonacciRetracements _retracements = new();
    private readonly PriceFibonacciFans _fans = new();
    private readonly PriceFibonacciArcs _arcs = new();
    private long _trendLineGeneration;
    private long _retracementGeneration;
    private long _fanGeneration;
    private long _arcGeneration;
    private long _arcLayoutFrameRevision = -1, _arcLayoutStoreRevision = -1;
    private Rect _arcLayoutArea;
    private bool _arcLayoutAttached, _arcLayoutAvailable;
    private PriceChartSnapshot? _arcLayoutSnapshot;
    private PriceTimeAggregationSnapshot? _arcLayoutAggregation;
    private ImmutableDictionary<long, PriceFibonacciArcProjection> _arcProjections = ImmutableDictionary<long, PriceFibonacciArcProjection>.Empty;
    private ImmutableArray<PriceArcLevel> _visibleArcLevels = [];
    private PriceTrendValueLevel _trendFirstValueLevel = PriceTrendValueLevel.Close;
    private PriceTrendValueLevel _trendSecondValueLevel = PriceTrendValueLevel.Close;
    private PriceTrendValueLevel _retracementFirstValueLevel = PriceTrendValueLevel.Close;
    private PriceTrendValueLevel _retracementSecondValueLevel = PriceTrendValueLevel.Close;
    private PriceTrendValueLevel _fanFirstValueLevel = PriceTrendValueLevel.Close;
    private PriceTrendValueLevel _fanSecondValueLevel = PriceTrendValueLevel.Close;
    private PriceTrendValueLevel _arcFirstValueLevel = PriceTrendValueLevel.Close;
    private PriceTrendValueLevel _arcSecondValueLevel = PriceTrendValueLevel.Close;
    private bool _extrapolateToInfinity;
    private bool _retracementShowLevel23_6, _retracementShowLevel76_4, _retracementShowAdditionalLevels;
    private bool _fanShowLevel0, _fanShowLevel23_6, _fanShowLevel76_4;
    private bool _arcShowLevel23_6, _arcShowLevel76_4, _arcShowLevel100;
    // A single owner is shared by all four tools. Store and exact draft identity are part of
    // ownership, so coincident IDs/revisions/generations can never transfer a gesture.
    private sealed class TwoAnchorGesture(object store, object draft, long generation, int endpointEdit)
    {
        public object Store { get; } = store;
        public object Draft { get; } = draft;
        public long Generation { get; set; } = generation;
        public long FrameRevision { get; set; }
        public int EndpointEdit { get; } = endpointEdit;
        public PriceTrendAnchor? First { get; set; }
        public PriceTrendAnchor? Preview { get; set; }
        public int KeyboardIndex { get; set; } = -1;
        public IPointer? Pointer { get; set; }
        public Point Origin { get; set; }
        public bool DragMoved { get; set; }
    }
    private TwoAnchorGesture? _twoAnchorGesture;
    private IPointer? _gestureCaptureToRelease;
    private TwoAnchorGesture? TrendGesture => ReferenceEquals(_twoAnchorGesture?.Store, TrendLines) ? _twoAnchorGesture : null;
    private TwoAnchorGesture? RetracementGesture => ReferenceEquals(_twoAnchorGesture?.Store, Retracements) ? _twoAnchorGesture : null;
    private TwoAnchorGesture? FanGesture => ReferenceEquals(_twoAnchorGesture?.Store, Fans) ? _twoAnchorGesture : null;
    private TwoAnchorGesture? ArcGesture => ReferenceEquals(_twoAnchorGesture?.Store, Arcs) ? _twoAnchorGesture : null;
    private PriceTrendAnchor? _trendFirstAnchor => TrendGesture?.First;
    private PriceTrendAnchor? _trendPreviewAnchor => TrendGesture?.Preview;
    private int _trendEndpointEdit => TrendGesture?.EndpointEdit ?? 0;
    private sealed record PointerFrame(long Revision, long AnnotationRevision, long AnnotationGeneration,
        long ImageAnnotationRevision, long ImageAnnotationGeneration, long TrendRevision, long TrendGeneration,
        long RetracementRevision, long RetracementGeneration, long FanRevision, long FanGeneration, long ArcRevision, long ArcGeneration);
    private readonly ConditionalWeakTable<PointerPressedEventArgs, PointerFrame> _pointerFrames = new();
    private readonly ConditionalWeakTable<PointerReleasedEventArgs, PointerFrame> _releaseFrames = new();
    private readonly ConditionalWeakTable<KeyEventArgs, PointerFrame> _keyFrames = new();
    private readonly ConditionalWeakTable<PointerWheelEventArgs, PointerFrame> _wheelFrames = new();
    private sealed record HoverFrame(long Revision, long Generation, long TrendGeneration, long RetracementGeneration, long FanGeneration, long ArcRevision, long ArcGeneration);
    private long _hoverGeneration;
    private readonly ConditionalWeakTable<PointerEventArgs, HoverFrame> _hoverFrames = new();
    private InputElement? _inputRoot;
    private void CapturePointer(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, this)) _pointerFrames.GetValue(e, _ => new PointerFrame(FrameRevision, TextAnnotations.Revision, _textAnnotationGeneration,
            ImageAnnotations.Revision, _imageAnnotationGeneration, TrendLines.Revision, _trendLineGeneration, Retracements.Revision, _retracementGeneration, Fans.Revision, _fanGeneration, Arcs.Revision, _arcGeneration));
    }
    private void CapturePointerRelease(object? sender, PointerReleasedEventArgs e)
    {
        if (ReferenceEquals(e.Source, this)) _releaseFrames.GetValue(e, _ => new PointerFrame(FrameRevision,
            TextAnnotations.Revision, _textAnnotationGeneration, ImageAnnotations.Revision,
            _imageAnnotationGeneration, TrendLines.Revision, _trendLineGeneration, Retracements.Revision, _retracementGeneration, Fans.Revision, _fanGeneration, Arcs.Revision, _arcGeneration));
    }
    private void CaptureWheel(object? sender, PointerWheelEventArgs e)
    {
        if (ReferenceEquals(e.Source, this)) _wheelFrames.GetValue(e, _ => new PointerFrame(FrameRevision,
            TextAnnotations.Revision, _textAnnotationGeneration, ImageAnnotations.Revision, _imageAnnotationGeneration,
            TrendLines.Revision, _trendLineGeneration, Retracements.Revision, _retracementGeneration,
            Fans.Revision, _fanGeneration, Arcs.Revision, _arcGeneration));
    }
    private void CaptureHover(object? sender, PointerEventArgs e)
    {
        if (ReferenceEquals(e.Source, this)) _hoverFrames.GetValue(e, _ => new HoverFrame(FrameRevision, ++_hoverGeneration, _trendLineGeneration, _retracementGeneration, _fanGeneration, Arcs.Revision, _arcGeneration));
    }
    private void CaptureKey(object? sender, KeyEventArgs e)
    {
        if (ReferenceEquals(e.Source, this)) _keyFrames.GetValue(e, _ => new PointerFrame(FrameRevision,
            TextAnnotations.Revision, _textAnnotationGeneration, ImageAnnotations.Revision, _imageAnnotationGeneration,
            TrendLines.Revision, _trendLineGeneration, Retracements.Revision, _retracementGeneration,
            Fans.Revision, _fanGeneration, Arcs.Revision, _arcGeneration));
    }
    static PricePlot()
    {
        // Capture before root instance callbacks can replace the frame or authoring intent.
        // Class handlers retain no plot references; event-keyed tables isolate nested input.
        PointerPressedEvent.AddClassHandler<TopLevel>((root, e) =>
        {
            if (e.Source is PricePlot chart && chart._attached && ReferenceEquals(chart._inputRoot, root))
                chart.CapturePointer(root, e);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerReleasedEvent.AddClassHandler<TopLevel>((root, e) =>
        {
            if (e.Source is PricePlot chart && chart._attached && ReferenceEquals(chart._inputRoot, root))
                chart.CapturePointerRelease(root, e);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerMovedEvent.AddClassHandler<TopLevel>((root, e) =>
        {
            if (e.Source is PricePlot chart && chart._attached && ReferenceEquals(chart._inputRoot, root))
                chart.CaptureHover(root, e);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        KeyDownEvent.AddClassHandler<TopLevel>((root, e) =>
        {
            if (e.Source is PricePlot chart && chart._attached && ReferenceEquals(chart._inputRoot, root))
                chart.CaptureKey(root, e);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerWheelChangedEvent.AddClassHandler<TopLevel>((root, e) =>
        {
            if (e.Source is PricePlot chart && chart._attached && ReferenceEquals(chart._inputRoot, root))
                chart.CaptureWheel(root, e);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }
    public PricePlot()
    {
        Focusable = true; ClipToBounds = true; MinHeight = 180;
        // Crosshair owns the chart's hover presentation. The legacy selection summary
        // remains available through Tip/automation/diagnostics, but its enormous native
        // popup must not cover the price panes and intercept subsequent pointer moves.
        ToolTip.AddToolTipOpeningHandler(this, (_, e) =>
        {
            if (_attached && IsPlotAvailable && ToolTip.GetTip(this) is string tip &&
                tip == AccessibleSummary) { e.Cancel = true; e.Handled = true; }
        });
        GotFocus += (_, _) => InvalidateVisual();
        // The real editor lives outside this plot. Losing focus still clears transient
        // candle/hover state, but must not abandon its valid annotation draft.
        LostFocus += (_, _) => CancelInteraction();
        AutomationProperties.SetName(this, "Candlestick chart with optional independent-scale volume overlay. Raw ordinal or compressed UTC time spacing. Hover any pane for shared-X values without selecting. Left and right select candles; plus and minus zoom; PageUp and PageDown pan; Home fits; Escape cancels annotation, trend-line, retracement, fan or arc authoring first, then clears selection. F2 edits a selected note or redraws a selected trend line, retracement, fan or arc; Delete removes it. Trend lines join two actual original OHLC values and are finite by default, with optional positive-X extension only. The new-line extension preference does not change selected lines; use the selected-line extension control for that. Drag from one raw candle to another, click each candle in turn, or use Left/Right and Enter for each endpoint while a two-anchor tool is active. Fibonacci Retracements are independent bounded horizontal levels: first original anchor is 100%, second is 0%. Close/Close and optional levels off are explicit Olivia defaults. Fibonacci Fans have a first-anchor origin and second-anchor zero-level line; fixed 38.2%, 50%, 61.8% rays and optional 0%, 23.6%, 76.4%. Fan rays extend from the origin through the derived point in the authored X direction, including leftward, not always positive X. Fan geometry and Close/Close with optional levels off are explicit Olivia compatibility choices, not WPF runtime parity. Fibonacci Arcs are independent full 360-degree circles centered at the first original anchor. The second defines the 100% radius measured in plot DIPs; fixed 38.2%, 50%, 61.8% and optional 23.6%, 76.4%, 100%. Full sweep and plot-DIP distance are Olivia compatibility choices, not WPF runtime parity. Raw display is required for authoring; aggregate display retains original values and suppresses unresolved or collapsed endpoints.");
    }
    public PriceChartSnapshot? Snapshot => _frame.Snapshot;
    /// <summary>Bounded chart-position notes. UI callers should mutate through this control's methods.</summary>
    public PriceTextAnnotations TextAnnotations => _textAnnotations;
    public long? SelectedTextAnnotationId => TextAnnotations.SelectedId;
    public bool IsTextAnnotationPlacementArmed => _textAnnotationPlacementArmed;
    public bool IsTextAnnotationToolActive => _textAnnotationPlacementArmed || TextAnnotations.Draft is not null;
    /// <summary>Bounded chart-local images; use this control's methods to maintain exclusive tools.</summary>
    public PriceImageAnnotations ImageAnnotations => _imageAnnotations;
    public long? SelectedImageAnnotationId => ImageAnnotations.SelectedId;
    public bool IsImageAnnotationPlacementArmed => _imageAnnotationPlacementArmed;
    public bool IsImageAnnotationToolActive => _imageAnnotationPlacementArmed || ImageAnnotations.Draft is not null;
    /// <summary>Exact image-tool ownership generation; ordinary hover/focus updates do not advance it.</summary>
    public long ImageAnnotationToolGeneration => _imageAnnotationGeneration;
    public bool IsAnyAnnotationToolActive => IsTextAnnotationToolActive || IsImageAnnotationToolActive || IsTrendLineToolActive || IsRetracementToolActive || IsFanToolActive || IsArcToolActive;
    public ImmutableArray<PriceImageAnnotationBox> VisibleImageAnnotationBoxes
    { get { EnsureImageAnnotationLayout(); return _imageAnnotationBoxes; } }
    public int SuppressedImageAnnotationCount
    { get { EnsureImageAnnotationLayout(); return ImageAnnotations.Items.Length - _imageAnnotationBoxes.Length; } }
    public string ImageAnnotationLayoutStatus
    { get { EnsureImageAnnotationLayout(); return _imageAnnotationLayoutStatus; } }
    /// <summary>Live native bitmap count, bounded by committed notes and reset on detach/release.</summary>
    public int CachedImageAnnotationBitmapCount
    { get { PruneImageAnnotationBitmaps(); return _imageAnnotationBitmaps.Count; } }
    /// <summary>Follows the common TextAnnotationToolChanged notification, with both tools settled.</summary>
    public event EventHandler? ImageAnnotationToolChanged;
    public ImmutableArray<PriceTextAnnotationBox> VisibleTextAnnotationBoxes
    { get { EnsureTextAnnotationLayout(); return _textAnnotationBoxes; } }
    public int SuppressedTextAnnotationCount
    { get { EnsureTextAnnotationLayout(); return TextAnnotations.Items.Length - _textAnnotationBoxes.Length; } }
    public string TextAnnotationLayoutStatus
    { get { EnsureTextAnnotationLayout(); return _textAnnotationLayoutStatus; } }
    /// <summary>Published after mode, draft, selection and rendering state are settled.</summary>
    public event EventHandler? TextAnnotationToolChanged;

    /// <summary>Original-data anchors, not arbitrary chart coordinates. Mutate through this control.</summary>
    public PriceTrendLines TrendLines => _trendLines;
    public long? SelectedTrendLineId => TrendLines.SelectedId;
    public bool IsTrendLineToolActive => TrendLines.Draft is not null;
    public bool IsTrendLinePlacementArmed => IsTrendLineToolActive;
    /// <summary>Exact trend-tool ownership generation, independent of benign host UI refreshes.</summary>
    public long TrendLineToolGeneration => _trendLineGeneration;
    public PriceTrendAnchor? TrendLineFirstAnchor => _trendFirstAnchor;
    public PriceTrendAnchor? TrendLinePreviewAnchor => _trendPreviewAnchor;
    /// <summary>Explicit UI compatibility choice. Changing it cancels a pending draft; assigning the same value is inert.</summary>
    public PriceTrendValueLevel TrendLineFirstValueLevel
    {
        get => _trendFirstValueLevel;
        set
        {
            Dispatcher.UIThread.VerifyAccess(); ValidateTrendLevel(value);
            if (_trendFirstValueLevel == value) return;
            _trendFirstValueLevel = value; ++_trendLineGeneration;
            InvalidateFrame(trendLineChanged: true);
        }
    }
    public PriceTrendValueLevel TrendLineSecondValueLevel
    {
        get => _trendSecondValueLevel;
        set
        {
            Dispatcher.UIThread.VerifyAccess(); ValidateTrendLevel(value);
            if (_trendSecondValueLevel == value) return;
            _trendSecondValueLevel = value; ++_trendLineGeneration;
            InvalidateFrame(trendLineChanged: true);
        }
    }
    /// <summary>
    /// Explicit new-line preference, false by compatibility choice rather than an official default.
    /// Changes cancel a pending draft but never change a retained line. Same-value assignments are inert.
    /// </summary>
    public bool ExtrapolateToInfinity
    {
        get => _extrapolateToInfinity;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (_extrapolateToInfinity == value) return;
            _extrapolateToInfinity = value; ++_trendLineGeneration;
            InvalidateFrame(trendLineChanged: true);
        }
    }
    /// <summary>Follows the common and image notifications, only while all annotation tools remain current.</summary>
    public event EventHandler? TrendLineToolChanged;
    private static void ValidateTrendLevel(PriceTrendValueLevel value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
    }
    private bool CanAuthorTrendLine => _attached && IsPlotAvailable && Snapshot is { } prices &&
        AggregationSnapshot is null && prices.BudgetStatus == ChartBudgetStatus.Satisfied &&
        prices.Domain is not null && !prices.Candles.IsEmpty;
    public bool BeginTrendLinePlacement()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanAuthorTrendLine || TrendLines.Items.Length >= PriceTrendLines.MaximumLines) return false;
        var draft = TrendLines.BeginNew(ExtrapolateToInfinity);
        if (draft is null) return false;
        var textChanged = ClearTextAnnotationSelectionCore();
        var imageChanged = ClearImageAnnotationSelectionCore();
        var retracementChanged = ClearRetracementSelectionCore();
        StartTwoAnchorGesture(TrendLines, draft, ++_trendLineGeneration);
        InvalidateFrame(textChanged, imageChanged, trendLineChanged: true, preserveTrendTool: true,
            retracementChanged: retracementChanged);
        return _attached && ReferenceEquals(TrendLines.Draft, draft);
    }
    /// <summary>Redraws both endpoints. The committed original stays intact until the exact draft commits.</summary>
    public bool BeginSelectedTrendLineEdit() => BeginTrendLineEdit(0);
    /// <summary>Replaces one original OHLC endpoint; the other original endpoint stays unchanged.</summary>
    public bool BeginSelectedTrendLineEndpointEdit(bool firstEndpoint) => BeginTrendLineEdit(firstEndpoint ? 1 : 2);
    private bool BeginTrendLineEdit(int endpoint)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanAuthorTrendLine || TrendLines.SelectedId is not { } id) return false;
        var draft = TrendLines.BeginEdit(id);
        if (draft is null) return false;
        var textChanged = ClearTextAnnotationSelectionCore();
        var imageChanged = ClearImageAnnotationSelectionCore();
        var retracementChanged = ClearRetracementSelectionCore();
        StartTwoAnchorGesture(TrendLines, draft, ++_trendLineGeneration, endpoint);
        InvalidateFrame(textChanged, imageChanged, trendLineChanged: true, preserveTrendTool: true,
            retracementChanged: retracementChanged);
        return _attached && ReferenceEquals(TrendLines.Draft, draft);
    }
    public void CancelTrendLineTool()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (CancelTrendLineToolCore()) InvalidateFrame(trendLineChanged: true);
    }
    private bool CancelTrendLineToolCore()
    {
        var changed = TrendLines.Draft is { } draft && TrendLines.Cancel(draft);
        changed |= TrendGesture is not null;
        ResetTrendGestureCore();
        if (changed) ++_trendLineGeneration;
        return changed;
    }
    private void ResetTrendGestureCore() => ResetTwoAnchorGestureCore(TrendLines);
    private void StartTwoAnchorGesture(object store, object draft, long generation, int endpoint = 0)
    {
        if (_twoAnchorGesture is { } previous) ResetTwoAnchorGestureCore(previous.Store);
        _twoAnchorGesture = new(store, draft, generation, endpoint);
    }
    private bool ResetTwoAnchorGestureCore(object store)
    {
        if (_twoAnchorGesture is not { } gesture || !ReferenceEquals(gesture.Store, store)) return false;
        _twoAnchorGesture = null;
        // Retire ownership before releasing physical capture, which may reenter this control.
        if (gesture.Pointer is { } pointer) _gestureCaptureToRelease = pointer;
        return true;
    }
    public bool SelectTrendLine(long id)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null || !TrendLines.Select(id)) return false;
        var textChanged = ClearTextAnnotationSelectionCore();
        var imageChanged = ClearImageAnnotationSelectionCore();
        ResetTrendGestureCore(); ++_trendLineGeneration;
        InvalidateFrame(textChanged, imageChanged, trendLineChanged: true);
        return _attached && TrendLines.SelectedId == id && TrendLines.Draft is null;
    }
    public bool ClearTrendLineSelection()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || !ClearTrendLineSelectionCore()) return false;
        InvalidateFrame(trendLineChanged: true);
        return _attached && TrendLines.SelectedId is null && !IsTrendLineToolActive;
    }
    private bool ClearTrendLineSelectionCore()
    {
        var changed = TrendLines.ClearSelection();
        changed |= TrendGesture is not null;
        ResetTrendGestureCore();
        if (!changed) return false;
        ++_trendLineGeneration; ++FrameRevision; _statusRevision = -1;
        _badge = null; _badgeRevision = -1; ClearCrosshairCore();
        return true;
    }
    public bool RemoveSelectedTrendLine()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null) return false;
        var previous = TrendLines.Revision;
        var removed = TrendLines.RemoveSelected();
        ResetTrendGestureCore();
        if (previous != TrendLines.Revision)
        {
            ++_trendLineGeneration;
            InvalidateFrame(trendLineChanged: true);
        }
        return removed;
    }
    /// <summary>
    /// Changes only the exact currently selected retained line, without changing its original anchors
    /// or the new-line preference. Active annotation authoring is never replaced by this operation.
    /// </summary>
    public bool SetSelectedTrendLineExtrapolation(bool value)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null || IsAnyAnnotationToolActive || TrendLines.SelectedId is not { } id)
            return false;
        var expected = TrendLines.Items.FirstOrDefault(line => line.Id == id);
        if (expected is null) return false;
        var previousRevision = TrendLines.Revision;
        if (!TrendLines.SetExtrapolateToInfinity(expected, value)) return false;
        // The exact retained object was checked by the store even for a no-op. Do not disturb
        // pointer generations, frame caches, selection or notifications for that assignment.
        if (previousRevision == TrendLines.Revision) return true;
        var updated = TrendLines.Items.FirstOrDefault(line => line.Id == id);
        var revision = TrendLines.Revision;
        var generation = ++_trendLineGeneration;
        InvalidateFrame(trendLineChanged: true);
        // A nested tool, source release or second selected-line update wins. Never restore
        // the earlier object or publish another transition after the guarded notifications.
        return _attached && generation == _trendLineGeneration && revision == TrendLines.Revision &&
            TrendLines.SelectedId == id && !IsAnyAnnotationToolActive &&
            ReferenceEquals(updated, TrendLines.Items.FirstOrDefault(line => line.Id == id));
    }
    /// <summary>Accessible index alternative; index addresses the actual rendered candle array.</summary>
    public bool CaptureTrendLineAnchor(int candleIndex, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        return CaptureTwoAnchorAtIndex(TrendGesture, candleIndex, expectedRevision);
    }
    /// <summary>Click-click alternative. Only an actual raw candle can supply either exact OHLC point.</summary>
    public bool CaptureTrendLineAnchorAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        return TrendGesture is { } gesture && FindTwoAnchorAt(gesture, point, expectedRevision) is { } anchor &&
            CaptureTwoAnchorCore(gesture, anchor);
    }
    private bool IsCurrentTwoAnchorGesture(TwoAnchorGesture gesture)
    {
        if (!_attached || !ReferenceEquals(_twoAnchorGesture, gesture) || gesture.FrameRevision != FrameRevision) return false;
        if (ReferenceEquals(gesture.Store, TrendLines))
            return ReferenceEquals(gesture.Draft, TrendLines.Draft) && gesture.Generation == _trendLineGeneration;
        if (ReferenceEquals(gesture.Store, Retracements))
            return ReferenceEquals(gesture.Draft, Retracements.Draft) && gesture.Generation == _retracementGeneration;
        if (ReferenceEquals(gesture.Store, Fans))
            return ReferenceEquals(gesture.Draft, Fans.Draft) && gesture.Generation == _fanGeneration;
        if (ReferenceEquals(gesture.Store, Arcs))
            return ReferenceEquals(gesture.Draft, Arcs.Draft) && gesture.Generation == _arcGeneration;
        return false;
    }
    private bool CanCaptureTwoAnchor(TwoAnchorGesture gesture, long revision) =>
        CanAuthorTrendLine && revision == FrameRevision && IsCurrentTwoAnchorGesture(gesture);
    private PriceTrendValueLevel NextTwoAnchorLevel(TwoAnchorGesture gesture)
    {
        var first = gesture.EndpointEdit == 1 || (gesture.EndpointEdit == 0 && gesture.First is null);
        if (ReferenceEquals(gesture.Store, TrendLines)) return first ? TrendLineFirstValueLevel : TrendLineSecondValueLevel;
        if (ReferenceEquals(gesture.Store, Retracements)) return first ? RetracementFirstValueLevel : RetracementSecondValueLevel;
        if (ReferenceEquals(gesture.Store, Fans)) return first ? FanFirstValueLevel : FanSecondValueLevel;
        if (ReferenceEquals(gesture.Store, Arcs)) return first ? ArcFirstValueLevel : ArcSecondValueLevel;
        throw new InvalidOperationException("Unknown two-anchor tool owner.");
    }
    private bool CaptureTwoAnchorAtIndex(TwoAnchorGesture? gesture, int candleIndex, long expectedRevision)
    {
        if (gesture is null || !CanCaptureTwoAnchor(gesture, expectedRevision) || Snapshot is not { } prices ||
            candleIndex < 0 || candleIndex >= prices.Candles.Length) return false;
        var anchor = PriceTrendGeometry.CreateAnchor(prices, prices.Candles[candleIndex].BarIndex, NextTwoAnchorLevel(gesture));
        return anchor is { } value && CaptureTwoAnchorCore(gesture, value);
    }
    private PriceTrendAnchor? FindTwoAnchorAt(TwoAnchorGesture gesture, Point point, long expectedRevision)
    {
        if (!CanCaptureTwoAnchor(gesture, expectedRevision) || !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            !PlotArea.Contains(point) || Snapshot is not { } prices) return null;
        if (VisibleImageAnnotationBoxes.Any(box => box.Bounds.Contains(point)) ||
            VisibleTextAnnotationBoxes.Any(box => box.Bounds.Contains(point))) return null;
        var area = PlotArea;
        var x = (point.X - area.X) / area.Width;
        var y = (point.Y - area.Y) / area.Height;
        // A neighboring candle's tolerance must not steal an exact body/wick hit in dense views.
        // Plot/window/normalized round trips can perturb a zero-height doji or zero-width wick.
        // This subpixel allowance covers numerical roundoff, not the user-facing 5-DIP hit target.
        const double roundoffDips = 1e-7;
        var hit = PriceChartGeometry.HitTest(prices, x, y, roundoffDips / area.Width, roundoffDips / area.Height) ??
            PriceChartGeometry.HitTest(prices, x, y, 5 / area.Width, 5 / area.Height);
        if (hit is null) return null;
        foreach (var candle in prices.Candles)
            if (candle.Hit == hit.Value)
                return PriceTrendGeometry.CreateAnchor(prices, candle.BarIndex, NextTwoAnchorLevel(gesture));
        return null;
    }
    private bool CaptureTwoAnchorCore(TwoAnchorGesture gesture, PriceTrendAnchor anchor, IPointer? pointer = null, Point origin = default)
    {
        if (!CanCaptureTwoAnchor(gesture, FrameRevision)) return false;
        var originalFirst = gesture.Draft switch
        {
            PriceTrendLineDraft { Original: { } original } => original.First,
            PriceFibonacciRetracementDraft { Original: { } original } => original.First,
            PriceFibonacciFanDraft { Original: { } original } => original.First,
            PriceFibonacciArcDraft { Original: { } original } => original.First,
            _ => (PriceTrendAnchor?)null
        };
        var originalSecond = gesture.Draft switch
        {
            PriceTrendLineDraft { Original: { } original } => original.Second,
            PriceFibonacciRetracementDraft { Original: { } original } => original.Second,
            PriceFibonacciFanDraft { Original: { } original } => original.Second,
            PriceFibonacciArcDraft { Original: { } original } => original.Second,
            _ => (PriceTrendAnchor?)null
        };
        if (gesture.EndpointEdit != 0 && originalFirst is { } existingFirst && originalSecond is { } existingSecond)
            return CommitTwoAnchors(gesture, gesture.EndpointEdit == 1 ? anchor : existingFirst,
                gesture.EndpointEdit == 2 ? anchor : existingSecond);
        if (gesture.First is { } first) return CommitTwoAnchors(gesture, first, anchor);
        gesture.First = anchor; gesture.Preview = null;
        AdvanceTwoAnchorGeneration(gesture);
        if (pointer is not null)
        {
            gesture.Pointer = pointer; gesture.Origin = origin; gesture.DragMoved = false;
            pointer.Capture(this);
            if (!IsCurrentTwoAnchorGesture(gesture) || !ReferenceEquals(pointer.Captured, this)) return false;
        }
        InvalidateTwoAnchorGesture(gesture);
        return IsCurrentTwoAnchorGesture(gesture);
    }
    private void AdvanceTwoAnchorGeneration(TwoAnchorGesture gesture)
    {
        if (ReferenceEquals(gesture.Store, TrendLines)) gesture.Generation = ++_trendLineGeneration;
        else if (ReferenceEquals(gesture.Store, Retracements)) gesture.Generation = ++_retracementGeneration;
        else if (ReferenceEquals(gesture.Store, Fans)) gesture.Generation = ++_fanGeneration;
        else if (ReferenceEquals(gesture.Store, Arcs)) gesture.Generation = ++_arcGeneration;
        else throw new InvalidOperationException("Unknown two-anchor tool owner.");
    }
    private void InvalidateTwoAnchorGesture(TwoAnchorGesture gesture) =>
        InvalidateFrame(trendLineChanged: ReferenceEquals(gesture.Store, TrendLines),
            preserveTrendTool: ReferenceEquals(gesture.Store, TrendLines),
            retracementChanged: ReferenceEquals(gesture.Store, Retracements),
            preserveRetracementTool: ReferenceEquals(gesture.Store, Retracements),
            fanChanged: ReferenceEquals(gesture.Store, Fans), preserveFanTool: ReferenceEquals(gesture.Store, Fans),
            arcChanged: ReferenceEquals(gesture.Store, Arcs), preserveArcTool: ReferenceEquals(gesture.Store, Arcs));
    private bool CommitTwoAnchors(TwoAnchorGesture gesture, PriceTrendAnchor first, PriceTrendAnchor second)
    {
        if (!CanCaptureTwoAnchor(gesture, FrameRevision) || first.Hit.Key == second.Hit.Key ||
            first.Hit.Timestamp == second.Hit.Timestamp || first.Hit.DatasetVersion != second.Hit.DatasetVersion ||
            !string.Equals(first.Hit.SeriesId, second.Hit.SeriesId, StringComparison.Ordinal)) return false;
        // Each adapter projects against this exact original source before committing. This also
        // validates the retained half of an endpoint edit; no synthetic/nearest raw hit is used.
        if (gesture.Draft is PriceTrendLineDraft trendDraft && ReferenceEquals(gesture.Store, TrendLines))
        {
            var projection = PriceTrendGeometry.Project(new(trendDraft.LineId ?? 0, first, second)
                { ExtrapolateToInfinity = trendDraft.ExtrapolateToInfinity }, Snapshot!);
            if (projection.First is null || projection.Second is null ||
                projection.Status is not (PriceTrendProjectionStatus.Visible or PriceTrendProjectionStatus.OutsideViewport) ||
                !TrendLines.Commit(trendDraft, first, second)) return false;
            ResetTwoAnchorGestureCore(TrendLines); ++_trendLineGeneration;
            var revision = TrendLines.Revision;
            InvalidateFrame(trendLineChanged: true);
            return _attached && revision == TrendLines.Revision && TrendLines.Draft is null;
        }
        if (gesture.Draft is PriceFibonacciRetracementDraft retracementDraft && ReferenceEquals(gesture.Store, Retracements))
        {
            var projected = PriceFibonacciRetracementGeometry.Project(RetracementCandidate(retracementDraft, first, second), Snapshot!);
            if (projected.First is null || projected.Second is null ||
                projected.Status is not (PriceTrendProjectionStatus.Visible or PriceTrendProjectionStatus.OutsideViewport) ||
                !Retracements.Commit(retracementDraft, first, second)) return false;
            ResetTwoAnchorGestureCore(Retracements); ++_retracementGeneration;
            var revision = Retracements.Revision;
            InvalidateFrame(retracementChanged: true);
            return _attached && revision == Retracements.Revision && Retracements.Draft is null;
        }
        if (gesture.Draft is PriceFibonacciFanDraft fanDraft && ReferenceEquals(gesture.Store, Fans))
        {
            var projected = PriceFibonacciFanGeometry.Project(FanCandidate(fanDraft, first, second), Snapshot!);
            if (projected.First is null || projected.Second is null ||
                projected.Status is not (PriceTrendProjectionStatus.Visible or PriceTrendProjectionStatus.OutsideViewport) ||
                !Fans.Commit(fanDraft, first, second)) return false;
            ResetTwoAnchorGestureCore(Fans); ++_fanGeneration;
            var revision = Fans.Revision;
            InvalidateFrame(fanChanged: true);
            return _attached && revision == Fans.Revision && Fans.Draft is null;
        }
        if (gesture.Draft is PriceFibonacciArcDraft arcDraft && ReferenceEquals(gesture.Store, Arcs))
        {
            var projected = PriceFibonacciArcGeometry.Project(ArcCandidate(arcDraft, first, second), Snapshot!,
                PlotArea.Width, PlotArea.Height);
            if (projected.First is null || projected.Second is null ||
                projected.Status is not (PriceTrendProjectionStatus.Visible or PriceTrendProjectionStatus.OutsideViewport) ||
                !Arcs.Commit(arcDraft, first, second)) return false;
            ResetTwoAnchorGestureCore(Arcs); ++_arcGeneration;
            var revision = Arcs.Revision;
            InvalidateFrame(arcChanged: true);
            return _attached && revision == Arcs.Revision && Arcs.Draft is null;
        }
        return false;
    }
    private static PriceFibonacciArc ArcCandidate(PriceFibonacciArcDraft draft,
        PriceTrendAnchor first, PriceTrendAnchor second) => new(draft.ArcId ?? 0, first, second)
        { ShowLevel23_6 = draft.ShowLevel23_6, ShowLevel76_4 = draft.ShowLevel76_4, ShowLevel100 = draft.ShowLevel100 };
    private static PriceFibonacciFan FanCandidate(PriceFibonacciFanDraft draft,
        PriceTrendAnchor first, PriceTrendAnchor second) => new(draft.FanId ?? 0, first, second)
        { ShowLevel0 = draft.ShowLevel0, ShowLevel23_6 = draft.ShowLevel23_6, ShowLevel76_4 = draft.ShowLevel76_4 };
    private static PriceFibonacciRetracement RetracementCandidate(PriceFibonacciRetracementDraft draft,
        PriceTrendAnchor first, PriceTrendAnchor second) => new(draft.RetracementId ?? 0, first, second)
        { ShowLevel23_6 = draft.ShowLevel23_6, ShowLevel76_4 = draft.ShowLevel76_4, ShowAdditionalLevels = draft.ShowAdditionalLevels };
    public ImmutableArray<PriceTrendLineSegment> VisibleTrendLineSegments
    {
        get
        {
            if (!_attached || !IsPlotAvailable || Snapshot is not { } prices) return [];
            var builder = ImmutableArray.CreateBuilder<PriceTrendLineSegment>();
            foreach (var line in TrendLines.Items)
            {
                var projected = PriceTrendGeometry.Project(line, prices, AggregationSnapshot);
                if (projected.Segment is not { } segment) continue;
                builder.Add(new(line.Id, TrendScreenPoint(segment.Start.X, segment.Start.Y),
                    TrendScreenPoint(segment.End.X, segment.End.Y)));
            }
            return builder.ToImmutable();
        }
    }
    public int SuppressedTrendLineCount => TrendLines.Items.Length - VisibleTrendLineSegments.Length;
    private Point TrendScreenPoint(double x, double y) => new(PlotArea.X + x * PlotArea.Width, PlotArea.Y + y * PlotArea.Height);
    public string GetTrendLineStatus(long id)
    {
        var line = TrendLines.Items.FirstOrDefault(item => item.Id == id);
        if (line is null) return "Missing trend line";
        var state = Snapshot is null ? "NoSource" : !_attached ? "Detached" : !IsPlotAvailable ? "UnavailableLayout" :
            PriceTrendGeometry.Project(line, Snapshot, AggregationSnapshot).Status.ToString();
        return $"Trend {line.Id}: {state}. First {DescribeTrendAnchor(line.First)}. Second {DescribeTrendAnchor(line.Second)}. " +
            $"Original anchors retained; {DescribeTrendExtent(line.ExtrapolateToInfinity)}.";
    }
    private static string DescribeTrendExtent(bool value) => value
        ? "positive-X extension (+X) only; ExtrapolateToInfinity=true; never extends toward negative X"
        : "finite segment; ExtrapolateToInfinity=false";
    private static string DescribeTrendAnchor(PriceTrendAnchor anchor) =>
        $"{anchor.Hit.SeriesId}; key {anchor.Hit.Key}; UTC {anchor.Hit.Timestamp.ToUniversalTime():O}; " +
        $"{anchor.Level} {Raw(anchor.Value)}; source version {anchor.Hit.SourceVersion}; dataset version {anchor.Hit.DatasetVersion}";
    public string TrendLineStatus => $"Trend lines: {TrendLines.Items.Length}/{PriceTrendLines.MaximumLines}; " +
        $"{VisibleTrendLineSegments.Length} visible; {SuppressedTrendLineCount} suppressed. " +
        "Compatibility: two actual original OHLC points; finite by default, with optional positive-X extension only; false and first/second level Close are explicit compatibility defaults, not official defaults. " +
        $"New-line preference: {DescribeTrendExtent(ExtrapolateToInfinity)}; selected lines change only through their own extension control. " +
        "Aggregated display maps original X to constituent slots and retains original Y; unresolved or collapsed lines are suppressed, never reanchored. " +
        (AggregationSnapshot is not null ? "Return to Raw to create or edit endpoints. " : "") +
        (TrendLines.Draft is { } draft ? $"Current draft: {DescribeTrendExtent(draft.ExtrapolateToInfinity)}. " : "") +
        (TrendLines.Draft is not null ? _trendEndpointEdit != 0 ? $"Choose replacement endpoint {_trendEndpointEdit} on a raw candle. Escape cancels. " :
            _trendFirstAnchor is { } first ? $"First captured: {DescribeTrendAnchor(first)}. Drag/release or click a different raw candle for the second point. " :
            "Drag from one raw candle to another, click each candle, or use Left/Right then Enter for each endpoint. Escape cancels. " : "") +
        (TrendLines.SelectedId is { } selected ? GetTrendLineStatus(selected) : "No trend line selected.");
    private bool SelectTrendLineAt(Point point, long expectedRevision, bool beginEdit = false)
    {
        if (!_attached || expectedRevision != FrameRevision || !PlotArea.Contains(point)) return false;
        var segments = VisibleTrendLineSegments;
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var line = segments[i];
            if (TrendDistance(point, line.Start, line.End) > 6) continue;
            if (!beginEdit) return SelectTrendLine(line.Id);
            if (!CanAuthorTrendLine) return SelectTrendLine(line.Id);
            // Select and begin without an intermediate event; a newer callback must win.
            var draft = TrendLines.BeginEdit(line.Id);
            if (draft is null) return false;
            var textChanged = ClearTextAnnotationSelectionCore();
            var imageChanged = ClearImageAnnotationSelectionCore();
            var retracementChanged = ClearRetracementSelectionCore();
            StartTwoAnchorGesture(TrendLines, draft, ++_trendLineGeneration);
            InvalidateFrame(textChanged, imageChanged, trendLineChanged: true, preserveTrendTool: true,
                retracementChanged: retracementChanged);
            return _attached && ReferenceEquals(TrendLines.Draft, draft);
        }
        return false;
    }
    private static double TrendDistance(Point point, Point start, Point end)
    {
        var delta = end - start;
        var length = delta.X * delta.X + delta.Y * delta.Y;
        var offset = point - start;
        var t = length == 0 ? 0 : Math.Clamp((offset.X * delta.X + offset.Y * delta.Y) / length, 0, 1);
        var dx = point.X - start.X - t * delta.X;
        var dy = point.Y - start.Y - t * delta.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
    private void RenderTrendLines(DrawingContext context)
    {
        if (Snapshot is not { } prices) return;
        using (context.PushClip(PlotArea))
        {
            foreach (var segment in VisibleTrendLineSegments)
            {
                var selected = segment.Id == TrendLines.SelectedId;
                context.DrawLine(new Pen(selected ? Palette.Accent : Palette.Text, selected ? 2.5 : 1.5), segment.Start, segment.End);
                if (!selected) continue;
                var line = TrendLines.Items.First(item => item.Id == segment.Id);
                var projection = PriceTrendGeometry.Project(line, prices, AggregationSnapshot);
                DrawTrendEndpoint(context, line.First, projection.First?.DisplayIndex);
                DrawTrendEndpoint(context, line.Second, projection.Second?.DisplayIndex);
            }
            if (TrendLines.Draft is { } draft && _trendFirstAnchor is { } first)
            {
                if (_trendPreviewAnchor is { } second && first.Hit.Key != second.Hit.Key)
                {
                    var projected = PriceTrendGeometry.Project(new PriceTrendLine(0, first, second)
                        { ExtrapolateToInfinity = draft.ExtrapolateToInfinity }, prices);
                    if (projected.Segment is { } preview)
                        context.DrawLine(new Pen(Palette.Accent, 2, dashStyle: DashStyle.Dash),
                            TrendScreenPoint(preview.Start.X, preview.Start.Y), TrendScreenPoint(preview.End.X, preview.End.Y));
                    DrawTrendEndpoint(context, second, FindTrendOriginalIndex(second));
                }
                DrawTrendEndpoint(context, first, FindTrendOriginalIndex(first));
            }
        }
    }
    private int? FindTrendOriginalIndex(PriceTrendAnchor anchor)
    {
        if (Snapshot is not { } prices) return null;
        foreach (var candle in prices.Candles) if (candle.Hit.Key == anchor.Hit.Key) return candle.BarIndex;
        return null;
    }
    private void DrawTrendEndpoint(DrawingContext context, PriceTrendAnchor anchor, int? index)
    {
        if (index is not { } barIndex || Snapshot is not { Domain: { } domain, BarCount: > 0 } prices ||
            barIndex < prices.BarStart || barIndex >= prices.BarStart + prices.BarCount ||
            anchor.Value < domain.Minimum || anchor.Value > domain.Maximum) return;
        var x = prices.GetNormalizedX(barIndex);
        var y = DualAxisChartGeometry.NormalizeY(-anchor.Value, new HistoryValueDomain(-domain.Maximum, -domain.Minimum));
        context.DrawEllipse(Palette.Surface, new Pen(Palette.Accent, 2), TrendScreenPoint(x, y), 4, 4);
    }

    /// <summary>Separate bounded retracements. Trend IDs, counts and flags remain independent.</summary>
    public PriceFibonacciRetracements Retracements => _retracements;
    public long? SelectedRetracementId => Retracements.SelectedId;
    public bool IsRetracementToolActive => Retracements.Draft is not null;
    public bool IsRetracementPlacementArmed => IsRetracementToolActive;
    public long RetracementToolGeneration => _retracementGeneration;
    public PriceTrendAnchor? RetracementFirstAnchor => RetracementGesture?.First;
    public PriceTrendAnchor? RetracementPreviewAnchor => RetracementGesture?.Preview;
    public PriceTrendValueLevel RetracementFirstValueLevel
    {
        get => _retracementFirstValueLevel;
        set
        {
            Dispatcher.UIThread.VerifyAccess(); ValidateTrendLevel(value);
            if (_retracementFirstValueLevel == value) return;
            _retracementFirstValueLevel = value; ++_retracementGeneration;
            InvalidateFrame(retracementChanged: true);
        }
    }
    public PriceTrendValueLevel RetracementSecondValueLevel
    {
        get => _retracementSecondValueLevel;
        set
        {
            Dispatcher.UIThread.VerifyAccess(); ValidateTrendLevel(value);
            if (_retracementSecondValueLevel == value) return;
            _retracementSecondValueLevel = value; ++_retracementGeneration;
            InvalidateFrame(retracementChanged: true);
        }
    }
    public bool RetracementShowLevel23_6
    {
        get => _retracementShowLevel23_6;
        set => SetRetracementNewOptions(value, RetracementShowLevel76_4, RetracementShowAdditionalLevels);
    }
    public bool RetracementShowLevel76_4
    {
        get => _retracementShowLevel76_4;
        set => SetRetracementNewOptions(RetracementShowLevel23_6, value, RetracementShowAdditionalLevels);
    }
    public bool RetracementShowAdditionalLevels
    {
        get => _retracementShowAdditionalLevels;
        set => SetRetracementNewOptions(RetracementShowLevel23_6, RetracementShowLevel76_4, value);
    }
    /// <summary>New-object preferences only. One atomic change cancels pending authoring; identical values are inert.</summary>
    public bool SetRetracementNewOptions(bool showLevel23_6, bool showLevel76_4, bool showAdditionalLevels)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_retracementShowLevel23_6 == showLevel23_6 && _retracementShowLevel76_4 == showLevel76_4 &&
            _retracementShowAdditionalLevels == showAdditionalLevels) return true;
        _retracementShowLevel23_6 = showLevel23_6; _retracementShowLevel76_4 = showLevel76_4;
        _retracementShowAdditionalLevels = showAdditionalLevels;
        ++_retracementGeneration;
        InvalidateFrame(retracementChanged: true);
        return _retracementShowLevel23_6 == showLevel23_6 && _retracementShowLevel76_4 == showLevel76_4 &&
            _retracementShowAdditionalLevels == showAdditionalLevels;
    }
    /// <summary>Follows the settled common notification; reentrant newer state always wins.</summary>
    public event EventHandler? RetracementToolChanged;
    public bool BeginRetracementPlacement()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanAuthorTrendLine || Retracements.Items.Length >= PriceFibonacciRetracements.MaximumRetracements) return false;
        var draft = Retracements.BeginNew(RetracementShowLevel23_6, RetracementShowLevel76_4, RetracementShowAdditionalLevels);
        return draft is not null && BeginRetracementGesture(draft, 0);
    }
    public bool BeginSelectedRetracementEdit() => BeginRetracementEdit(0);
    public bool BeginSelectedRetracementEndpointEdit(bool firstEndpoint) => BeginRetracementEdit(firstEndpoint ? 1 : 2);
    private bool BeginRetracementEdit(int endpoint)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanAuthorTrendLine || Retracements.SelectedId is not { } id) return false;
        var draft = Retracements.BeginEdit(id);
        return draft is not null && BeginRetracementGesture(draft, endpoint);
    }
    private bool BeginRetracementGesture(PriceFibonacciRetracementDraft draft, int endpoint)
    {
        var textChanged = ClearTextAnnotationSelectionCore();
        var imageChanged = ClearImageAnnotationSelectionCore();
        var trendChanged = ClearTrendLineSelectionCore();
        StartTwoAnchorGesture(Retracements, draft, ++_retracementGeneration, endpoint);
        InvalidateFrame(textChanged, imageChanged, trendChanged, retracementChanged: true, preserveRetracementTool: true);
        return _attached && ReferenceEquals(Retracements.Draft, draft);
    }
    public void CancelRetracementTool()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (CancelRetracementToolCore()) InvalidateFrame(retracementChanged: true);
    }
    private bool CancelRetracementToolCore()
    {
        var changed = Retracements.Draft is { } draft && Retracements.Cancel(draft);
        changed |= ResetTwoAnchorGestureCore(Retracements);
        if (changed) ++_retracementGeneration;
        return changed;
    }
    public bool SelectRetracement(long id)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null || !Retracements.Select(id)) return false;
        var textChanged = ClearTextAnnotationSelectionCore();
        var imageChanged = ClearImageAnnotationSelectionCore();
        var trendChanged = ClearTrendLineSelectionCore();
        ResetTwoAnchorGestureCore(Retracements); ++_retracementGeneration;
        InvalidateFrame(textChanged, imageChanged, trendChanged, retracementChanged: true);
        return _attached && Retracements.SelectedId == id && Retracements.Draft is null;
    }
    public bool ClearRetracementSelection()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || !ClearRetracementSelectionCore()) return false;
        InvalidateFrame(retracementChanged: true);
        return _attached && Retracements.SelectedId is null && !IsRetracementToolActive;
    }
    private bool ClearRetracementSelectionCore()
    {
        var changed = Retracements.ClearSelection();
        changed |= ResetTwoAnchorGestureCore(Retracements);
        if (!changed) return false;
        ++_retracementGeneration; ++FrameRevision; _statusRevision = -1;
        _badge = null; _badgeRevision = -1; ClearCrosshairCore();
        return true;
    }
    public bool RemoveSelectedRetracement()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null) return false;
        var previous = Retracements.Revision;
        var removed = Retracements.RemoveSelected();
        ResetTwoAnchorGestureCore(Retracements);
        if (previous != Retracements.Revision)
        {
            ++_retracementGeneration;
            InvalidateFrame(retracementChanged: true);
        }
        return removed;
    }
    /// <summary>Changes only the exact selected object, not its anchors or the new-object preferences.</summary>
    public bool SetSelectedRetracementOptions(bool showLevel23_6, bool showLevel76_4, bool showAdditionalLevels)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null || IsAnyAnnotationToolActive || Retracements.SelectedId is not { } id) return false;
        var expected = Retracements.Items.FirstOrDefault(item => item.Id == id);
        if (expected is null) return false;
        var previous = Retracements.Revision;
        if (!Retracements.SetOptions(expected, showLevel23_6, showLevel76_4, showAdditionalLevels)) return false;
        if (previous == Retracements.Revision) return true;
        var updated = Retracements.Items.FirstOrDefault(item => item.Id == id);
        var revision = Retracements.Revision;
        var generation = ++_retracementGeneration;
        InvalidateFrame(retracementChanged: true);
        return _attached && generation == _retracementGeneration && revision == Retracements.Revision &&
            Retracements.SelectedId == id && !IsAnyAnnotationToolActive &&
            ReferenceEquals(updated, Retracements.Items.FirstOrDefault(item => item.Id == id));
    }
    public bool CaptureRetracementAnchor(int candleIndex, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        return CaptureTwoAnchorAtIndex(RetracementGesture, candleIndex, expectedRevision);
    }
    public bool CaptureRetracementAnchorAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        return RetracementGesture is { } gesture && FindTwoAnchorAt(gesture, point, expectedRevision) is { } anchor &&
            CaptureTwoAnchorCore(gesture, anchor);
    }
    public ImmutableArray<PriceRetracementLevel> VisibleRetracementLevels
    {
        get
        {
            if (!_attached || !IsPlotAvailable || Snapshot is not { } prices) return [];
            var builder = ImmutableArray.CreateBuilder<PriceRetracementLevel>();
            foreach (var retracement in Retracements.Items)
                foreach (var level in PriceFibonacciRetracementGeometry.Project(retracement, prices, AggregationSnapshot).Levels)
                    if (level.Segment is { } segment && level.Price is { } price)
                        builder.Add(new(retracement.Id, level.Ratio, price, TrendScreenPoint(segment.Start.X, segment.Start.Y),
                            TrendScreenPoint(segment.End.X, segment.End.Y)));
            return builder.ToImmutable();
        }
    }
    public int SuppressedRetracementCount => Retracements.Items.Length - VisibleRetracementLevels.Select(level => level.Id).Distinct().Count();
    public string GetRetracementStatus(long id)
    {
        var retracement = Retracements.Items.FirstOrDefault(item => item.Id == id);
        if (retracement is null) return "Missing retracement";
        var projection = Snapshot is { } prices ? PriceFibonacciRetracementGeometry.Project(retracement, prices, AggregationSnapshot) : null;
        var state = Snapshot is null ? "NoSource" : !_attached ? "Detached" : !IsPlotAvailable ? "UnavailableLayout" : projection!.Status.ToString();
        var levels = projection is null ? "No source for derived levels." : string.Join("; ", projection.Levels.Select(level =>
            $"{RetracementPercent(level.Ratio)} = {(level.Price is { } price ? Raw(price) : "not representable")} ({level.Status})"));
        return $"Retracement {id}: {state}. First (100%) {DescribeTrendAnchor(retracement.First)}. " +
            $"Second (0%) {DescribeTrendAnchor(retracement.Second)}. {DescribeRetracementOptions(retracement.ShowLevel23_6, retracement.ShowLevel76_4, retracement.ShowAdditionalLevels)}. " +
            $"Bounded horizontal span between anchors; original endpoints retained. Derived levels: {levels}";
    }
    private static string RetracementPercent(double ratio) => (ratio * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%";
    private static string DescribeRetracementOptions(bool level23, bool level76, bool additional) =>
        $"ShowLevel23_6={level23}; ShowLevel76_4={level76}; ShowAdditionalLevels={additional}";
    public string RetracementStatus => $"Retracements: {Retracements.Items.Length}/{PriceFibonacciRetracements.MaximumRetracements}; " +
        $"{VisibleRetracementLevels.Length} visible levels; {SuppressedRetracementCount} suppressed objects. " +
        "First anchor=100%, second=0%; fixed 0%, 38.2%, 50%, 61.8%, 100%; optional 23.6%, 76.4%, and 161.8%/261.8%/423.6% group. " +
        "Compatibility: original OHLC anchors, bounded horizontal span, no extension; Close/Close and all optional flags false are explicit Olivia choices, not official defaults. " +
        $"New-retracement preferences: {DescribeRetracementOptions(RetracementShowLevel23_6, RetracementShowLevel76_4, RetracementShowAdditionalLevels)}. " +
        "Aggregate display maps original X and retains original Y; unresolved or same-slot endpoints suppress all levels, never reanchor or expand the Y domain. " +
        (AggregationSnapshot is not null ? "Return to Raw to create or edit endpoints. " : "") +
        (Retracements.Draft is { } draft ? $"Current draft: {DescribeRetracementOptions(draft.ShowLevel23_6, draft.ShowLevel76_4, draft.ShowAdditionalLevels)}. " : "") +
        (RetracementGesture is { } gesture ? gesture.EndpointEdit != 0 ? $"Choose replacement endpoint {gesture.EndpointEdit} on a raw candle. Escape cancels. " :
            gesture.First is { } first ? $"First captured: {DescribeTrendAnchor(first)}. Drag/release or click a different raw candle for the second point. " :
            "Drag from one raw candle to another, click each candle, or use Left/Right then Enter. Escape cancels. " : "") +
        (Retracements.SelectedId is { } selected ? GetRetracementStatus(selected) : "No retracement selected.");
    private bool SelectRetracementAt(Point point, long expectedRevision, bool beginEdit = false)
    {
        if (!_attached || expectedRevision != FrameRevision || !PlotArea.Contains(point)) return false;
        var levels = VisibleRetracementLevels;
        for (var index = levels.Length - 1; index >= 0; index--)
        {
            var level = levels[index];
            if (TrendDistance(point, level.Start, level.End) > 6) continue;
            if (!beginEdit || !CanAuthorTrendLine) return SelectRetracement(level.Id);
            var draft = Retracements.BeginEdit(level.Id);
            return draft is not null && BeginRetracementGesture(draft, 0);
        }
        return false;
    }
    private void RenderRetracements(DrawingContext context)
    {
        if (Snapshot is not { } prices) return;
        using (context.PushClip(PlotArea))
        {
            var labelBounds = new List<Rect>();
            foreach (var level in VisibleRetracementLevels)
            {
                var selected = level.Id == Retracements.SelectedId;
                var brush = selected ? Palette.Accent : Palette.Text;
                context.DrawLine(new Pen(brush, selected ? 2 : 1), level.Start, level.End);
                var label = new FormattedText(RetracementPercent(level.Ratio), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 10, brush);
                var bounds = new Rect(Math.Min(level.Start.X, level.End.X) + 3, level.Start.Y - label.Height - 2, label.Width, label.Height);
                if (Math.Abs(level.End.X - level.Start.X) < label.Width + 6 || !PlotArea.Contains(bounds.TopLeft) ||
                    !PlotArea.Contains(bounds.BottomRight) || labelBounds.Any(previous => previous.Intersects(bounds))) continue;
                context.DrawText(label, bounds.TopLeft); labelBounds.Add(bounds);
            }
            if (Retracements.SelectedId is { } selectedId && Retracements.Items.FirstOrDefault(item => item.Id == selectedId) is { } selectedRetracement)
            {
                var projection = PriceFibonacciRetracementGeometry.Project(selectedRetracement, prices, AggregationSnapshot);
                if (projection.Status == PriceTrendProjectionStatus.Visible)
                {
                    DrawTrendEndpoint(context, selectedRetracement.First, projection.First?.DisplayIndex);
                    DrawTrendEndpoint(context, selectedRetracement.Second, projection.Second?.DisplayIndex);
                }
            }
            if (Retracements.Draft is { } draft && RetracementGesture is { First: { } first } gesture)
            {
                if (gesture.Preview is { } second && first.Hit.Key != second.Hit.Key)
                {
                    var projection = PriceFibonacciRetracementGeometry.Project(RetracementCandidate(draft, first, second), prices);
                    foreach (var level in projection.Levels)
                        if (level.Segment is { } segment)
                            context.DrawLine(new Pen(Palette.Accent, 1.5, dashStyle: DashStyle.Dash),
                                TrendScreenPoint(segment.Start.X, segment.Start.Y), TrendScreenPoint(segment.End.X, segment.End.Y));
                    DrawTrendEndpoint(context, second, FindTrendOriginalIndex(second));
                }
                DrawTrendEndpoint(context, first, FindTrendOriginalIndex(first));
            }
        }
    }

    /// <summary>Separate bounded fan store. Trend and retracement IDs, counts and flags remain independent.</summary>
    public PriceFibonacciFans Fans => _fans;
    public long? SelectedFanId => Fans.SelectedId;
    public bool IsFanToolActive => Fans.Draft is not null;
    public bool IsFanPlacementArmed => IsFanToolActive;
    public long FanToolGeneration => _fanGeneration;
    public PriceTrendAnchor? FanFirstAnchor => FanGesture?.First;
    public PriceTrendAnchor? FanPreviewAnchor => FanGesture?.Preview;
    public PriceTrendValueLevel FanFirstValueLevel
    {
        get => _fanFirstValueLevel;
        set
        {
            Dispatcher.UIThread.VerifyAccess(); ValidateTrendLevel(value);
            if (_fanFirstValueLevel == value) return;
            _fanFirstValueLevel = value; ++_fanGeneration;
            InvalidateFrame(fanChanged: true);
        }
    }
    public PriceTrendValueLevel FanSecondValueLevel
    {
        get => _fanSecondValueLevel;
        set
        {
            Dispatcher.UIThread.VerifyAccess(); ValidateTrendLevel(value);
            if (_fanSecondValueLevel == value) return;
            _fanSecondValueLevel = value; ++_fanGeneration;
            InvalidateFrame(fanChanged: true);
        }
    }
    public bool FanShowLevel0
    {
        get => _fanShowLevel0;
        set => SetFanNewOptions(value, FanShowLevel23_6, FanShowLevel76_4);
    }
    public bool FanShowLevel23_6
    {
        get => _fanShowLevel23_6;
        set => SetFanNewOptions(FanShowLevel0, value, FanShowLevel76_4);
    }
    public bool FanShowLevel76_4
    {
        get => _fanShowLevel76_4;
        set => SetFanNewOptions(FanShowLevel0, FanShowLevel23_6, value);
    }
    /// <summary>New-object preferences only. One atomic change cancels pending authoring; identical values are inert.</summary>
    public bool SetFanNewOptions(bool showLevel0, bool showLevel23_6, bool showLevel76_4)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_fanShowLevel0 == showLevel0 && _fanShowLevel23_6 == showLevel23_6 &&
            _fanShowLevel76_4 == showLevel76_4) return true;
        _fanShowLevel0 = showLevel0; _fanShowLevel23_6 = showLevel23_6;
        _fanShowLevel76_4 = showLevel76_4;
        ++_fanGeneration;
        InvalidateFrame(fanChanged: true);
        return _fanShowLevel0 == showLevel0 && _fanShowLevel23_6 == showLevel23_6 &&
            _fanShowLevel76_4 == showLevel76_4;
    }
    /// <summary>Follows the settled common notification; reentrant newer state always wins.</summary>
    public event EventHandler? FanToolChanged;
    public bool BeginFanPlacement()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanAuthorTrendLine || Fans.Items.Length >= PriceFibonacciFans.MaximumFans) return false;
        var draft = Fans.BeginNew(FanShowLevel0, FanShowLevel23_6, FanShowLevel76_4);
        return draft is not null && BeginFanGesture(draft, 0);
    }
    public bool BeginSelectedFanEdit() => BeginFanEdit(0);
    public bool BeginSelectedFanEndpointEdit(bool firstEndpoint) => BeginFanEdit(firstEndpoint ? 1 : 2);
    private bool BeginFanEdit(int endpoint)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanAuthorTrendLine || Fans.SelectedId is not { } id) return false;
        var draft = Fans.BeginEdit(id);
        return draft is not null && BeginFanGesture(draft, endpoint);
    }
    private bool BeginFanGesture(PriceFibonacciFanDraft draft, int endpoint)
    {
        var textChanged = ClearTextAnnotationSelectionCore();
        var imageChanged = ClearImageAnnotationSelectionCore();
        var trendChanged = ClearTrendLineSelectionCore();
        StartTwoAnchorGesture(Fans, draft, ++_fanGeneration, endpoint);
        InvalidateFrame(textChanged, imageChanged, trendChanged, fanChanged: true, preserveFanTool: true);
        return _attached && ReferenceEquals(Fans.Draft, draft);
    }
    public void CancelFanTool()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (CancelFanToolCore()) InvalidateFrame(fanChanged: true);
    }
    private bool CancelFanToolCore()
    {
        var changed = Fans.Draft is { } draft && Fans.Cancel(draft);
        changed |= ResetTwoAnchorGestureCore(Fans);
        if (changed) ++_fanGeneration;
        return changed;
    }
    public bool SelectFan(long id)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null || !Fans.Select(id)) return false;
        var textChanged = ClearTextAnnotationSelectionCore();
        var imageChanged = ClearImageAnnotationSelectionCore();
        var trendChanged = ClearTrendLineSelectionCore();
        ResetTwoAnchorGestureCore(Fans); ++_fanGeneration;
        InvalidateFrame(textChanged, imageChanged, trendChanged, fanChanged: true);
        return _attached && Fans.SelectedId == id && Fans.Draft is null;
    }
    public bool ClearFanSelection()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || !ClearFanSelectionCore()) return false;
        InvalidateFrame(fanChanged: true);
        return _attached && Fans.SelectedId is null && !IsFanToolActive;
    }
    private bool ClearFanSelectionCore()
    {
        var changed = Fans.ClearSelection();
        changed |= ResetTwoAnchorGestureCore(Fans);
        if (!changed) return false;
        ++_fanGeneration; ++FrameRevision; _statusRevision = -1;
        _badge = null; _badgeRevision = -1; ClearCrosshairCore();
        return true;
    }
    public bool RemoveSelectedFan()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null) return false;
        var previous = Fans.Revision;
        var removed = Fans.RemoveSelected();
        ResetTwoAnchorGestureCore(Fans);
        if (previous != Fans.Revision)
        {
            ++_fanGeneration;
            InvalidateFrame(fanChanged: true);
        }
        return removed;
    }
    /// <summary>Changes only the exact selected object, not its anchors or the new-object preferences.</summary>
    public bool SetSelectedFanOptions(bool showLevel0, bool showLevel23_6, bool showLevel76_4)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null || IsAnyAnnotationToolActive || Fans.SelectedId is not { } id) return false;
        var expected = Fans.Items.FirstOrDefault(item => item.Id == id);
        if (expected is null) return false;
        var previous = Fans.Revision;
        if (!Fans.SetOptions(expected, showLevel0, showLevel23_6, showLevel76_4)) return false;
        if (previous == Fans.Revision) return true;
        var updated = Fans.Items.FirstOrDefault(item => item.Id == id);
        var revision = Fans.Revision;
        var generation = ++_fanGeneration;
        InvalidateFrame(fanChanged: true);
        return _attached && generation == _fanGeneration && revision == Fans.Revision &&
            Fans.SelectedId == id && !IsAnyAnnotationToolActive &&
            ReferenceEquals(updated, Fans.Items.FirstOrDefault(item => item.Id == id));
    }
    public bool CaptureFanAnchor(int candleIndex, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        return CaptureTwoAnchorAtIndex(FanGesture, candleIndex, expectedRevision);
    }
    public bool CaptureFanAnchorAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        return FanGesture is { } gesture && FindTwoAnchorAt(gesture, point, expectedRevision) is { } anchor &&
            CaptureTwoAnchorCore(gesture, anchor);
    }
    public ImmutableArray<PriceFanLevel> VisibleFanLevels
    {
        get
        {
            if (!_attached || !IsPlotAvailable || Snapshot is not { } prices) return [];
            var builder = ImmutableArray.CreateBuilder<PriceFanLevel>();
            foreach (var fan in Fans.Items)
                foreach (var level in PriceFibonacciFanGeometry.Project(fan, prices, AggregationSnapshot).Levels)
                    if (level.Segment is { } segment)
                        builder.Add(new(fan.Id, level.Ratio, level.SecondPrice, TrendScreenPoint(segment.Start.X, segment.Start.Y),
                            TrendScreenPoint(segment.End.X, segment.End.Y)));
            return builder.ToImmutable();
        }
    }
    public int SuppressedFanCount => Fans.Items.Length - VisibleFanLevels.Select(level => level.Id).Distinct().Count();
    public string GetFanStatus(long id)
    {
        var fan = Fans.Items.FirstOrDefault(item => item.Id == id);
        if (fan is null) return "Missing fan";
        var projection = Snapshot is { } prices ? PriceFibonacciFanGeometry.Project(fan, prices, AggregationSnapshot) : null;
        var state = Snapshot is null ? "NoSource" : !_attached ? "Detached" : !IsPlotAvailable ? "UnavailableLayout" : projection!.Status.ToString();
        var levels = projection is null ? "No source for derived rays." : string.Join("; ", projection.Levels.Select(level =>
            $"{FanPercent(level.Ratio)}: second-X price {(level.SecondPrice is { } price ? Raw(price) : "not representable")} ({level.Status})"));
        return $"Fan {id}: {state}. First (origin) {DescribeTrendAnchor(fan.First)}. " +
            $"Second (0% line anchor) {DescribeTrendAnchor(fan.Second)}. {DescribeFanOptions(fan.ShowLevel0, fan.ShowLevel23_6, fan.ShowLevel76_4)}. " +
            "Original endpoints retained. Rays start at the first anchor and extend through the derived second-X point in the authored X direction; " +
            $"leftward when the second anchor is earlier, never bidirectional. Derived ray reference values: {levels}";
    }
    private static string FanPercent(double ratio) => (ratio * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%";
    private static string DescribeFanOptions(bool level0, bool level23, bool level76) =>
        $"ShowLevel0={level0}; ShowLevel23_6={level23}; ShowLevel76_4={level76}";
    public string FanStatus => $"Fans: {Fans.Items.Length}/{PriceFibonacciFans.MaximumFans}; " +
        $"{VisibleFanLevels.Length} visible rays; {SuppressedFanCount} suppressed objects. " +
        "First anchor=origin, second defines the 0% line; fixed 38.2%, 50%, 61.8%; optional 0%, 23.6%, 76.4%. " +
        "Compatibility: Q_r=(x2, y2+r*(y1-y2)); ray origin+t*(Q_r-origin), t>=0, clipped in both X and Y. " +
        "Rays extend in the authored X direction, including leftward; not trend-line positive-X extension and not bidirectional. " +
        "This formula, extent, Close/Close and all optional flags false are explicit Olivia choices, not WPF runtime/default parity. " +
        $"New-fan preferences: {DescribeFanOptions(FanShowLevel0, FanShowLevel23_6, FanShowLevel76_4)}. " +
        "Aggregate display maps original X and retains original Y; unresolved or same-slot endpoints suppress all rays, never reanchor or expand the Y domain. " +
        (AggregationSnapshot is not null ? "Return to Raw to create or edit endpoints. " : "") +
        (Fans.Draft is { } draft ? $"Current draft: {DescribeFanOptions(draft.ShowLevel0, draft.ShowLevel23_6, draft.ShowLevel76_4)}. " : "") +
        (FanGesture is { } gesture ? gesture.EndpointEdit != 0 ? $"Choose replacement endpoint {gesture.EndpointEdit} on a raw candle. Escape cancels. " :
            gesture.First is { } first ? $"Origin captured: {DescribeTrendAnchor(first)}. Drag/release or click a different raw candle for the second point. " :
            "Drag from one raw candle to another, click each candle, or use Left/Right then Enter. Escape cancels. " : "") +
        (Fans.SelectedId is { } selected ? GetFanStatus(selected) : "No fan selected.");
    private bool SelectFanAt(Point point, long expectedRevision, bool beginEdit = false)
    {
        if (!_attached || expectedRevision != FrameRevision || !PlotArea.Contains(point)) return false;
        var levels = VisibleFanLevels;
        for (var index = levels.Length - 1; index >= 0; index--)
        {
            var level = levels[index];
            if (TrendDistance(point, level.Start, level.End) > 6) continue;
            if (!beginEdit || !CanAuthorTrendLine) return SelectFan(level.Id);
            var draft = Fans.BeginEdit(level.Id);
            return draft is not null && BeginFanGesture(draft, 0);
        }
        return false;
    }
    private void RenderFans(DrawingContext context)
    {
        if (Snapshot is not { } prices) return;
        using (context.PushClip(PlotArea))
        {
            var labelBounds = new List<Rect>();
            foreach (var level in VisibleFanLevels)
            {
                var selected = level.Id == Fans.SelectedId;
                var brush = selected ? Palette.Accent : Palette.Text;
                context.DrawLine(new Pen(brush, selected ? 2 : 1), level.Start, level.End);
                var label = new FormattedText(FanPercent(level.Ratio), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 10, brush);
                // Labels are complete percentages, positioned near the clipped ray's far end.
                // Insufficient space or collisions suppress only the label, never the exact ray.
                var area = PlotArea;
                if (!double.IsFinite(label.Width) || !double.IsFinite(label.Height) ||
                    label.Width + 6 > area.Width || label.Height + 6 > area.Height ||
                    TrendDistance(level.Start, level.End, level.End) < Math.Max(label.Width, label.Height) + 8) continue;
                var point = level.Start + (level.End - level.Start) * .85;
                var bounds = new Rect(Math.Clamp(point.X - label.Width / 2, area.Left + 3, area.Right - label.Width - 3),
                    Math.Clamp(point.Y - label.Height - 3, area.Top + 3, area.Bottom - label.Height - 3), label.Width, label.Height);
                if (labelBounds.Any(previous => previous.Intersects(bounds))) continue;
                context.DrawText(label, bounds.TopLeft); labelBounds.Add(bounds);
            }
            if (Fans.SelectedId is { } selectedId && Fans.Items.FirstOrDefault(item => item.Id == selectedId) is { } selectedFan)
            {
                var projection = PriceFibonacciFanGeometry.Project(selectedFan, prices, AggregationSnapshot);
                if (projection.Status == PriceTrendProjectionStatus.Visible)
                {
                    DrawTrendEndpoint(context, selectedFan.First, projection.First?.DisplayIndex);
                    DrawTrendEndpoint(context, selectedFan.Second, projection.Second?.DisplayIndex);
                }
            }
            if (Fans.Draft is { } draft && FanGesture is { First: { } first } gesture)
            {
                if (gesture.Preview is { } second && first.Hit.Key != second.Hit.Key)
                {
                    var projection = PriceFibonacciFanGeometry.Project(FanCandidate(draft, first, second), prices);
                    foreach (var level in projection.Levels)
                        if (level.Segment is { } segment)
                            context.DrawLine(new Pen(Palette.Accent, 1.5, dashStyle: DashStyle.Dash),
                                TrendScreenPoint(segment.Start.X, segment.Start.Y), TrendScreenPoint(segment.End.X, segment.End.Y));
                    DrawTrendEndpoint(context, second, FindTrendOriginalIndex(second));
                }
                DrawTrendEndpoint(context, first, FindTrendOriginalIndex(first));
            }
        }
    }

    /// <summary>Separate bounded arc store. Trend, retracement and fan IDs, counts and flags remain independent.</summary>
    public PriceFibonacciArcs Arcs => _arcs;
    public long? SelectedArcId => Arcs.SelectedId;
    public bool IsArcToolActive => Arcs.Draft is not null;
    public bool IsArcPlacementArmed => IsArcToolActive;
    public long ArcToolGeneration => _arcGeneration;
    public PriceTrendAnchor? ArcFirstAnchor => ArcGesture?.First;
    public PriceTrendAnchor? ArcPreviewAnchor => ArcGesture?.Preview;
    public PriceTrendValueLevel ArcFirstValueLevel
    {
        get => _arcFirstValueLevel;
        set
        {
            Dispatcher.UIThread.VerifyAccess(); ValidateTrendLevel(value);
            if (_arcFirstValueLevel == value) return;
            _arcFirstValueLevel = value; ++_arcGeneration;
            InvalidateFrame(arcChanged: true);
        }
    }
    public PriceTrendValueLevel ArcSecondValueLevel
    {
        get => _arcSecondValueLevel;
        set
        {
            Dispatcher.UIThread.VerifyAccess(); ValidateTrendLevel(value);
            if (_arcSecondValueLevel == value) return;
            _arcSecondValueLevel = value; ++_arcGeneration;
            InvalidateFrame(arcChanged: true);
        }
    }
    public bool ArcShowLevel23_6
    {
        get => _arcShowLevel23_6;
        set => SetArcNewOptions(value, ArcShowLevel76_4, ArcShowLevel100);
    }
    public bool ArcShowLevel76_4
    {
        get => _arcShowLevel76_4;
        set => SetArcNewOptions(ArcShowLevel23_6, value, ArcShowLevel100);
    }
    public bool ArcShowLevel100
    {
        get => _arcShowLevel100;
        set => SetArcNewOptions(ArcShowLevel23_6, ArcShowLevel76_4, value);
    }
    /// <summary>New-object preferences only. One atomic change cancels pending authoring; identical values are inert.</summary>
    public bool SetArcNewOptions(bool showLevel23_6, bool showLevel76_4, bool showLevel100)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_arcShowLevel23_6 == showLevel23_6 && _arcShowLevel76_4 == showLevel76_4 &&
            _arcShowLevel100 == showLevel100) return true;
        _arcShowLevel23_6 = showLevel23_6; _arcShowLevel76_4 = showLevel76_4;
        _arcShowLevel100 = showLevel100;
        ++_arcGeneration;
        InvalidateFrame(arcChanged: true);
        return _arcShowLevel23_6 == showLevel23_6 && _arcShowLevel76_4 == showLevel76_4 &&
            _arcShowLevel100 == showLevel100;
    }
    /// <summary>Follows the settled common notification; reentrant newer state always wins.</summary>
    public event EventHandler? ArcToolChanged;
    public bool BeginArcPlacement()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanAuthorTrendLine || Arcs.Items.Length >= PriceFibonacciArcs.MaximumArcs) return false;
        var draft = Arcs.BeginNew(ArcShowLevel23_6, ArcShowLevel76_4, ArcShowLevel100);
        return draft is not null && BeginArcGesture(draft, 0);
    }
    public bool BeginSelectedArcEdit() => BeginArcEdit(0);
    public bool BeginSelectedArcEndpointEdit(bool firstEndpoint) => BeginArcEdit(firstEndpoint ? 1 : 2);
    private bool BeginArcEdit(int endpoint)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanAuthorTrendLine || Arcs.SelectedId is not { } id) return false;
        var draft = Arcs.BeginEdit(id);
        return draft is not null && BeginArcGesture(draft, endpoint);
    }
    private bool BeginArcGesture(PriceFibonacciArcDraft draft, int endpoint)
    {
        var textChanged = ClearTextAnnotationSelectionCore();
        var imageChanged = ClearImageAnnotationSelectionCore();
        var trendChanged = ClearTrendLineSelectionCore();
        StartTwoAnchorGesture(Arcs, draft, ++_arcGeneration, endpoint);
        InvalidateFrame(textChanged, imageChanged, trendChanged, arcChanged: true, preserveArcTool: true);
        return _attached && ReferenceEquals(Arcs.Draft, draft);
    }
    public void CancelArcTool()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (CancelArcToolCore()) InvalidateFrame(arcChanged: true);
    }
    private bool CancelArcToolCore()
    {
        var changed = Arcs.Draft is { } draft && Arcs.Cancel(draft);
        changed |= ResetTwoAnchorGestureCore(Arcs);
        if (changed) ++_arcGeneration;
        return changed;
    }
    public bool SelectArc(long id)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null || !Arcs.Select(id)) return false;
        var textChanged = ClearTextAnnotationSelectionCore();
        var imageChanged = ClearImageAnnotationSelectionCore();
        var trendChanged = ClearTrendLineSelectionCore();
        ResetTwoAnchorGestureCore(Arcs); ++_arcGeneration;
        InvalidateFrame(textChanged, imageChanged, trendChanged, arcChanged: true);
        return _attached && Arcs.SelectedId == id && Arcs.Draft is null;
    }
    public bool ClearArcSelection()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || !ClearArcSelectionCore()) return false;
        InvalidateFrame(arcChanged: true);
        return _attached && Arcs.SelectedId is null && !IsArcToolActive;
    }
    private bool ClearArcSelectionCore()
    {
        var changed = Arcs.ClearSelection();
        changed |= ResetTwoAnchorGestureCore(Arcs);
        if (!changed) return false;
        ++_arcGeneration; ++FrameRevision; _statusRevision = -1;
        _badge = null; _badgeRevision = -1; ClearCrosshairCore();
        return true;
    }
    public bool RemoveSelectedArc()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null) return false;
        var previous = Arcs.Revision;
        var removed = Arcs.RemoveSelected();
        ResetTwoAnchorGestureCore(Arcs);
        if (previous != Arcs.Revision)
        {
            ++_arcGeneration;
            InvalidateFrame(arcChanged: true);
        }
        return removed;
    }
    /// <summary>Changes only the exact selected object, not its anchors or the new-object preferences.</summary>
    public bool SetSelectedArcOptions(bool showLevel23_6, bool showLevel76_4, bool showLevel100)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null || IsAnyAnnotationToolActive || Arcs.SelectedId is not { } id) return false;
        var expected = Arcs.Items.FirstOrDefault(item => item.Id == id);
        if (expected is null) return false;
        var previous = Arcs.Revision;
        if (!Arcs.SetOptions(expected, showLevel23_6, showLevel76_4, showLevel100)) return false;
        if (previous == Arcs.Revision) return true;
        var updated = Arcs.Items.FirstOrDefault(item => item.Id == id);
        var revision = Arcs.Revision;
        var generation = ++_arcGeneration;
        InvalidateFrame(arcChanged: true);
        return _attached && generation == _arcGeneration && revision == Arcs.Revision &&
            Arcs.SelectedId == id && !IsAnyAnnotationToolActive &&
            ReferenceEquals(updated, Arcs.Items.FirstOrDefault(item => item.Id == id));
    }
    public bool CaptureArcAnchor(int candleIndex, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        return CaptureTwoAnchorAtIndex(ArcGesture, candleIndex, expectedRevision);
    }
    public bool CaptureArcAnchorAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        return ArcGesture is { } gesture && FindTwoAnchorAt(gesture, point, expectedRevision) is { } anchor &&
            CaptureTwoAnchorCore(gesture, anchor);
    }
    /// <summary>Exact currently rendered geometry, shared by rendering, hit testing and readback.</summary>
    public ImmutableArray<PriceArcLevel> VisibleArcLevels
    { get { EnsureArcLayout(); return _visibleArcLevels; } }
    /// <summary>All enabled levels, including explicit geometry refusals; null without an available attached plot.</summary>
    public PriceFibonacciArcProjection? GetArcProjection(long id)
    {
        EnsureArcLayout();
        return _arcProjections.GetValueOrDefault(id);
    }
    private void ClearArcLayout()
    {
        _arcLayoutFrameRevision = -1; _arcLayoutStoreRevision = -1;
        _arcLayoutSnapshot = null; _arcLayoutAggregation = null;
        _arcProjections = ImmutableDictionary<long, PriceFibonacciArcProjection>.Empty;
        _visibleArcLevels = [];
    }
    private void EnsureArcLayout()
    {
        var prices = Snapshot;
        var aggregation = AggregationSnapshot;
        var area = PlotArea;
        var storeRevision = Arcs.Revision;
        if (_arcLayoutFrameRevision == FrameRevision && _arcLayoutStoreRevision == storeRevision &&
            _arcLayoutArea == area && _arcLayoutAttached == _attached && _arcLayoutAvailable == IsPlotAvailable &&
            ReferenceEquals(_arcLayoutSnapshot, prices) && ReferenceEquals(_arcLayoutAggregation, aggregation)) return;
        _arcLayoutFrameRevision = FrameRevision; _arcLayoutStoreRevision = storeRevision;
        _arcLayoutArea = area; _arcLayoutAttached = _attached; _arcLayoutAvailable = IsPlotAvailable;
        _arcLayoutSnapshot = prices; _arcLayoutAggregation = aggregation;
        _arcProjections = ImmutableDictionary<long, PriceFibonacciArcProjection>.Empty;
        _visibleArcLevels = [];
        if (!_attached || !IsPlotAvailable || prices is null) return;
        var projections = ImmutableDictionary.CreateBuilder<long, PriceFibonacciArcProjection>();
        var levels = ImmutableArray.CreateBuilder<PriceArcLevel>();
        foreach (var arc in Arcs.Items)
        {
            var projection = PriceFibonacciArcGeometry.Project(arc, prices, area.Width, area.Height, aggregation);
            projections.Add(arc.Id, projection);
            foreach (var level in projection.Levels)
            {
                if (level.Status != PriceFibonacciArcLevelStatus.Visible || level.RadiusDips is not { } radius ||
                    level.Segments.IsDefaultOrEmpty) continue;
                var segments = level.Segments.Select(segment => new PriceCrosshairLine(
                    TrendScreenPoint(segment.Start.X, segment.Start.Y),
                    TrendScreenPoint(segment.End.X, segment.End.Y))).ToImmutableArray();
                levels.Add(new(arc.Id, level.Ratio, radius, segments));
            }
        }
        _arcProjections = projections.ToImmutable();
        _visibleArcLevels = levels.ToImmutable();
    }
    public int SuppressedArcCount => Arcs.Items.Length - VisibleArcLevels.Select(level => level.Id).Distinct().Count();
    public string GetArcStatus(long id)
    {
        var arc = Arcs.Items.FirstOrDefault(item => item.Id == id);
        if (arc is null) return "Missing arc";
        var projection = GetArcProjection(id);
        var state = Snapshot is null ? "NoSource" : !_attached ? "Detached" : !IsPlotAvailable ? "UnavailableLayout" :
            projection?.Status.ToString() ?? "UnavailableProjection";
        var levels = projection is null ? "No available plot for radius projection." : string.Join("; ", projection.Levels.Select(level =>
            $"{ArcPercent(level.Ratio)}: radius {(level.RadiusDips is { } radius ? Raw(radius) + " DIPs" : "not representable")} ({level.Status})"));
        var issue = projection is { GeometryIssue: not PriceFibonacciArcGeometryIssue.None }
            ? $" Geometry issue: {projection.GeometryIssue}." : "";
        return $"Arc {id}: {state}.{issue} First (center) {DescribeTrendAnchor(arc.First)}. " +
            $"Second (100% radius reference) {DescribeTrendAnchor(arc.Second)}. {DescribeArcOptions(arc.ShowLevel23_6, arc.ShowLevel76_4, arc.ShowLevel100)}. " +
            "Original endpoints retained. Complete 360-degree circles; radius is the level ratio times the Euclidean " +
            $"distance between the unclamped projected anchors in plot DIPs, never mixed ordinal/price units. Derived radius values: {levels}";
    }
    private static string ArcPercent(double ratio) => (ratio * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%";
    private static string DescribeArcOptions(bool level23, bool level76, bool level100) =>
        $"ShowLevel23_6={level23}; ShowLevel76_4={level76}; ShowLevel100={level100}";
    public string ArcStatus => $"Arcs: {Arcs.Items.Length}/{PriceFibonacciArcs.MaximumArcs}; " +
        $"{VisibleArcLevels.Length} visible levels; {SuppressedArcCount} suppressed objects. " +
        "First anchor=center, second defines the 100% radius; fixed 38.2%, 50%, 61.8%; optional 23.6%, 76.4%, 100%. " +
        "Complete 360-degree circles use Euclidean distance in current plot DIPs; levels are XY-clipped bounded polylines, never straight-line or ellipse fallbacks. " +
        $"Maximum chord error {Raw(PriceFibonacciArcGeometry.MaximumChordErrorDips)} DIP; at most {PriceFibonacciArcGeometry.MaximumSegmentsPerLevel} chords per level; numeric/budget failures are explicitly suppressed. " +
        "Full sweep, plot-DIP metric, Close/Close and all optional flags false are explicit Olivia compatibility choices, not WPF runtime/default parity. " +
        $"New-arc preferences: {DescribeArcOptions(ArcShowLevel23_6, ArcShowLevel76_4, ArcShowLevel100)}. " +
        "Aggregate display maps original X and retains original Y; unresolved or same-slot endpoints suppress all levels, never reanchor or expand the Y domain. " +
        (AggregationSnapshot is not null ? "Return to Raw to create or edit endpoints. " : "") +
        (Arcs.Draft is { } draft ? $"Current draft: {DescribeArcOptions(draft.ShowLevel23_6, draft.ShowLevel76_4, draft.ShowLevel100)}. " : "") +
        (ArcGesture is { } gesture ? gesture.EndpointEdit != 0 ? $"Choose replacement endpoint {gesture.EndpointEdit} on a raw candle. Escape cancels. " :
            gesture.First is { } first ? $"Center captured: {DescribeTrendAnchor(first)}. Drag/release or click a different raw candle for the radius reference. " :
            "Drag from one raw candle to another, click each candle, or use Left/Right then Enter. Escape cancels. " : "") +
        (Arcs.SelectedId is { } selected ? GetArcStatus(selected) : "No arc selected.");
    private bool SelectArcAt(Point point, long expectedRevision, bool beginEdit = false)
    {
        if (!_attached || expectedRevision != FrameRevision || !PlotArea.Contains(point)) return false;
        var levels = VisibleArcLevels;
        for (var index = levels.Length - 1; index >= 0; index--)
        {
            var level = levels[index];
            // Test exactly the clipped chords consumed by RenderArcs. Never bridge clipped gaps,
            // test a diameter/radius line, or substitute a separate analytic curve.
            if (!level.Segments.Any(segment => TrendDistance(point, segment.Start, segment.End) <= 6)) continue;
            if (!beginEdit || !CanAuthorTrendLine) return SelectArc(level.Id);
            var draft = Arcs.BeginEdit(level.Id);
            return draft is not null && BeginArcGesture(draft, 0);
        }
        return false;
    }
    private void RenderArcs(DrawingContext context)
    {
        if (Snapshot is not { } prices || !IsPlotAvailable) return;
        var area = PlotArea;
        using (context.PushClip(area))
        {
            var labelBounds = new List<Rect>();
            foreach (var level in VisibleArcLevels)
            {
                var selected = level.Id == Arcs.SelectedId;
                var brush = selected ? Palette.Accent : Palette.Text;
                var pen = new Pen(brush, selected ? 2 : 1);
                foreach (var segment in level.Segments) context.DrawLine(pen, segment.Start, segment.End);
                var label = new FormattedText(ArcPercent(level.Ratio), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 10, brush);
                // Complete percentages fit inside the main pane. Collisions suppress only labels.
                if (!double.IsFinite(label.Width) || !double.IsFinite(label.Height) ||
                    label.Width + 6 > area.Width || label.Height + 6 > area.Height ||
                    level.Segments.Sum(segment => TrendDistance(segment.Start, segment.End, segment.End)) <
                    Math.Max(label.Width, label.Height) + 8) continue;
                var middle = level.Segments[level.Segments.Length / 2];
                var point = middle.Start + (middle.End - middle.Start) * .5;
                var bounds = new Rect(Math.Clamp(point.X - label.Width / 2, area.Left + 3, area.Right - label.Width - 3),
                    Math.Clamp(point.Y - label.Height - 3, area.Top + 3, area.Bottom - label.Height - 3), label.Width, label.Height);
                if (labelBounds.Any(previous => previous.Intersects(bounds))) continue;
                context.DrawText(label, bounds.TopLeft); labelBounds.Add(bounds);
            }
            if (Arcs.SelectedId is { } selectedId && Arcs.Items.FirstOrDefault(item => item.Id == selectedId) is { } selectedArc)
            {
                var projection = GetArcProjection(selectedId);
                if (projection?.Status == PriceTrendProjectionStatus.Visible)
                {
                    DrawTrendEndpoint(context, selectedArc.First, projection.First?.DisplayIndex);
                    DrawTrendEndpoint(context, selectedArc.Second, projection.Second?.DisplayIndex);
                }
            }
            if (Arcs.Draft is { } draft && ArcGesture is { First: { } first } gesture)
            {
                if (gesture.Preview is { } second && first.Hit.Key != second.Hit.Key)
                {
                    var projection = PriceFibonacciArcGeometry.Project(ArcCandidate(draft, first, second), prices, area.Width, area.Height);
                    if (projection.Status == PriceTrendProjectionStatus.Visible)
                    {
                        var pen = new Pen(Palette.Accent, 1.5, dashStyle: DashStyle.Dash);
                        foreach (var level in projection.Levels)
                            foreach (var segment in level.Segments)
                                context.DrawLine(pen, TrendScreenPoint(segment.Start.X, segment.Start.Y),
                                    TrendScreenPoint(segment.End.X, segment.End.Y));
                        DrawTrendEndpoint(context, first, projection.First?.DisplayIndex);
                        DrawTrendEndpoint(context, second, projection.Second?.DisplayIndex);
                    }
                }
                else
                {
                    // A single pending center has no curve yet. Show only its exact current raw anchor,
                    // never a nearest or clamped point masquerading as a valid two-anchor projection.
                    var index = FindTrendOriginalIndex(first);
                    if (index is { } originalIndex && PriceTrendGeometry.CreateAnchor(prices, originalIndex, first.Level) is { } current &&
                        current.Hit == first.Hit && BitConverter.DoubleToInt64Bits(current.Value) == BitConverter.DoubleToInt64Bits(first.Value))
                        DrawTrendEndpoint(context, first, originalIndex);
                }
            }
        }
    }

    public bool BeginTextAnnotationPlacement()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null || !IsPlotAvailable ||
            TextAnnotations.Items.Length >= PriceTextAnnotations.MaximumNotes) return false;
        if (_textAnnotationPlacementArmed && TextAnnotations.Draft is null &&
            !IsImageAnnotationToolActive && ImageAnnotations.SelectedId is null &&
            !IsTrendLineToolActive && SelectedTrendLineId is null && !IsRetracementToolActive && SelectedRetracementId is null &&
            !IsFanToolActive && SelectedFanId is null && !IsArcToolActive && SelectedArcId is null) return true;
        var imageChanged = ClearImageAnnotationSelectionCore();
        TextAnnotations.ClearSelection();
        _textAnnotationPlacementArmed = true;
        var generation = ++_textAnnotationGeneration;
        InvalidateFrame(textAnnotationChanged: true, imageAnnotationChanged: imageChanged);
        return _attached && _textAnnotationPlacementArmed && generation == _textAnnotationGeneration;
    }
    /// <summary>Places a new draft only for an armed tool and the exact current pointer frame.</summary>
    public bool BeginTextAnnotationAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || !_textAnnotationPlacementArmed ||
            Snapshot is null || !IsPlotAvailable || !double.IsFinite(point.X) ||
            !double.IsFinite(point.Y) || !PlotArea.Contains(point)) return false;
        var draft = TextAnnotations.BeginNew(point.X, point.Y);
        if (draft is null) return false;
        var imageChanged = ClearImageAnnotationSelectionCore();
        _textAnnotationPlacementArmed = false;
        ++_textAnnotationGeneration;
        InvalidateFrame(textAnnotationChanged: true, imageAnnotationChanged: imageChanged);
        return _attached && ReferenceEquals(TextAnnotations.Draft, draft);
    }
    public bool BeginSelectedTextAnnotationEdit()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null || TextAnnotations.SelectedId is not { } id) return false;
        var draft = TextAnnotations.BeginEdit(id);
        if (draft is null) return false;
        var imageChanged = ClearImageAnnotationSelectionCore();
        _textAnnotationPlacementArmed = false;
        ++_textAnnotationGeneration;
        InvalidateFrame(textAnnotationChanged: true, imageAnnotationChanged: imageChanged);
        return _attached && ReferenceEquals(TextAnnotations.Draft, draft);
    }
    public bool CommitTextAnnotation(PriceTextAnnotationDraft draft, string text)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null || !TextAnnotations.Commit(draft, text)) return false;
        var imageChanged = ClearImageAnnotationSelectionCore();
        _textAnnotationPlacementArmed = false;
        ++_textAnnotationGeneration;
        var revision = TextAnnotations.Revision;
        InvalidateFrame(textAnnotationChanged: true, imageAnnotationChanged: imageChanged);
        return _attached && revision == TextAnnotations.Revision && TextAnnotations.Draft is null;
    }
    public void CancelTextAnnotationTool()
    {
        Dispatcher.UIThread.VerifyAccess();
        var changed = _textAnnotationPlacementArmed;
        _textAnnotationPlacementArmed = false;
        if (TextAnnotations.Draft is { } draft) changed |= TextAnnotations.Cancel(draft);
        if (!changed) return;
        ++_textAnnotationGeneration;
        InvalidateFrame(textAnnotationChanged: true);
    }
    public bool SelectTextAnnotation(long id)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null) return false;
        var previousRevision = TextAnnotations.Revision;
        if (!TextAnnotations.Select(id)) return false;
        var imageChanged = ClearImageAnnotationSelectionCore();
        var changed = imageChanged || _textAnnotationPlacementArmed || previousRevision != TextAnnotations.Revision;
        _textAnnotationPlacementArmed = false;
        if (changed)
        {
            ++_textAnnotationGeneration;
            InvalidateFrame(textAnnotationChanged: true, imageAnnotationChanged: imageChanged);
        }
        return _attached && TextAnnotations.SelectedId == id && TextAnnotations.Draft is null;
    }
    /// <summary>Clears selection and authoring without deleting any committed note.</summary>
    public bool ClearTextAnnotationSelection()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || !ClearTextAnnotationSelectionCore()) return false;
        InvalidateFrame(textAnnotationChanged: true);
        return _attached && TextAnnotations.SelectedId is null && !IsTextAnnotationToolActive;
    }
    /// <summary>Last rendered note wins overlap hit testing; no candle or volume hit is substituted.</summary>
    public bool SelectTextAnnotationAt(Point point, long expectedRevision, bool beginEdit = false)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || !double.IsFinite(point.X) ||
            !double.IsFinite(point.Y)) return false;
        EnsureTextAnnotationLayout();
        for (var index = _textAnnotationBoxes.Length - 1; index >= 0; index--)
        {
            var box = _textAnnotationBoxes[index];
            if (!box.Bounds.Contains(point)) continue;
            if (!beginEdit) return SelectTextAnnotation(box.Id);
            // Select and start the edit in one store operation, before any external callback.
            var draft = TextAnnotations.BeginEdit(box.Id);
            if (draft is null) return false;
            var imageChanged = ClearImageAnnotationSelectionCore();
            _textAnnotationPlacementArmed = false;
            ++_textAnnotationGeneration;
            InvalidateFrame(textAnnotationChanged: true, imageAnnotationChanged: imageChanged);
            return _attached && ReferenceEquals(TextAnnotations.Draft, draft);
        }
        return false;
    }
    public bool RemoveSelectedTextAnnotation()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null) return false;
        var revision = TextAnnotations.Revision;
        var removed = TextAnnotations.RemoveSelected();
        var changed = revision != TextAnnotations.Revision || _textAnnotationPlacementArmed;
        _textAnnotationPlacementArmed = false;
        if (changed)
        {
            ++_textAnnotationGeneration;
            InvalidateFrame(textAnnotationChanged: true);
        }
        return removed;
    }
    public bool BeginImageAnnotationPlacement()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null || !IsPlotAvailable ||
            ImageAnnotations.Items.Length >= PriceImageAnnotations.MaximumNotes ||
            ImageAnnotations.Items.Sum(note => note.Image.Width * note.Image.Height) >= PriceImageAnnotations.MaximumPixelCount) return false;
        if (_imageAnnotationPlacementArmed && ImageAnnotations.Draft is null &&
            !IsTextAnnotationToolActive && TextAnnotations.SelectedId is null &&
            !IsTrendLineToolActive && SelectedTrendLineId is null && !IsRetracementToolActive && SelectedRetracementId is null &&
            !IsFanToolActive && SelectedFanId is null && !IsArcToolActive && SelectedArcId is null) return true;
        var textChanged = ClearTextAnnotationSelectionCore();
        ImageAnnotations.ClearSelection();
        _imageAnnotationPlacementArmed = true;
        var generation = ++_imageAnnotationGeneration;
        InvalidateFrame(textChanged, imageAnnotationChanged: true);
        return _attached && _imageAnnotationPlacementArmed && generation == _imageAnnotationGeneration;
    }
    /// <summary>Starts an image draft at a fixed chart-local DIP anchor, never at a bar identity.</summary>
    public bool BeginImageAnnotationAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || !_imageAnnotationPlacementArmed ||
            Snapshot is null || !IsPlotAvailable || !double.IsFinite(point.X) ||
            !double.IsFinite(point.Y) || !PlotArea.Contains(point)) return false;
        var draft = ImageAnnotations.BeginNew(point.X, point.Y);
        if (draft is null) return false;
        var textChanged = ClearTextAnnotationSelectionCore();
        _imageAnnotationPlacementArmed = false;
        ++_imageAnnotationGeneration;
        InvalidateFrame(textChanged, imageAnnotationChanged: true);
        return _attached && ReferenceEquals(ImageAnnotations.Draft, draft);
    }
    public bool BeginSelectedImageAnnotationEdit()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null || ImageAnnotations.SelectedId is not { } id) return false;
        var draft = ImageAnnotations.BeginEdit(id);
        if (draft is null) return false;
        var textChanged = ClearTextAnnotationSelectionCore();
        _imageAnnotationPlacementArmed = false;
        ++_imageAnnotationGeneration;
        InvalidateFrame(textChanged, imageAnnotationChanged: true);
        return _attached && ReferenceEquals(ImageAnnotations.Draft, draft);
    }
    public bool CommitImageAnnotation(PriceImageAnnotationDraft draft, RasterImage image)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null || !ImageAnnotations.Commit(draft, image)) return false;
        var textChanged = ClearTextAnnotationSelectionCore();
        _imageAnnotationPlacementArmed = false;
        ++_imageAnnotationGeneration;
        var revision = ImageAnnotations.Revision;
        InvalidateFrame(textChanged, imageAnnotationChanged: true);
        return _attached && revision == ImageAnnotations.Revision && ImageAnnotations.Draft is null;
    }
    public void CancelImageAnnotationTool()
    {
        Dispatcher.UIThread.VerifyAccess();
        var changed = CancelImageAnnotationToolCore();
        if (changed) InvalidateFrame(imageAnnotationChanged: true);
    }
    /// <summary>Cancels all authoring tools atomically, retaining committed notes, lines and selection.</summary>
    public void CancelAnnotationTools()
    {
        Dispatcher.UIThread.VerifyAccess();
        var textChanged = _textAnnotationPlacementArmed;
        _textAnnotationPlacementArmed = false;
        if (TextAnnotations.Draft is { } textDraft) textChanged |= TextAnnotations.Cancel(textDraft);
        if (textChanged) ++_textAnnotationGeneration;
        var imageChanged = CancelImageAnnotationToolCore();
        var trendChanged = CancelTrendLineToolCore();
        var retracementChanged = CancelRetracementToolCore();
        var fanChanged = CancelFanToolCore();
        var arcChanged = CancelArcToolCore();
        if (textChanged || imageChanged || trendChanged || retracementChanged || fanChanged || arcChanged)
            InvalidateFrame(textChanged, imageChanged, trendChanged, retracementChanged: retracementChanged, fanChanged: fanChanged, arcChanged: arcChanged);
    }
    private bool CancelImageAnnotationToolCore()
    {
        var changed = _imageAnnotationPlacementArmed;
        _imageAnnotationPlacementArmed = false;
        if (ImageAnnotations.Draft is { } draft) changed |= ImageAnnotations.Cancel(draft);
        if (changed) ++_imageAnnotationGeneration;
        return changed;
    }
    public bool SelectImageAnnotation(long id)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null) return false;
        var previousRevision = ImageAnnotations.Revision;
        if (!ImageAnnotations.Select(id)) return false;
        var textChanged = ClearTextAnnotationSelectionCore();
        var changed = textChanged || _imageAnnotationPlacementArmed || previousRevision != ImageAnnotations.Revision;
        _imageAnnotationPlacementArmed = false;
        if (changed)
        {
            ++_imageAnnotationGeneration;
            InvalidateFrame(textChanged, imageAnnotationChanged: true);
        }
        return _attached && ImageAnnotations.SelectedId == id && ImageAnnotations.Draft is null;
    }
    public bool ClearImageAnnotationSelection()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || !ClearImageAnnotationSelectionCore()) return false;
        InvalidateFrame(imageAnnotationChanged: true);
        return _attached && ImageAnnotations.SelectedId is null && !IsImageAnnotationToolActive;
    }
    /// <summary>Last rendered image wins image overlaps. General hit testing places all images above text.</summary>
    public bool SelectImageAnnotationAt(Point point, long expectedRevision, bool beginEdit = false)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || !double.IsFinite(point.X) ||
            !double.IsFinite(point.Y)) return false;
        EnsureImageAnnotationLayout();
        for (var index = _imageAnnotationBoxes.Length - 1; index >= 0; index--)
        {
            var box = _imageAnnotationBoxes[index];
            if (!box.Bounds.Contains(point)) continue;
            if (!beginEdit) return SelectImageAnnotation(box.Id);
            var draft = ImageAnnotations.BeginEdit(box.Id);
            if (draft is null) return false;
            var textChanged = ClearTextAnnotationSelectionCore();
            _imageAnnotationPlacementArmed = false;
            ++_imageAnnotationGeneration;
            InvalidateFrame(textChanged, imageAnnotationChanged: true);
            return _attached && ReferenceEquals(ImageAnnotations.Draft, draft);
        }
        return false;
    }
    public bool RemoveSelectedImageAnnotation()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Snapshot is null) return false;
        var revision = ImageAnnotations.Revision;
        var removed = ImageAnnotations.RemoveSelected();
        var changed = revision != ImageAnnotations.Revision || _imageAnnotationPlacementArmed;
        _imageAnnotationPlacementArmed = false;
        if (changed)
        {
            ++_imageAnnotationGeneration;
            InvalidateFrame(imageAnnotationChanged: true);
        }
        return removed;
    }
    private void EnsureImageAnnotationLayout()
    {
        PruneImageAnnotationBitmaps();
        var revision = ImageAnnotations.Revision;
        var area = PlotArea;
        var available = _attached && Snapshot is not null && IsPlotAvailable;
        if (_imageAnnotationLayoutRevision == revision && _imageAnnotationLayoutSize == Bounds.Size &&
            _imageAnnotationLayoutArea == area && _imageAnnotationLayoutAvailable == available &&
            _imageAnnotationLayoutHasSource == (Snapshot is not null) && _imageAnnotationLayoutAttached == _attached) return;
        _imageAnnotationLayoutRevision = revision; _imageAnnotationLayoutSize = Bounds.Size;
        _imageAnnotationLayoutArea = area; _imageAnnotationLayoutAvailable = available;
        _imageAnnotationLayoutHasSource = Snapshot is not null; _imageAnnotationLayoutAttached = _attached;
        _imageAnnotationLayouts = []; _imageAnnotationBoxes = [];
        var items = ImageAnnotations.Items;
        _imageAnnotationLayoutStatus = Snapshot is null ? "NoSource" : !_attached ? "Detached" :
            !IsPlotAvailable ? "UnavailableLayout" : items.IsEmpty ? "NoNotes" : "Visible";
        if (!available || items.IsEmpty) return;
        var layouts = ImmutableArray.CreateBuilder<ImageAnnotationLayout>();
        var boxes = ImmutableArray.CreateBuilder<PriceImageAnnotationBox>();
        foreach (var note in items)
        {
            var anchor = new Point(note.X, note.Y);
            if (!area.Contains(anchor)) continue;
            // Original pixels stay immutable. Display fits 128x96 DIP without upscaling,
            // preserves aspect ratio and never shrinks again just to fit a smaller pane.
            var scale = Math.Min(1d, Math.Min(128d / note.Image.Width, 96d / note.Image.Height));
            var width = note.Image.Width * scale;
            var height = note.Image.Height * scale;
            if (width > area.Width || height > area.Height) continue;
            var left = Math.Clamp(anchor.X + 8, area.Left, area.Right - width);
            var top = Math.Clamp(anchor.Y + 8, area.Top, area.Bottom - height);
            var box = new PriceImageAnnotationBox(note.Id, new Rect(left, top, width, height), anchor);
            boxes.Add(box); layouts.Add(new(box, note.Image));
        }
        _imageAnnotationLayouts = layouts.ToImmutable(); _imageAnnotationBoxes = boxes.ToImmutable();
        if (_imageAnnotationBoxes.Length < items.Length)
            _imageAnnotationLayoutStatus = _imageAnnotationBoxes.IsEmpty ? "AllNotesSuppressed" : "SomeNotesSuppressed";
    }
    private string ImageAnnotationStatus => $". Image annotations: {ImageAnnotations.Items.Length}/{PriceImageAnnotations.MaximumNotes}; " +
        $"{VisibleImageAnnotationBoxes.Length} visible; {SuppressedImageAnnotationCount} suppressed ({ImageAnnotationLayoutStatus}). " +
        "Compatibility: chart-local DIP anchors, independent of bars and zoom/pan; original pixels retained. " +
        "Images preserve aspect ratio within 128 by 96 DIP without upscaling, and are fully suppressed if their anchor or box cannot fit the main pane. " +
        "Images render and hit-test above text; the last image wins image overlaps. " +
        (_imageAnnotationPlacementArmed ? "Click the price pane to place an image. Escape cancels." :
         ImageAnnotations.Draft is not null ? "Editing image annotation; Escape cancels the draft." :
         ImageAnnotations.SelectedId is { } id ? $"Image {id} selected; F2 edits, Delete removes." : "No image annotation selected.");
    private void PruneImageAnnotationBitmaps()
    {
        var items = ImageAnnotations.Items;
        foreach (var pair in _imageAnnotationBitmaps.ToArray())
        {
            if (_attached && items.Any(note => note.Id == pair.Key && ReferenceEquals(note.Image, pair.Value.Image))) continue;
            _imageAnnotationBitmaps.Remove(pair.Key);
            pair.Value.Bitmap.Dispose();
        }
    }
    private void ReleaseImageAnnotationBitmaps()
    {
        foreach (var entry in _imageAnnotationBitmaps.Values) entry.Bitmap.Dispose();
        _imageAnnotationBitmaps.Clear();
    }
    private WriteableBitmap GetImageAnnotationBitmap(long id, RasterImage image)
    {
        if (_imageAnnotationBitmaps.TryGetValue(id, out var existing))
        {
            if (ReferenceEquals(existing.Image, image)) return existing.Bitmap;
            _imageAnnotationBitmaps.Remove(id); existing.Bitmap.Dispose();
        }
        // Only validated, immutable RGBA bytes reach the graphics backend. No PNG/native decode.
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96),
            PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        try
        {
            var pixels = image.CopyPixels();
            using (var framebuffer = bitmap.Lock())
            {
                var rowBytes = checked(image.Width * 4);
                for (var row = 0; row < image.Height; row++)
                    Marshal.Copy(pixels, row * rowBytes, IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes), rowBytes);
            }
            _imageAnnotationBitmaps.Add(id, new(image, bitmap));
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
    private void RenderImageAnnotations(DrawingContext context)
    {
        EnsureImageAnnotationLayout();
        using (context.PushClip(PlotArea))
            foreach (var layout in _imageAnnotationLayouts)
            {
                var box = layout.Box;
                var selected = ImageAnnotations.SelectedId == box.Id;
                var pen = new Pen(selected ? Palette.Accent : Palette.Muted, selected ? 2 : 1);
                context.DrawLine(pen, box.AnchorPoint, box.Bounds.TopLeft);
                context.DrawEllipse(Palette.Accent, null, box.AnchorPoint, 2.5, 2.5);
                // No opaque fill: the original straight-alpha pixels composite onto the chart.
                context.DrawImage(GetImageAnnotationBitmap(box.Id, layout.Image),
                    new Rect(0, 0, layout.Image.Width, layout.Image.Height), box.Bounds);
                context.DrawRectangle(null, pen, box.Bounds.Deflate(Math.Min(selected ? 1 : .5,
                    Math.Min(box.Bounds.Width, box.Bounds.Height) / 2)));
            }
    }
    private void EnsureTextAnnotationLayout()
    {
        var revision = TextAnnotations.Revision;
        var area = PlotArea;
        var available = _attached && Snapshot is not null && IsPlotAvailable;
        if (_textAnnotationLayoutRevision == revision && _textAnnotationLayoutSize == Bounds.Size &&
            _textAnnotationLayoutArea == area && ReferenceEquals(_textAnnotationLayoutPalette, Palette) &&
            _textAnnotationLayoutAvailable == available && _textAnnotationLayoutHasSource == (Snapshot is not null) &&
            _textAnnotationLayoutAttached == _attached) return;
        _textAnnotationLayoutRevision = revision; _textAnnotationLayoutSize = Bounds.Size;
        _textAnnotationLayoutArea = area; _textAnnotationLayoutPalette = Palette;
        _textAnnotationLayoutAvailable = available; _textAnnotationLayoutHasSource = Snapshot is not null;
        _textAnnotationLayoutAttached = _attached;
        _textAnnotationLayouts = []; _textAnnotationBoxes = [];
        var items = TextAnnotations.Items;
        _textAnnotationLayoutStatus = Snapshot is null ? "NoSource" : !_attached ? "Detached" :
            !IsPlotAvailable ? "UnavailableLayout" : items.IsEmpty ? "NoNotes" : "Visible";
        if (!available || items.IsEmpty) return;
        var layouts = ImmutableArray.CreateBuilder<TextAnnotationLayout>();
        var boxes = ImmutableArray.CreateBuilder<PriceTextAnnotationBox>();
        foreach (var note in items)
        {
            var anchor = new Point(note.X, note.Y);
            if (!area.Contains(anchor)) continue;
            // Compatibility choice: chart-local DIP anchors, not series-point/bar identities.
            // Wrap and measure the complete text, never DrawingText's one-line ellipsis.
            var text = new FormattedText(note.Text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 12, Palette.Text)
            {
                MaxTextWidth = Math.Max(1, Math.Min(300, area.Width - 12)),
                MaxLineCount = int.MaxValue, Trimming = TextTrimming.None
            };
            var width = text.Width + 12;
            var height = text.Height + 8;
            // Suppress the entire box if its complete text cannot fit. The stored text and
            // anchor are never shortened or moved, and remain available in the notes editor.
            if (!double.IsFinite(width) || !double.IsFinite(height) || width > area.Width || height > area.Height)
                continue;
            var left = Math.Clamp(anchor.X + 8, area.Left, area.Right - width);
            var top = Math.Clamp(anchor.Y + 8, area.Top, area.Bottom - height);
            var box = new PriceTextAnnotationBox(note.Id, new Rect(left, top, width, height), anchor);
            boxes.Add(box); layouts.Add(new(box, text));
        }
        _textAnnotationLayouts = layouts.ToImmutable(); _textAnnotationBoxes = boxes.ToImmutable();
        if (_textAnnotationBoxes.Length < items.Length)
            _textAnnotationLayoutStatus = _textAnnotationBoxes.IsEmpty ? "AllNotesSuppressed" : "SomeNotesSuppressed";
    }
    private string TextAnnotationStatus => $". Text annotations: {TextAnnotations.Items.Length}/{PriceTextAnnotations.MaximumNotes}; " +
        $"{VisibleTextAnnotationBoxes.Length} visible; {SuppressedTextAnnotationCount} suppressed ({TextAnnotationLayoutStatus}). " +
        "Complete labels are suppressed when their anchor or full text cannot fit the main pane; no text is ellipsized. " +
        "Compatibility: chart-local DIP anchors, independent of bars and zoom/pan; full text retained in notes editor. " +
        (_textAnnotationPlacementArmed ? "Click the price pane to place a note. Escape cancels." :
         TextAnnotations.Draft is not null ? "Editing text annotation; Escape cancels the draft." :
         TextAnnotations.SelectedId is { } id ? $"Note {id} selected; F2 edits, Delete removes." : "No text annotation selected.");
    private void RenderTextAnnotations(DrawingContext context)
    {
        EnsureTextAnnotationLayout();
        using (context.PushClip(PlotArea))
            foreach (var layout in _textAnnotationLayouts)
            {
                var box = layout.Box;
                var selected = TextAnnotations.SelectedId == box.Id;
                var pen = new Pen(selected ? Palette.Accent : Palette.Muted, selected ? 2 : 1);
                context.DrawLine(pen, box.AnchorPoint, box.Bounds.TopLeft);
                context.DrawEllipse(Palette.Accent, null, box.AnchorPoint, 2.5, 2.5);
                context.FillRectangle(selected ? Palette.Selection : Palette.Surface, box.Bounds);
                context.DrawRectangle(null, pen, box.Bounds);
                context.DrawText(layout.Text, new Point(box.Bounds.Left + 6, box.Bounds.Top + 4));
            }
    }
    /// <summary>Transient nearest-ordinal argument inspection, independent of click selection,
    /// Original rows and the final original CurrentClose. Never carried across frame changes.</summary>
    public PriceCrosshairSnapshot? Crosshair => _crosshair;
    public ImmutableArray<PriceCrosshairLine> CrosshairLines => _crosshairLines;
    public ImmutableArray<PriceCrosshairMarker> CrosshairHighlights => _crosshairHighlights;
    /// <summary>Complete numeric readout, or an explicit nonnumeric diagnostics fallback.</summary>
    public Rect? CrosshairLabelBounds => _crosshairLabel?.Bounds;
    public string CrosshairLabelPlacement => _crosshairLabelPlacement;
    /// <summary>The final ORIGINAL row's exact close and provenance, independent of the visible
    /// or selected candle. A trailing gap is unavailable, never an older carried-forward price.</summary>
    public PriceCurrentCloseSnapshot? CurrentClose => _frame.CurrentClose;
    /// <summary>The exact unellipsized right-gutter label rectangle consumed by Render.</summary>
    public Rect? CurrentCloseBadgeBounds => GetCurrentCloseBadge()?.Bounds;
    /// <summary>Actual unrounded close's position on the price axis; edge labels may be moved
    /// inward to fit, but their short axis tick stays at this exact location.</summary>
    public Point? CurrentCloseAxisPoint => GetCurrentCloseBadge()?.AxisPoint;
    public string CurrentClosePlacement { get { GetCurrentCloseBadge(); return _badgePlacement; } }
    /// <summary>Immutable full-source aggregation and its original provenance, or null for raw bars.</summary>
    public PriceTimeAggregationSnapshot? AggregationSnapshot => _frame.Aggregation;
    /// <summary>The displayed candle or volume hit's actual segment, never a fabricated raw hit.</summary>
    public PriceTimeAggregateSegment? SelectedAggregateSegment
    {
        get
        {
            if (AggregationSnapshot is not { } aggregation ||
                (SelectedHit ?? SelectedVolumeHit) is not { } hit) return null;
            // Keys are unique in both sources; a derived segment retains its first original key.
            foreach (var segment in aggregation.Segments)
                if (segment.Bar.Key == hit.Key) return segment;
            return null;
        }
    }
    /// <summary>The requested options. Snapshot.Options may contain an effective auto-domain
    /// extended to include visible Bollinger values; the caller's options are never changed.</summary>
    public PriceChartOptions Options => _options;
    public bool BollingerEnabled => _frame.BollingerEnabled;
    /// <summary>Full-source calculation, reused unchanged when zooming or panning; null when disabled.</summary>
    public PriceBollingerSnapshot? BollingerBands => _frame.BollingerBands;
    /// <summary>The exact number of precomputed clipped lines consumed by Render, not raw point pairs.</summary>
    public int VisibleBollingerSegmentCount => _frame.BollingerSegments.Length;
    public bool WilliamsREnabled => _frame.WilliamsREnabled;
    /// <summary>Full-source calculation, reused unchanged when zooming or panning; null when disabled.</summary>
    public PriceWilliamsRSnapshot? WilliamsRValues => _frame.WilliamsRValues;
    /// <summary>The exact precomputed lines consumed by the available indicator pane's Render.</summary>
    public int VisibleWilliamsRSegmentCount => WilliamsRPlotArea is not null ? _frame.WilliamsRSegments.Length : 0;
    public bool CciEnabled => _frame.CciEnabled;
    /// <summary>Full-source raw CCI values, reused across zoom/pan; never clamped to the display range.</summary>
    public PriceCciSnapshot? CciValues => _frame.CciValues;
    /// <summary>The exact display-clipped lines consumed by the available CCI pane's Render.</summary>
    public int VisibleCciSegmentCount => CciPlotArea is not null ? _frame.CciSegments.Length : 0;
    public bool VolumeEnabled => _frame.VolumeEnabled;
    /// <summary>Viewport volume on its own linear zero-to-visible-maximum scale, never the price domain.</summary>
    public PriceVolumeSnapshot? VolumeSnapshot => _frame.VolumeValues;
    /// <summary>The exact positive-height volume rectangles consumed by the available price plot.</summary>
    public int VisibleVolumeColumnCount => IsPlotAvailable && VolumeSnapshot is { } volumes
        ? volumes.Columns.Count(column => column.Body.Height > 0) : 0;
    public PriceChartHit? SelectedVolumeHit => _volumeSelection;
    public long FrameRevision { get; private set; }
    public UiPalette Palette { get => GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }
    public OliviaChartTheme? ChartTheme { get => GetValue(ChartThemeProperty); set => SetValue(ChartThemeProperty, value); }
    // Stable identities across indicator visibility changes; volume gray and current-close orange
    // are semantic paints from the official example and deliberately stay independent.
    private IBrush BollingerBrush => _seriesBrushes.Length == 0 ? Palette.Muted : _seriesBrushes[0];
    private IBrush CciBrush => _seriesBrushes.Length == 0 ? Palette.Accent : _seriesBrushes[1];
    private IBrush WilliamsRBrush => _seriesBrushes.Length == 0 ? Palette.Accent : _seriesBrushes[2];
    private Rect FullPlotArea => new(76, 62, Math.Max(0, Bounds.Width - 76 - CurrentCloseGutter), Math.Max(0, Bounds.Height - 132));
    // Keep the original single-Williams-%R layout. For two panes, allocate one shared
    // vertical budget, not two independently fixed-height panels. All three plots need
    // at least 60 pixels; if that is impossible the explicit size warning replaces them.
    private double IndicatorPaneHeight => CciEnabled && WilliamsREnabled
        ? Math.Clamp((FullPlotArea.Height - 2 * PaneGap) * .27, 60, 110)
        : Math.Clamp(FullPlotArea.Height * .3, 60, 140);
    /// <summary>Price-only area. Disabling both oscillators restores the original full rectangle.</summary>
    public Rect PlotArea => CciEnabled || WilliamsREnabled
        ? new(FullPlotArea.X, FullPlotArea.Y, FullPlotArea.Width,
            Math.Max(0, FullPlotArea.Height - (CciEnabled && WilliamsREnabled ? 2 : 1) * (PaneGap + IndicatorPaneHeight)))
        : FullPlotArea;
    /// <summary>CCI's fixed DISPLAY [-150,150] pane; raw values may exceed these bounds.</summary>
    public Rect? CciPlotArea => CciEnabled && IsPlotAvailable
        ? new Rect(FullPlotArea.X, PlotArea.Bottom + PaneGap, FullPlotArea.Width, IndicatorPaneHeight)
        : null;
    /// <summary>Separate fixed [-100,0] indicator pane; null when disabled or layout is too small.</summary>
    public Rect? WilliamsRPlotArea => WilliamsREnabled && IsPlotAvailable
        ? new Rect(FullPlotArea.X, PlotArea.Bottom + PaneGap + (CciEnabled ? IndicatorPaneHeight + PaneGap : 0), FullPlotArea.Width, IndicatorPaneHeight)
        : null;
    public bool IsPlotAvailable => Bounds.Width >= 320 && PlotArea.Height >= (CciEnabled || WilliamsREnabled ? 60 : 35);
    private string InsufficientSpaceMessage => CciEnabled && WilliamsREnabled
        ? "Enlarge chart: price, CCI and Williams %R each need at least 60 px of height"
        : CciEnabled ? "Enlarge chart: price and CCI panes need more space"
        : WilliamsREnabled ? "Enlarge chart: price and Williams %R panes need more space"
        : "Enlarge chart to show candles and labels";
    public PriceChartHit? SelectedHit => Snapshot is { } s && _selection >= 0 && _selection < s.Candles.Length ? s.Candles[_selection].Hit : null;
    private string PeriodUnit => AggregationSnapshot is null ? "" : " aggregate segments";
    public string Status
    {
        get
        {
            // Frame statistics/provenance can scan bounded aggregate/volume collections.
            // Hover must not repeat those scans or rebuild selection details at pointer rate.
            if (_statusRevision != FrameRevision)
            { _cachedStatus = BuildStatus(); _statusRevision = FrameRevision; }
            return _cachedStatus;
        }
    }
    private string BuildStatus() => Snapshot is not { } s ? "No source" : AggregationStatus +
        (s.BudgetStatus == ChartBudgetStatus.InsufficientBudget
        ? $"Budget refused: need {s.RequiredCandleCount} candles; no partial chart"
        : $"Dataset {s.Source.DatasetVersion} · {s.Candles.Length} candles · {s.Gaps.Length} gaps · {(_options.TimeViewport is null ? "ordinal bars" : "time-selected segments")} {s.BarStart}–{s.BarStart + s.BarCount}" +
            (VolumeSnapshot is { } volumes ? $" · {(AggregationSnapshot is null ? "Volume" : "Aggregated Volume")} gray overlay · own Y 0–visible max{(AggregationSnapshot is null ? "" : " + interval margin")} · {VisibleVolumeColumnCount} columns · {volumes.MissingVolumeCount} unavailable" : "") +
            (BollingerBands is { } bands ? $" · BB(20,2){PeriodUnit} Close · population σ · {VisibleBollingerSegmentCount} lines · {bands.OverflowCount} overflow windows omitted" : "") +
            (CciValues is { } cci ? $" · CCI(14){PeriodUnit} HLC3 compatibility · display [-150,150], guides ±100 · {VisibleCciSegmentCount} lines · {cci.FlatWindowCount} flat windows omitted" : "") +
            (WilliamsRValues is { } values ? $" · Williams %R(14){PeriodUnit} · fixed [-100,0] · {VisibleWilliamsRSegmentCount} lines · {values.FlatWindowCount} flat windows omitted" : "") +
            ((CciEnabled || WilliamsREnabled) && !IsPlotAvailable ? " · " + InsufficientSpaceMessage : "")) + CurrentCloseStatus;
    private string CurrentCloseStatus
    {
        get
        {
            if (CurrentClose is not { } current) return "";
            if (current.OriginalHit is not { } hit) return " · Current original close unavailable: empty source";
            return $" · Current original close {Raw(hit.Bar.Close)}; original series {hit.SeriesId}; " +
                $"index {current.OriginalIndex}; key {hit.Key}; timestamp {hit.Timestamp:O}; " +
                $"source version {hit.SourceVersion}; dataset version {hit.DatasetVersion}; " +
                $"right-axis label {CurrentClosePlacement} (invariant F4 display only; no carry-forward)";
        }
    }
    private CurrentCloseBadge? GetCurrentCloseBadge()
    {
        if (_badgeRevision == FrameRevision) return _badge;
        _badgeRevision = FrameRevision; _badge = null;
        var current = CurrentClose;
        _badgePlacement = current?.State.ToString() ?? "NoSource";
        if (current is not { State: PriceCurrentCloseState.Visible, ScreenY: { } screenY, AxisText: { } value }) return null;
        if (!IsPlotAvailable) { _badgePlacement = "UnavailableLayout"; return null; }
        var area = PlotArea;
        // Use the complete numeric label, never DrawingText's ellipsis. If it cannot fit,
        // suppress the badge and retain exact raw value/provenance in diagnostics instead.
        var text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, Brushes.White);
        var width = text.Width + 6;
        var height = text.Height + 4;
        var left = area.Right + 6;
        if (!double.IsFinite(width) || !double.IsFinite(height) ||
            width > Bounds.Width - 6 - left || height > area.Height)
        { _badgePlacement = "TextTooWide"; return null; }
        var y = area.Top + screenY * area.Height;
        var top = Math.Clamp(y - height / 2, area.Top, area.Bottom - height);
        _badge = new(new Rect(left, top, width, height), new Point(area.Right, y), text);
        _badgePlacement = "Visible";
        return _badge;
    }
    private string AggregationStatus
    {
        get
        {
            if (AggregationSnapshot is not { } aggregation) return "";
            var original = aggregation.OriginalSource;
            return $"Aggregated OHLC / Aggregated Volume · {PriceTimeAggregator.GetLabel(aggregation.Interval)} UTC buckets [start,end) · " +
                $"original series {original.SeriesId}; source version {original.SourceVersion}; dataset version {original.DatasetVersion} · " +
                $"{original.Bars.Length} original rows → {aggregation.Segments.Length} aggregate segments · " +
                $"Missing volume segments {aggregation.Segments.Count(segment => segment.VolumeStatus == PriceAggregateVolumeStatus.Missing)}; " +
                $"Overflow volume segments {aggregation.Segments.Count(segment => segment.VolumeStatus == PriceAggregateVolumeStatus.Overflow)} · " +
                "explicit gaps retained; same-bucket runs split at gaps; no empty buckets invented · ";
        }
    }
    public string AccessibleSummary => Status + (_crosshair is null ? "" : ". Hover crosshair: " +
        _crosshair.AccessibleSummary + $". Group label {CrosshairLabelPlacement}. " +
        "Hover does not change the clicked selection or its Original rows.") +
        (_selectionDetails ??= BuildSelectionDetails()) + TextAnnotationStatus + ImageAnnotationStatus + ". " + TrendLineStatus + ". " + RetracementStatus + ". " + FanStatus + ". " + ArcStatus;
    private string BuildSelectionDetails()
    {
        if ((SelectedHit ?? SelectedVolumeHit) is not { } hit) return "";
        var values = $"{hit.SeriesId}; key {hit.Key}; UTC {hit.Timestamp.ToUniversalTime():O}; " +
            $"O {Raw(hit.Bar.Open)}; H {Raw(hit.Bar.High)}; L {Raw(hit.Bar.Low)}; C {Raw(hit.Bar.Close)}; " +
            $"{(AggregationSnapshot is null ? "Volume" : "Aggregated Volume")} {RawVolume(hit.Bar.Volume)}; " +
            $"source version {hit.SourceVersion}; dataset version {hit.DatasetVersion}";
        var selection = SelectedVolumeHit is not null ? "Volume column selected" : "Candle selected";
        if (AggregationSnapshot is not { } aggregation || SelectedAggregateSegment is not { } segment)
            return $". {selection}. {values}";
        var first = aggregation.OriginalSource.Bars[segment.OriginalStartIndex];
        var last = aggregation.OriginalSource.Bars[segment.OriginalStartIndex + segment.OriginalCount - 1];
        var end = segment.BucketEndUtc is { } endUtc ? endUtc.ToString("O", CultureInfo.InvariantCulture)
            : $"UTC tick {segment.BucketEndUtcTicksExclusive} (beyond DateTimeOffset.MaxValue)";
        return $". {selection}: Aggregated OHLC. {values}. " +
            $"{PriceTimeAggregator.GetLabel(aggregation.Interval)} UTC bucket [{segment.BucketStartUtc:O}, {end}); " +
            $"{segment.OriginalCount} original rows, indexes {segment.OriginalStartIndex}–{segment.OriginalStartIndex + segment.OriginalCount - 1} (inclusive); " +
            $"first original key {first.Key}, timestamp {first.Timestamp:O}; last original key {last.Key}, timestamp {last.Timestamp:O}; " +
            $"Aggregated Volume status {segment.VolumeStatus}; missing constituent volumes {segment.MissingVolumeCount}. " +
            "Displayed key/time belong to the first constituent, not the UTC bucket start. Original rows are available separately.";
    }
    private static string Raw(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "gap";
    private static string RawVolume(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "unavailable";
    public event EventHandler? StateChanged;
    /// <summary>Replaces the displayed data with raw bars, retaining ordinal options and
    /// exiting the source-bound compressed time projection.</summary>
    public void SetSource(PriceChartSource source) => SetSource(source,
        _options.TimeViewport is null ? _options : _options.WithTimeViewport(null));
    /// <summary>Publishes raw source and requested options together; clears prior aggregation.</summary>
    public void SetSource(PriceChartSource source, PriceChartOptions options)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(options);
        var candidate = CreateFrame(source, options, BollingerEnabled, BollingerBands, WilliamsREnabled, WilliamsRValues, CciEnabled, CciValues, VolumeEnabled);
        _frame = candidate; InvalidateFrame();
    }
    /// <summary>Publishes aggregate prices, volume, recalculated indicators and provenance atomically.
    /// Omitted options retain the current requested view; aggregation always precedes that viewport.</summary>
    public void SetAggregatedSource(PriceTimeAggregationSnapshot snapshot, PriceChartOptions? options = null)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(snapshot);
        options ??= _options;
        if (options.TimeViewport is { } time && !ReferenceEquals(time.Axis.Aggregation, snapshot))
            options = options.WithTimeViewport(time.Rebase(snapshot));
        var candidate = CreateFrame(snapshot.Source, options, BollingerEnabled, BollingerBands,
            WilliamsREnabled, WilliamsRValues, CciEnabled, CciValues, VolumeEnabled, snapshot);
        _frame = candidate; InvalidateFrame();
    }
    public void SetOptions(PriceChartOptions options)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(options);
        var candidate = CreateFrame(_source, options, BollingerEnabled, BollingerBands, WilliamsREnabled, WilliamsRValues, CciEnabled, CciValues, VolumeEnabled, AggregationSnapshot);
        _frame = candidate; InvalidateFrame();
    }
    public void SetBollingerEnabled(bool enabled)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (enabled == BollingerEnabled) return;
        var candidate = CreateFrame(_source, _options, enabled, BollingerBands, WilliamsREnabled, WilliamsRValues, CciEnabled, CciValues, VolumeEnabled, AggregationSnapshot);
        _frame = candidate; InvalidateFrame();
    }
    public void SetWilliamsREnabled(bool enabled)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (enabled == WilliamsREnabled) return;
        var candidate = CreateFrame(_source, _options, BollingerEnabled, BollingerBands, enabled, WilliamsRValues, CciEnabled, CciValues, VolumeEnabled, AggregationSnapshot);
        _frame = candidate; InvalidateFrame();
    }
    public void SetCciEnabled(bool enabled)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (enabled == CciEnabled) return;
        var candidate = CreateFrame(_source, _options, BollingerEnabled, BollingerBands, WilliamsREnabled, WilliamsRValues, enabled, CciValues, VolumeEnabled, AggregationSnapshot);
        _frame = candidate; InvalidateFrame();
    }
    public void SetVolumeEnabled(bool enabled)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (enabled == VolumeEnabled) return;
        // Toggling this overlay must not recalculate prices, BB, or either oscillator.
        // The existing price snapshot and every indicator cache remain exactly unchanged.
        var candidate = _frame with
        {
            VolumeEnabled = enabled,
            VolumeValues = enabled && Snapshot is { } prices ? PriceVolumeGeometry.Create(prices,
                PriceFinancialSideMargins.ForInterval(AggregationSnapshot?.Interval).Volume) : null
        };
        _frame = candidate; InvalidateFrame();
    }
    private PriceFrame CreateFrame(PriceChartSource? source, PriceChartOptions options,
        bool enabled, PriceBollingerSnapshot? previousBands, bool williamsREnabled,
        PriceWilliamsRSnapshot? previousWilliamsR, bool cciEnabled, PriceCciSnapshot? previousCci, bool volumeEnabled,
        PriceTimeAggregationSnapshot? aggregation = null)
    {
        if (source is null) return new(null, options, null, enabled, null, [], williamsREnabled, null, [], cciEnabled, null, [], volumeEnabled, null, null, null);
        var bands = !enabled ? null : ReferenceEquals(previousBands?.Source, source)
            ? previousBands : _indicatorCache.Bollinger(source);
        var williamsR = !williamsREnabled ? null : ReferenceEquals(previousWilliamsR?.Source, source)
            ? previousWilliamsR : _indicatorCache.WilliamsR(source);
        var cci = !cciEnabled ? null : ReferenceEquals(previousCci?.Source, source)
            ? previousCci : _indicatorCache.Cci(source);
        HistoryValueDomain? overlayDomain = null;
        if (options.FixedDomain is null && bands is not null)
        {
            var start = Math.Min(options.BarStart, source.Bars.Length);
            var count = Math.Min(options.BarCount, source.Bars.Length - start);
            if (options.TimeViewport is { } time) (start, count) = time.SelectSegments();
            double? minimum = null, maximum = null;
            foreach (var point in bands.Points)
            {
                if (point.BarIndex < start) continue;
                if (point.BarIndex >= start + count) break;
                minimum = minimum.HasValue ? Math.Min(minimum.Value, point.Lower) : point.Lower;
                maximum = maximum.HasValue ? Math.Max(maximum.Value, point.Upper) : point.Upper;
            }
            if (minimum.HasValue) overlayDomain = new(minimum.Value, maximum!.Value);
        }
        var margins = PriceFinancialSideMargins.ForInterval(aggregation?.Interval);
        var snapshot = source.Query(options, overlayDomain, margins.Price);
        // Both dimensionless oscillators and volume are deliberately excluded from the price auto-domain.
        return new(source, options, snapshot, enabled, bands, CreateBollingerSegments(snapshot, bands),
            williamsREnabled, williamsR, CreateWilliamsRSegments(snapshot, williamsR),
            cciEnabled, cci, CreateCciSegments(snapshot, cci),
            volumeEnabled, volumeEnabled ? PriceVolumeGeometry.Create(snapshot, margins.Volume) : null, aggregation,
            PriceCurrentClose.Create(snapshot, aggregation));
    }
    private static ImmutableArray<BollingerSegment> CreateBollingerSegments(PriceChartSnapshot snapshot,
        PriceBollingerSnapshot? bands)
    {
        // GeometryBudget retains its existing candle-budget meaning. Refused OHLC frames
        // suppress the entire overlay; admitted default BB(20,2) has at most 53,940 lines
        // for the bounded 18,000-bar source, not a second independently sampled budget.
        if (bands is null || snapshot.BudgetStatus != ChartBudgetStatus.Satisfied ||
            snapshot.BarCount == 0 || (snapshot.Options.TimeViewport is null && snapshot.BarCount < 2) ||
            snapshot.Domain is not { } domain) return [];
        var segments = ImmutableArray.CreateBuilder<BollingerSegment>();
        // Clip through the same projection as candles, including offscreen neighbours
        // when a narrow fractional time range contains just one candle center.
        for (var i = 1; i < bands.Points.Length; i++)
        {
            var previous = bands.Points[i - 1];
            var current = bands.Points[i];
            if (current.BarIndex < snapshot.BarStart) continue;
            if (previous.BarIndex >= snapshot.BarStart + snapshot.BarCount) break;
            // Missing warm-up / gap / overflow output breaks every curve, never bridge it.
            if (current.BarIndex != previous.BarIndex + 1) continue;
            Add(previous.Middle, current.Middle, true);
            Add(previous.Upper, current.Upper, false);
            Add(previous.Lower, current.Lower, false);
            void Add(double first, double second, bool isMiddle)
            {
                var line = snapshot.ClipValueSegment(previous.BarIndex, first, current.BarIndex, second, domain);
                if (line is { } clipped) segments.Add(new(clipped, isMiddle));
            }
        }
        return segments.ToImmutable();
    }
    private static ImmutableArray<HistoryChartLine> CreateWilliamsRSegments(PriceChartSnapshot snapshot,
        PriceWilliamsRSnapshot? values)
    {
        // Preserve the candle budget's meaning and refuse the indicator along with refused OHLC.
        // At most 17,986 lines exist for the bounded 18,000-bar source and full 14-bar warm-up.
        if (values is null || snapshot.BudgetStatus != ChartBudgetStatus.Satisfied || snapshot.BarCount == 0 ||
            (snapshot.Options.TimeViewport is null && snapshot.BarCount < 2))
            return [];
        var segments = ImmutableArray.CreateBuilder<HistoryChartLine>();
        for (var i = 1; i < values.Points.Length; i++)
        {
            var previous = values.Points[i - 1];
            var current = values.Points[i];
            if (current.BarIndex < snapshot.BarStart) continue;
            if (previous.BarIndex >= snapshot.BarStart + snapshot.BarCount) break;
            // Gaps, warm-up and omitted flat windows are genuine breaks, not interpolated data.
            if (current.BarIndex != previous.BarIndex + 1) continue;
            var line = snapshot.ClipValueSegment(previous.BarIndex, previous.Value, current.BarIndex, current.Value, WilliamsRDomain);
            if (line is { } clipped) segments.Add(clipped);
        }
        return segments.ToImmutable();
    }
    private static ImmutableArray<HistoryChartLine> CreateCciSegments(PriceChartSnapshot snapshot,
        PriceCciSnapshot? values)
    {
        // Bounded all-source values are independent of the viewport; geometry alone is clipped.
        // At most 17,986 segments exist for an 18,000-bar source with the default 14-bar period.
        if (values is null || snapshot.BudgetStatus != ChartBudgetStatus.Satisfied || snapshot.BarCount == 0 ||
            (snapshot.Options.TimeViewport is null && snapshot.BarCount < 2))
            return [];
        var segments = ImmutableArray.CreateBuilder<HistoryChartLine>();
        for (var i = 1; i < values.Points.Length; i++)
        {
            var previous = values.Points[i - 1];
            var current = values.Points[i];
            // Never bridge a missing warm-up, gap, flat or otherwise omitted output index.
            if (current.BarIndex < snapshot.BarStart) continue;
            if (previous.BarIndex >= snapshot.BarStart + snapshot.BarCount) break;
            if (current.BarIndex != previous.BarIndex + 1) continue;
            var line = snapshot.ClipValueSegment(previous.BarIndex, previous.Value, current.BarIndex, current.Value, CciDisplayDomain);
            if (line is { } clipped) segments.Add(clipped);
        }
        return segments.ToImmutable();
    }
    public void ReleaseSource()
    {
        Dispatcher.UIThread.VerifyAccess();
        _indicatorCache.Clear();
        var notesChanged = IsTextAnnotationToolActive || !TextAnnotations.Items.IsEmpty || TextAnnotations.SelectedId is not null;
        var imagesChanged = IsImageAnnotationToolActive || !ImageAnnotations.Items.IsEmpty || ImageAnnotations.SelectedId is not null;
        var trendsChanged = IsTrendLineToolActive || !TrendLines.Items.IsEmpty || TrendLines.SelectedId is not null;
        var retracementsChanged = IsRetracementToolActive || !Retracements.Items.IsEmpty || Retracements.SelectedId is not null;
        var fansChanged = IsFanToolActive || !Fans.Items.IsEmpty || Fans.SelectedId is not null;
        var arcsChanged = IsArcToolActive || !Arcs.Items.IsEmpty || Arcs.SelectedId is not null;
        _textAnnotationPlacementArmed = false; TextAnnotations.Clear(); ++_textAnnotationGeneration;
        _imageAnnotationPlacementArmed = false; ImageAnnotations.Clear(); ++_imageAnnotationGeneration;
        ResetTrendGestureCore(); TrendLines.Clear(); ++_trendLineGeneration;
        ResetTwoAnchorGestureCore(Retracements); Retracements.Clear(); ++_retracementGeneration;
        ResetTwoAnchorGestureCore(Fans); Fans.Clear(); ++_fanGeneration;
        ResetTwoAnchorGestureCore(Arcs); Arcs.Clear(); ++_arcGeneration;
        ReleaseImageAnnotationBitmaps();
        _imageAnnotationLayouts = []; _imageAnnotationBoxes = []; _imageAnnotationLayoutRevision = -1;
        _frame = new(null, _options, null, false, null, [], false, null, [], false, null, [], false, null, null, null);
        _pointerFrames.Clear(); _releaseFrames.Clear(); _hoverFrames.Clear(); _keyFrames.Clear(); _wheelFrames.Clear();
        ClearArcLayout();
        InvalidateFrame(notesChanged, imagesChanged, trendsChanged, retracementChanged: retracementsChanged, fanChanged: fansChanged, arcChanged: arcsChanged);
    }
    public void CancelInteraction()
    {
        Dispatcher.UIThread.VerifyAccess();
        InvalidateFrame(preserveTrendTool: true, preserveRetracementTool: true, preserveFanTool: true, preserveArcTool: true);
    }
    private void InvalidateFrame(bool textAnnotationChanged = false, bool imageAnnotationChanged = false,
        bool trendLineChanged = false, bool preserveTrendTool = false,
        bool retracementChanged = false, bool preserveRetracementTool = false,
        bool fanChanged = false, bool preserveFanTool = false,
        bool arcChanged = false, bool preserveArcTool = false)
    {
        // Cross-tool retirement happens before the first public notification. In particular,
        // an owned live-window pause never observes a transient no-tool gap while switching.
        if ((textAnnotationChanged || imageAnnotationChanged || retracementChanged || fanChanged || arcChanged) && !trendLineChanged)
            trendLineChanged |= ClearTrendLineSelectionCore();
        if ((textAnnotationChanged || imageAnnotationChanged || trendLineChanged || fanChanged || arcChanged) && !retracementChanged)
            retracementChanged |= ClearRetracementSelectionCore();
        if ((textAnnotationChanged || imageAnnotationChanged || trendLineChanged || retracementChanged || arcChanged) && !fanChanged)
            fanChanged |= ClearFanSelectionCore();
        if ((textAnnotationChanged || imageAnnotationChanged || trendLineChanged || retracementChanged || fanChanged) && !arcChanged)
            arcChanged |= ClearArcSelectionCore();
        if (!preserveTrendTool) trendLineChanged |= CancelTrendLineToolCore();
        if (!preserveRetracementTool) retracementChanged |= CancelRetracementToolCore();
        if (!preserveFanTool) fanChanged |= CancelFanToolCore();
        if (!preserveArcTool) arcChanged |= CancelArcToolCore();
        FrameRevision++; _statusRevision = -1; _selectionDetails = null; _badge = null; _badgeRevision = -1; _selection = -1; _volumeSelection = null;
        if (_twoAnchorGesture is { } gesture &&
            ((preserveTrendTool && ReferenceEquals(gesture.Store, TrendLines)) ||
             (preserveRetracementTool && ReferenceEquals(gesture.Store, Retracements)) ||
             (preserveFanTool && ReferenceEquals(gesture.Store, Fans)) ||
             (preserveArcTool && ReferenceEquals(gesture.Store, Arcs)))) gesture.FrameRevision = FrameRevision;
        ClearCrosshairCore(); Changed(textAnnotationChanged, imageAnnotationChanged, trendLineChanged, retracementChanged, fanChanged, arcChanged);
    }
    private void ClearCrosshairCore()
    {
        _hoverGeneration++;
        _crosshair = null; _crosshairLines = []; _crosshairHighlights = [];
        _crosshairLabel = null; _crosshairLabelPlacement = "NoHover";
    }
    public void ClearCrosshair()
    {
        Dispatcher.UIThread.VerifyAccess();
        var hadHover = _crosshair is not null;
        ClearCrosshairCore();
        if (hadHover) Changed();
    }
    /// <summary>Resolve an argument from any existing pane, without candle/volume shape hit testing.
    /// A stale event cannot inspect a replacement frame. Reentrant callbacks always win.</summary>
    public bool UpdateCrosshairAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || IsAnyAnnotationToolActive) return false;
        _hoverGeneration++; // Newer explicit inspection also supersedes an older routed move.
        if (!IsPlotAvailable || !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            (!PlotArea.Contains(point) && !(CciPlotArea?.Contains(point) ?? false) &&
             !(WilliamsRPlotArea?.Contains(point) ?? false)) || Snapshot is not { } prices)
        { ClearCrosshair(); return false; }
        var x = (point.X - PlotArea.Left) / PlotArea.Width;
        var index = prices.FindBarAt(x);
        // Moving vertically, including between panes, does not recalculate values or publish
        // duplicate diagnostics for the same immutable argument. Nothing follows mouse Y.
        if (_crosshair is { } existing && ReferenceEquals(existing.Prices, prices) && existing.BarIndex == index)
            return true;
        var candidate = PriceCrosshair.Query(prices, x, VolumeSnapshot, BollingerBands, CciValues,
            WilliamsRValues, AggregationSnapshot);
        if (candidate is null) { ClearCrosshair(); return false; }
        var lineX = PlotArea.Left + candidate.ScreenX * PlotArea.Width;
        var lines = ImmutableArray.CreateBuilder<PriceCrosshairLine>();
        lines.Add(new(new Point(lineX, PlotArea.Top), new Point(lineX, PlotArea.Bottom)));
        if (CciPlotArea is { } cci) lines.Add(new(new Point(lineX, cci.Top), new Point(lineX, cci.Bottom)));
        if (WilliamsRPlotArea is { } wr) lines.Add(new(new Point(lineX, wr.Top), new Point(lineX, wr.Bottom)));
        var markers = ImmutableArray.CreateBuilder<PriceCrosshairMarker>();
        foreach (var marker in candidate.Highlights)
            if (CrosshairPaneArea(marker.Pane) is { } area)
                markers.Add(new(marker.Pane, new Point(lineX, area.Top + marker.ScreenY * area.Height)));
        var label = CreateCrosshairLabel(candidate, lineX, out var placement);
        _crosshair = candidate; _crosshairLines = lines.ToImmutable(); _crosshairHighlights = markers.ToImmutable();
        _crosshairLabel = label; _crosshairLabelPlacement = placement;
        Changed();
        return _attached && expectedRevision == FrameRevision && ReferenceEquals(_crosshair, candidate);
    }
    private Rect? CrosshairPaneArea(PriceCrosshairPane pane) => pane switch
    {
        PriceCrosshairPane.Price => PlotArea,
        PriceCrosshairPane.Cci => CciPlotArea,
        PriceCrosshairPane.WilliamsR => WilliamsRPlotArea,
        _ => null
    };
    private CrosshairLabel? CreateCrosshairLabel(PriceCrosshairSnapshot hover, double x, out string placement)
    {
        var area = FullPlotArea.Deflate(6);
        var text = Format(hover.LabelText);
        placement = "Visible";
        if (!Fits(text))
        {
            // Never silently ellipsize a number, clip a partial numeric label, or invent a
            // shortened value. The full raw/provenance record remains in diagnostics.
            text = Format("Crosshair: see diagnostics"); placement = "DetailsOnly";
        }
        if (!Fits(text)) { placement = "UnavailableLayout"; return null; }
        var width = text.Width + 12;
        var height = text.Height + 8;
        var left = x + 12;
        if (left + width > area.Right) left = x - 12 - width;
        left = Math.Clamp(left, area.Left, area.Right - width);
        return new(new Rect(left, area.Top, width, height), text);
        FormattedText Format(string value) => new(value, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 11, Palette.Text);
        bool Fits(FormattedText value) => double.IsFinite(value.Width) && double.IsFinite(value.Height) &&
            value.Width + 12 <= area.Width && value.Height + 8 <= area.Height;
    }
    private void RenderCrosshair(DrawingContext context)
    {
        if (_crosshair is null || IsAnyAnnotationToolActive) return;
        var pen = new Pen(Palette.Accent, 1, dashStyle: DashStyle.Dash);
        foreach (var line in _crosshairLines) context.DrawLine(pen, line.Start, line.End);
        foreach (var marker in _crosshairHighlights)
            if (CrosshairPaneArea(marker.Pane) is { } area)
                using (context.PushClip(area))
                    context.DrawEllipse(Palette.Surface, new Pen(Palette.Accent, 1.5), marker.Point, 3, 3);
        if (_crosshairLabel is { } label)
        {
            context.FillRectangle(Palette.Surface, label.Bounds);
            context.DrawRectangle(null, new Pen(Palette.Accent, 1), label.Bounds);
            context.DrawText(label.Text, new Point(label.Bounds.Left + 6, label.Bounds.Top + 4));
        }
    }
    private void Changed(bool textAnnotationChanged = false, bool imageAnnotationChanged = false,
        bool trendLineChanged = false, bool retracementChanged = false, bool fanChanged = false, bool arcChanged = false)
    {
        var frameRevision = FrameRevision;
        var textRevision = TextAnnotations.Revision;
        var imageRevision = ImageAnnotations.Revision;
        var trendRevision = TrendLines.Revision;
        var retracementRevision = Retracements.Revision;
        var fanRevision = Fans.Revision;
        var arcRevision = Arcs.Revision;
        var textGeneration = _textAnnotationGeneration;
        var imageGeneration = _imageAnnotationGeneration;
        var trendGeneration = _trendLineGeneration;
        var retracementGeneration = _retracementGeneration;
        var fanGeneration = _fanGeneration;
        var arcGeneration = _arcGeneration;
        var captureToRelease = _gestureCaptureToRelease;
        _gestureCaptureToRelease = null;
        if (captureToRelease is not null && ReferenceEquals(captureToRelease.Captured, this)) captureToRelease.Capture(null);
        if (!IsCurrentAnnotationState()) return;
        PruneImageAnnotationBitmaps();
        AutomationProperties.SetHelpText(this, AccessibleSummary); ToolTip.SetTip(this, SelectedHit is null && SelectedVolumeHit is null ? null : AccessibleSummary);
        InvalidateVisual();
        if (!IsCurrentAnnotationState()) return;
        StateChanged?.Invoke(this, EventArgs.Empty);
        // Nested annotation transitions publish their own settled state. Do not publish an
        // obsolete second notification after a callback has replaced/canceled either tool.
        if (!IsCurrentAnnotationState()) return;
        if (textAnnotationChanged || imageAnnotationChanged || trendLineChanged || retracementChanged || fanChanged || arcChanged) TextAnnotationToolChanged?.Invoke(this, EventArgs.Empty);
        if (!IsCurrentAnnotationState()) return;
        if (imageAnnotationChanged) ImageAnnotationToolChanged?.Invoke(this, EventArgs.Empty);
        if (!IsCurrentAnnotationState()) return;
        if (trendLineChanged) TrendLineToolChanged?.Invoke(this, EventArgs.Empty);
        if (!IsCurrentAnnotationState()) return;
        if (retracementChanged) RetracementToolChanged?.Invoke(this, EventArgs.Empty);
        if (!IsCurrentAnnotationState()) return;
        if (fanChanged) FanToolChanged?.Invoke(this, EventArgs.Empty);
        if (!IsCurrentAnnotationState()) return;
        if (arcChanged) ArcToolChanged?.Invoke(this, EventArgs.Empty);
        // No state mutation follows any external callback. A reentrant release/cancel wins.
        bool IsCurrentAnnotationState() => frameRevision == FrameRevision && textRevision == TextAnnotations.Revision &&
            imageRevision == ImageAnnotations.Revision && textGeneration == _textAnnotationGeneration &&
            imageGeneration == _imageAnnotationGeneration && trendRevision == TrendLines.Revision &&
            trendGeneration == _trendLineGeneration && retracementRevision == Retracements.Revision &&
            retracementGeneration == _retracementGeneration && fanRevision == Fans.Revision && fanGeneration == _fanGeneration &&
            arcRevision == Arcs.Revision && arcGeneration == _arcGeneration;
    }
    public bool SelectCandle(int index, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || !IsPlotAvailable || Snapshot is not { } s || index < 0 || index >= s.Candles.Length) return false;
        var notesChanged = ClearTextAnnotationSelectionCore();
        var imagesChanged = ClearImageAnnotationSelectionCore();
        var trendsChanged = ClearTrendLineSelectionCore();
        var retracementsChanged = ClearRetracementSelectionCore();
        var fansChanged = ClearFanSelectionCore();
        var arcsChanged = ClearArcSelectionCore();
        _selection = index; _volumeSelection = null; _selectionDetails = null;
        var revision = FrameRevision;
        Changed(notesChanged, imagesChanged, trendsChanged, retracementsChanged, fansChanged, arcsChanged);
        return _attached && revision == FrameRevision && _selection == index && _volumeSelection is null;
    }
    public bool SelectAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || expectedRevision != FrameRevision || !IsPlotAvailable || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !PlotArea.Contains(point) || Snapshot is not { } s) return false;
        if (_imageAnnotationPlacementArmed) return BeginImageAnnotationAt(point, expectedRevision);
        if (_textAnnotationPlacementArmed) return BeginTextAnnotationAt(point, expectedRevision);
        // Deterministic z-order: all images above all text, newest note last within each kind.
        EnsureImageAnnotationLayout();
        for (var index = _imageAnnotationBoxes.Length - 1; index >= 0; index--)
            if (_imageAnnotationBoxes[index].Bounds.Contains(point)) return SelectImageAnnotation(_imageAnnotationBoxes[index].Id);
        EnsureTextAnnotationLayout();
        for (var index = _textAnnotationBoxes.Length - 1; index >= 0; index--)
            if (_textAnnotationBoxes[index].Bounds.Contains(point)) return SelectTextAnnotation(_textAnnotationBoxes[index].Id);
        if (IsArcToolActive) return CaptureArcAnchorAt(point, expectedRevision);
        if (IsFanToolActive) return CaptureFanAnchorAt(point, expectedRevision);
        if (IsRetracementToolActive) return CaptureRetracementAnchorAt(point, expectedRevision);
        if (IsTrendLineToolActive) return CaptureTrendLineAnchorAt(point, expectedRevision);
        if (SelectArcAt(point, expectedRevision)) return true;
        if (SelectFanAt(point, expectedRevision)) return true;
        if (SelectRetracementAt(point, expectedRevision)) return true;
        if (SelectTrendLineAt(point, expectedRevision)) return true;
        var a = PlotArea; var x = (point.X - a.X) / a.Width; var y = (point.Y - a.Y) / a.Height;
        // Candles retain priority and their existing tolerance. Volume fallback tests only
        // its exact positive-height rectangle, never a tolerance-expanded or zero-height bar.
        var hit = PriceChartGeometry.HitTest(s, x, y, 5 / a.Width, 5 / a.Height);
        if (hit is not null)
            return SelectCandle(Array.FindIndex(s.Candles.ToArray(), c => c.Hit.Equals(hit.Value)), expectedRevision);
        var volumeHit = VolumeSnapshot is { } volumes ? PriceVolumeGeometry.HitTest(volumes, x, y) : null;
        var notesChanged = ClearTextAnnotationSelectionCore();
        var imagesChanged = ClearImageAnnotationSelectionCore();
        var trendsChanged = ClearTrendLineSelectionCore();
        var retracementsChanged = ClearRetracementSelectionCore();
        var fansChanged = ClearFanSelectionCore();
        var arcsChanged = ClearArcSelectionCore();
        _selection = -1; _volumeSelection = volumeHit; _selectionDetails = null;
        var revision = FrameRevision;
        Changed(notesChanged, imagesChanged, trendsChanged, retracementsChanged, fansChanged, arcsChanged);
        return volumeHit is not null && _attached && revision == FrameRevision &&
            _selection == -1 && _volumeSelection == volumeHit;
    }
    // Retire note selection/draft and its pointer frame without publishing intermediate state.
    // Callers complete candle selection before notifying either observer.
    private bool ClearTextAnnotationSelectionCore()
    {
        var changed = TextAnnotations.ClearSelection() || _textAnnotationPlacementArmed;
        _textAnnotationPlacementArmed = false;
        if (!changed) return false;
        ++_textAnnotationGeneration; ++FrameRevision; _statusRevision = -1;
        _badge = null; _badgeRevision = -1; ClearCrosshairCore();
        return true;
    }
    private bool ClearImageAnnotationSelectionCore()
    {
        var changed = ImageAnnotations.ClearSelection() || _imageAnnotationPlacementArmed;
        _imageAnnotationPlacementArmed = false;
        if (!changed) return false;
        ++_imageAnnotationGeneration; ++FrameRevision; _statusRevision = -1;
        _badge = null; _badgeRevision = -1; ClearCrosshairCore();
        return true;
    }
    public void Zoom(double factor)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        if (Snapshot is not { } s) return;
        if (_options.TimeViewport is { } time)
        {
            // Initial requests are independent of this interaction gate. Retain the
            // existing one-unit minimum, now in axis units rather than candle count.
            var length = Math.Max(1, time.Range.Length / factor);
            if (!double.IsFinite(length) || length > 90) return;
            var minimum = time.Range.Minimum + (time.Range.Length - length) / 2;
            var maximum = minimum + length;
            if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum) return;
            SetOptions(_options.WithTimeViewport(new(time.Axis, time.Constrain(new(minimum, maximum)))));
            return;
        }
        if (s.BarCount == 0) return;
        var count = (int)Math.Clamp(Math.Round(s.BarCount / factor), 1, PriceChartSource.MaximumBars);
        var start = Math.Max(0, s.BarStart + (s.BarCount - count) / 2);
        if (_source is not null) start = Math.Min(start, Math.Max(0, _source.Bars.Length - count));
        SetOptions(new(start, count, _options.GeometryBudget, _options.FixedDomain, _options.BodyWidthFraction));
    }
    public void Pan(int bars)
    {
        Dispatcher.UIThread.VerifyAccess(); if (Snapshot is not { } s || _source is null) return;
        if (_options.TimeViewport is { } time)
        {
            SetOptions(_options.WithTimeViewport(new(time.Axis, time.Constrain(time.Range.Pan(bars)))));
            return;
        }
        var start = (int)Math.Clamp((long)s.BarStart + bars, 0L, Math.Max(0, _source.Bars.Length - s.BarCount));
        SetOptions(new(start, Math.Max(1, s.BarCount), _options.GeometryBudget, _options.FixedDomain, _options.BodyWidthFraction));
    }
    public void ResetView() => SetOptions(new(geometryBudget: _options.GeometryBudget, bodyWidthFraction: _options.BodyWidthFraction,
        timeViewport: _options.TimeViewport is { Axis.FinancialWholeRange: { } whole } time ? new(time.Axis, whole) : null));
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e); _attached = true; _inputRoot = TopLevel.GetTopLevel(this);
        InvalidateFrame();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false; _inputRoot = null;
        var notesChanged = ClearTextAnnotationSelectionCore();
        var imagesChanged = ClearImageAnnotationSelectionCore();
        var trendsChanged = ClearTrendLineSelectionCore();
        var retracementsChanged = ClearRetracementSelectionCore();
        var fansChanged = ClearFanSelectionCore();
        var arcsChanged = ClearArcSelectionCore();
        ReleaseImageAnnotationBitmaps();
        _imageAnnotationLayouts = []; _imageAnnotationBoxes = []; _imageAnnotationLayoutRevision = -1;
        _pointerFrames.Clear(); _releaseFrames.Clear(); _hoverFrames.Clear(); _keyFrames.Clear(); _wheelFrames.Clear();
        ClearArcLayout();
        base.OnDetachedFromVisualTree(e);
        InvalidateFrame(notesChanged, imagesChanged, trendsChanged, retracementChanged: retracementsChanged, fanChanged: fansChanged, arcChanged: arcsChanged);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PaletteProperty || change.Property == ChartThemeProperty)
        {
            if (change.Property == ChartThemeProperty)
                _seriesBrushes = ChartTheme is { } theme
                    ? theme.SeriesColors.Take(3).Select(color => (IBrush)new global::Avalonia.Media.Immutable.ImmutableSolidColorBrush(color)).ToArray() : [];
            // Paint changes keep the immutable data frame, authoring intent and hit identities.
            _textAnnotationLayoutPalette = null;
            if (_crosshair is { } hover)
                _crosshairLabel = CreateCrosshairLabel(hover, PlotArea.Left + hover.ScreenX * PlotArea.Width, out _crosshairLabelPlacement);
            InvalidateVisual();
        }
        if (change.Property == BoundsProperty)
            // Switching from the external note editor can resize the plot before any
            // trend anchor exists. Preserve that empty intent, never a captured point.
            InvalidateFrame(preserveTrendTool: TrendGesture is { First: null, Pointer: null },
                preserveRetracementTool: RetracementGesture is { First: null, Pointer: null },
                preserveFanTool: FanGesture is { First: null, Pointer: null },
                preserveArcTool: ArcGesture is { First: null, Pointer: null });
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // Root tunneling captures before target focus; event-keyed weak storage isolates nested input.
        var hasFrame = _pointerFrames.TryGetValue(e, out var frame);
        _pointerFrames.Remove(e);
        base.OnPointerPressed(e);
        // Detach clears captured events. Reattachment cannot authorize the abandoned press
        // against its new frame; only a freshly root-captured event may select.
        if (!hasFrame || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            !PlotArea.Contains(e.GetPosition(this)) || !IsCurrentPointerFrame(frame!)) return;
        e.Handled = true;
        Focus();
        if (!IsCurrentPointerFrame(frame!)) return;
        var point = e.GetPosition(this);
        if (_imageAnnotationPlacementArmed) { BeginImageAnnotationAt(point, frame!.Revision); return; }
        if (_textAnnotationPlacementArmed) { BeginTextAnnotationAt(point, frame!.Revision); return; }
        if (e.ClickCount >= 2 && SelectImageAnnotationAt(point, frame!.Revision, beginEdit: true)) return;
        if (e.ClickCount >= 2 && SelectTextAnnotationAt(point, frame!.Revision, beginEdit: true)) return;
        if (_twoAnchorGesture is { } gesture)
        {
            if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
            if (VisibleImageAnnotationBoxes.Any(box => box.Bounds.Contains(point)) ||
                VisibleTextAnnotationBoxes.Any(box => box.Bounds.Contains(point)))
            { SelectAt(point, frame!.Revision); return; }
            if (FindTwoAnchorAt(gesture, point, frame!.Revision) is { } anchor)
                CaptureTwoAnchorCore(gesture, anchor, gesture.First is null && gesture.EndpointEdit == 0 ? e.Pointer : null, point);
            return;
        }
        if (e.ClickCount >= 2 && SelectArcAt(point, frame!.Revision, beginEdit: true)) return;
        if (e.ClickCount >= 2 && SelectFanAt(point, frame!.Revision, beginEdit: true)) return;
        if (e.ClickCount >= 2 && SelectRetracementAt(point, frame!.Revision, beginEdit: true)) return;
        if (e.ClickCount >= 2 && SelectTrendLineAt(point, frame!.Revision, beginEdit: true)) return;
        SelectAt(point, frame!.Revision);
    }
    private bool IsCurrentPointerFrame(PointerFrame frame) => _attached && frame.Revision == FrameRevision &&
        frame.AnnotationRevision == TextAnnotations.Revision && frame.AnnotationGeneration == _textAnnotationGeneration &&
        frame.ImageAnnotationRevision == ImageAnnotations.Revision && frame.ImageAnnotationGeneration == _imageAnnotationGeneration &&
        frame.TrendRevision == TrendLines.Revision && frame.TrendGeneration == _trendLineGeneration &&
        frame.RetracementRevision == Retracements.Revision && frame.RetracementGeneration == _retracementGeneration &&
        frame.FanRevision == Fans.Revision && frame.FanGeneration == _fanGeneration &&
        frame.ArcRevision == Arcs.Revision && frame.ArcGeneration == _arcGeneration;
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var hasFrame = _hoverFrames.TryGetValue(e, out var frame);
        _hoverFrames.Remove(e);
        base.OnPointerMoved(e);
        if (!hasFrame || frame!.Generation != _hoverGeneration || frame.Revision != FrameRevision ||
            frame.TrendGeneration != _trendLineGeneration || frame.RetracementGeneration != _retracementGeneration ||
            frame.FanGeneration != _fanGeneration || frame.ArcRevision != Arcs.Revision || frame.ArcGeneration != _arcGeneration) return;
        if (_twoAnchorGesture is { } gesture)
        {
            if (gesture.Pointer is { } pointer)
            {
                if (!ReferenceEquals(pointer, e.Pointer) || !IsCurrentTwoAnchorDrag(gesture)) return;
                gesture.DragMoved |= TrendDistance(e.GetPosition(this), gesture.Origin, gesture.Origin) >= 3;
            }
            UpdateTwoAnchorPreviewAt(gesture, e.GetPosition(this), frame.Revision);
            return;
        }
        UpdateCrosshairAt(e.GetPosition(this), frame.Revision);
    }
    private bool IsCurrentTwoAnchorDrag(TwoAnchorGesture gesture) => IsCurrentTwoAnchorGesture(gesture) &&
        gesture.Pointer is { } pointer && ReferenceEquals(pointer.Captured, this);
    private void UpdateTwoAnchorPreviewAt(TwoAnchorGesture gesture, Point point, long expectedRevision)
    {
        if (gesture.First is null || !CanCaptureTwoAnchor(gesture, expectedRevision)) return;
        var anchor = FindTwoAnchorAt(gesture, point, expectedRevision);
        if (anchor?.Hit.Key == gesture.First?.Hit.Key) anchor = null;
        if (gesture.Preview == anchor) return;
        gesture.Preview = anchor;
        Changed(trendLineChanged: ReferenceEquals(gesture.Store, TrendLines),
            retracementChanged: ReferenceEquals(gesture.Store, Retracements),
            fanChanged: ReferenceEquals(gesture.Store, Fans), arcChanged: ReferenceEquals(gesture.Store, Arcs));
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var hasFrame = _releaseFrames.TryGetValue(e, out var frame);
        _releaseFrames.Remove(e);
        var gesture = _twoAnchorGesture;
        base.OnPointerReleased(e);
        // InitialPressMouseButton remains Left during a chorded right-button release.
        // Only the actual left release may finish the left-owned drag.
        if (!hasFrame || gesture is null || e.InitialPressMouseButton != MouseButton.Left ||
            e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased ||
            !ReferenceEquals(gesture.Pointer, e.Pointer) || !IsCurrentPointerFrame(frame!) || !IsCurrentTwoAnchorDrag(gesture)) return;
        e.Handled = true;
        var point = e.GetPosition(this);
        var moved = gesture.DragMoved || TrendDistance(point, gesture.Origin, gesture.Origin) >= 3;
        if (moved && FindTwoAnchorAt(gesture, point, frame!.Revision) is { } second && gesture.First is { } first &&
            CommitTwoAnchors(gesture, first, second)) return;
        // Invalid releases and the first simple click retain only the exact authored first point.
        if (!IsCurrentTwoAnchorDrag(gesture)) return;
        _gestureCaptureToRelease = gesture.Pointer;
        gesture.Pointer = null; gesture.DragMoved = false; gesture.Preview = null;
        AdvanceTwoAnchorGeneration(gesture);
        InvalidateTwoAnchorGesture(gesture);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        var gesture = _twoAnchorGesture;
        var pointer = gesture?.Pointer;
        var generation = gesture?.Generation;
        var arcRevision = Arcs.Revision;
        var arcGeneration = _arcGeneration;
        base.OnPointerCaptureLost(e);
        // Capture-lost handlers may synchronously arm/capture a newer gesture. Never let
        // the old loss borrow its newly assigned pointer or generation. A benign focus/frame
        // refresh preserves this same ownership and must not hide a genuine capture loss.
        if (gesture is null || pointer is null || !ReferenceEquals(_twoAnchorGesture, gesture) ||
            !ReferenceEquals(pointer, e.Pointer) || !ReferenceEquals(gesture.Pointer, pointer) ||
            gesture.Generation != generation || arcRevision != Arcs.Revision || arcGeneration != _arcGeneration) return;
        if (ReferenceEquals(gesture.Store, TrendLines)) CancelTrendLineTool();
        else if (ReferenceEquals(gesture.Store, Retracements)) CancelRetracementTool();
        else if (ReferenceEquals(gesture.Store, Fans)) CancelFanTool();
        else if (ReferenceEquals(gesture.Store, Arcs)) CancelArcTool();
    }
    protected override void OnPointerExited(PointerEventArgs e)
    {
        var revision = FrameRevision;
        var generation = _hoverGeneration;
        var arcRevision = Arcs.Revision;
        var arcGeneration = _arcGeneration;
        var previous = _crosshair;
        base.OnPointerExited(e);
        if (revision == FrameRevision && generation == _hoverGeneration &&
            arcRevision == Arcs.Revision && arcGeneration == _arcGeneration &&
            ReferenceEquals(previous, _crosshair)) ClearCrosshair();
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var hasFrame = _wheelFrames.TryGetValue(e, out var frame);
        _wheelFrames.Remove(e);
        base.OnPointerWheelChanged(e);
        // Root-captured wheel intent cannot zoom away a newer tool armed during routing.
        if (!hasFrame || !IsCurrentPointerFrame(frame!)) return;
        var position = e.GetPosition(this);
        if (!IsPlotAvailable || (!PlotArea.Contains(position) && !(CciPlotArea?.Contains(position) ?? false) && !(WilliamsRPlotArea?.Contains(position) ?? false)) || e.Delta.Y == 0) return;
        Zoom(e.Delta.Y > 0 ? 1.5 : 1 / 1.5); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var hasFrame = _keyFrames.TryGetValue(e, out var frame);
        _keyFrames.Remove(e);
        base.OnKeyDown(e);
        // A queued key belongs to the root-captured frame, never a replacement tool armed by a handler.
        if (!hasFrame || !IsCurrentPointerFrame(frame!)) return;
        switch (e.Key)
        {
            case Key.Left: case Key.Right:
                if (_twoAnchorGesture is { } gesture && CanCaptureTwoAnchor(gesture, FrameRevision) &&
                    Snapshot is { Candles.Length: > 0 } trendPrices)
                {
                    gesture.KeyboardIndex = gesture.KeyboardIndex < 0
                        ? e.Key == Key.Right ? 0 : trendPrices.Candles.Length - 1
                        : Math.Clamp(gesture.KeyboardIndex + (e.Key == Key.Right ? 1 : -1), 0, trendPrices.Candles.Length - 1);
                    _selection = gesture.KeyboardIndex; _volumeSelection = null; _selectionDetails = null;
                    Changed();
                    break;
                }
                if (Snapshot is { Candles.Length: > 0 } s) SelectCandle(_selection < 0 ? (e.Key == Key.Right ? 0 : s.Candles.Length - 1) : Math.Clamp(_selection + (e.Key == Key.Right ? 1 : -1), 0, s.Candles.Length - 1), FrameRevision); break;
            case Key.Enter:
                if (_twoAnchorGesture is not { KeyboardIndex: >= 0 } keyboardGesture) return;
                CaptureTwoAnchorAtIndex(keyboardGesture, keyboardGesture.KeyboardIndex, FrameRevision);
                break;
            case Key.Add: case Key.OemPlus: Zoom(1.5); break;
            case Key.Subtract: case Key.OemMinus: Zoom(1 / 1.5); break;
            case Key.PageDown: Pan(Math.Max(1, (int)((_options.TimeViewport?.Range.Length ?? Snapshot?.BarCount ?? 1) / 5))); break;
            case Key.PageUp: Pan(-Math.Max(1, (int)((_options.TimeViewport?.Range.Length ?? Snapshot?.BarCount ?? 1) / 5))); break;
            case Key.Home: ResetView(); break;
            case Key.F2:
                if (ImageAnnotations.SelectedId is not null)
                {
                    if (!BeginSelectedImageAnnotationEdit()) return;
                }
                else if (TextAnnotations.SelectedId is not null)
                {
                    if (!BeginSelectedTextAnnotationEdit()) return;
                }
                else if (Arcs.SelectedId is not null)
                {
                    if (!BeginSelectedArcEdit()) return;
                }
                else if (Fans.SelectedId is not null)
                {
                    if (!BeginSelectedFanEdit()) return;
                }
                else if (Retracements.SelectedId is not null)
                {
                    if (!BeginSelectedRetracementEdit()) return;
                }
                else if (!BeginSelectedTrendLineEdit()) return;
                break;
            case Key.Delete:
                if (ImageAnnotations.SelectedId is not null) RemoveSelectedImageAnnotation();
                else if (TextAnnotations.SelectedId is not null) RemoveSelectedTextAnnotation();
                else if (Arcs.SelectedId is not null) RemoveSelectedArc();
                else if (Fans.SelectedId is not null) RemoveSelectedFan();
                else if (Retracements.SelectedId is not null) RemoveSelectedRetracement();
                else if (TrendLines.SelectedId is not null) RemoveSelectedTrendLine();
                else return;
                break;
            case Key.Escape:
                if (IsAnyAnnotationToolActive) CancelAnnotationTools();
                else if (ImageAnnotations.SelectedId is not null) ClearImageAnnotationSelection();
                else if (TextAnnotations.SelectedId is not null) ClearTextAnnotationSelection();
                else if (Arcs.SelectedId is not null) ClearArcSelection();
                else if (Fans.SelectedId is not null) ClearFanSelection();
                else if (Retracements.SelectedId is not null) ClearRetracementSelection();
                else if (TrendLines.SelectedId is not null) ClearTrendLineSelection();
                else CancelInteraction();
                break;
            default: return;
        }
        e.Handled = true;
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Palette.Surface, new Rect(Bounds.Size));
        if (IsFocused) context.DrawRectangle(null, new Pen(Palette.Accent, 2), new Rect(Bounds.Size).Deflate(1));
        DrawingText.Draw(context, AggregationSnapshot is { } aggregation
            ? $"Aggregated OHLC / Volume · {PriceTimeAggregator.GetLabel(aggregation.Interval)} UTC · {aggregation.OriginalSource.Label} · {(_options.TimeViewport is null ? "ordinal X" : "compressed time X")}"
            : $"OHLC · {_source?.Label} · ordinal X (not elapsed time)", new(10, 8), Palette.Text, 12, Math.Max(1, Bounds.Width - 20));
        DrawingText.Draw(context, "Rise", new(10, 30), Palette.Accent, 11, 60);
        DrawingText.Draw(context, "Fall", new(75, 30), Palette.Error, 11, 60);
        DrawingText.Draw(context, "Doji = exact-price horizontal line", new(140, 30), Palette.Text, 11, Math.Max(1, Bounds.Width - 150));
        var a = PlotArea;
        var frame = _frame;
        if (frame.Snapshot is { } s && IsPlotAvailable)
        {
            foreach (var tick in s.Ticks)
            {
                var y = a.Bottom - tick.Position * a.Height;
                context.DrawLine(new Pen(Palette.GridLine, 1), new(a.Left, y), new(a.Right, y));
                DrawingText.Draw(context, tick.Value.ToString("G4", CultureInfo.InvariantCulture), new(5, y - 7), Palette.Muted, 10, 67);
            }
            context.DrawRectangle(null, new Pen(Palette.Muted, 1), a);
            using (context.PushClip(a))
            {
                // Volume shares the price rectangle and ordinal X, but has independent Y.
                // Draw it first so the existing BB curves, candle bodies and wicks stay on top.
                if (frame.VolumeValues is { } volumes)
                    foreach (var column in volumes.Columns)
                    {
                        var body = column.Body;
                        if (body.Height > 0) context.FillRectangle(VolumeBrush,
                            new Rect(a.X + body.Left * a.Width, a.Y + body.Top * a.Height,
                                body.Width * a.Width, body.Height * a.Height));
                    }
                // These are the same immutable clipped segments counted by the public diagnostic.
                // They are display-only and never enter OHLC hit testing or raw selection values.
                var middlePen = new Pen(BollingerBrush, 1.5, dashStyle: DashStyle.Dash);
                var outerPen = new Pen(BollingerBrush, 1);
                foreach (var segment in frame.BollingerSegments)
                {
                    var line = segment.Line;
                    context.DrawLine(segment.IsMiddle ? middlePen : outerPen,
                        new(a.X + line.Start.X * a.Width, a.Y + line.Start.Y * a.Height),
                        new(a.X + line.End.X * a.Width, a.Y + line.End.Y * a.Height));
                }
                foreach (var c in s.Candles)
                {
                    var brush = c.Hit.Bar.Close > c.Hit.Bar.Open ? Palette.Accent : c.Hit.Bar.Close < c.Hit.Bar.Open ? Palette.Error : Palette.Text;
                    if (c.Wick is { } w) context.DrawLine(new Pen(brush, 2), new(a.X + w.Start.X * a.Width, a.Y + w.Start.Y * a.Height), new(a.X + w.End.X * a.Width, a.Y + w.End.Y * a.Height));
                    if (c.Body is { } b)
                    {
                        var r = new Rect(a.X + b.Left * a.Width, a.Y + b.Top * a.Height, b.Width * a.Width, b.Height * a.Height);
                        if (b.Height == 0) context.DrawLine(new Pen(brush, 2), r.TopLeft, r.TopRight);
                        else context.FillRectangle(brush, r);
                    }
                }
                if (_selection >= 0 && _selection < s.Candles.Length)
                {
                    var c = s.Candles[_selection]; var x = a.X + c.CenterX * a.Width;
                    context.DrawLine(new Pen(Palette.Text, 1), new(x, a.Top), new(x, a.Bottom));
                }
                else if (_volumeSelection is { } selectedVolume && frame.VolumeValues is { } selectedVolumes)
                {
                    foreach (var column in selectedVolumes.Columns)
                    {
                        if (column.Hit != selectedVolume) continue;
                        var x = a.X + s.GetNormalizedX(column.BarIndex) * a.Width;
                        context.DrawLine(new Pen(Palette.Text, 1), new(x, a.Top), new(x, a.Bottom));
                        break;
                    }
                }
            }
            if (GetCurrentCloseBadge() is { } badge)
            {
                // Only a short axis tick, not an unverified whole-plot current-price line.
                context.DrawLine(new Pen(CurrentCloseBrush, 1), badge.AxisPoint,
                    new Point(badge.Bounds.Left, badge.AxisPoint.Y));
                context.FillRectangle(CurrentCloseBrush, badge.Bounds);
                context.DrawText(badge.Text, new Point(badge.Bounds.Left + 2, badge.Bounds.Top + 2));
            }
            var axisBottom = a.Bottom;
            if (CciPlotArea is { } cciArea)
            {
                DrawingText.Draw(context, $"CCI(14){PeriodUnit} · HLC3 compatibility · display [-150,150]", new(cciArea.X, cciArea.Y - 20), CciBrush, 11, Math.Max(1, cciArea.Width));
                // The display bounds are not numerical clamps. Only +/-100 and zero are
                // labelled so labels remain legible even in the minimum-height pane.
                foreach (var value in new[] { 100, 0, -100 })
                {
                    var y = cciArea.Top + (150 - value) / 300d * cciArea.Height;
                    context.DrawLine(new Pen(Palette.GridLine, 1, dashStyle: value == 0 ? null : DashStyle.Dash), new(cciArea.Left, y), new(cciArea.Right, y));
                    DrawingText.Draw(context, value.ToString(CultureInfo.InvariantCulture), new(25, y - 7), Palette.Muted, 10, 45);
                }
                context.DrawRectangle(null, new Pen(Palette.Muted, 1), cciArea);
                using (context.PushClip(cciArea))
                {
                    var pen = new Pen(CciBrush, 2);
                    // This is the exact geometry exposed by VisibleCciSegmentCount.
                    foreach (var line in frame.CciSegments)
                        context.DrawLine(pen,
                            new(cciArea.X + line.Start.X * cciArea.Width, cciArea.Y + line.Start.Y * cciArea.Height),
                            new(cciArea.X + line.End.X * cciArea.Width, cciArea.Y + line.End.Y * cciArea.Height));
                }
                axisBottom = cciArea.Bottom;
            }
            if (WilliamsRPlotArea is { } indicatorArea)
            {
                DrawingText.Draw(context, $"Williams %R(14){PeriodUnit} · fixed [-100,0]", new(indicatorArea.X, indicatorArea.Y - 20), WilliamsRBrush, 11, Math.Max(1, indicatorArea.Width));
                foreach (var value in new[] { 0, -20, -80, -100 })
                {
                    var y = indicatorArea.Top - value / 100d * indicatorArea.Height;
                    context.DrawLine(new Pen(Palette.GridLine, 1), new(indicatorArea.Left, y), new(indicatorArea.Right, y));
                    DrawingText.Draw(context, value.ToString(CultureInfo.InvariantCulture), new(30, y - 7), Palette.Muted, 10, 40);
                }
                context.DrawRectangle(null, new Pen(Palette.Muted, 1), indicatorArea);
                using (context.PushClip(indicatorArea))
                {
                    var pen = new Pen(WilliamsRBrush, 2);
                    // This is the exact immutable geometry exposed by VisibleWilliamsRSegmentCount.
                    foreach (var line in frame.WilliamsRSegments)
                        context.DrawLine(pen,
                            new(indicatorArea.X + line.Start.X * indicatorArea.Width, indicatorArea.Y + line.Start.Y * indicatorArea.Height),
                            new(indicatorArea.X + line.End.X * indicatorArea.Width, indicatorArea.Y + line.End.Y * indicatorArea.Height));
                }
                axisBottom = indicatorArea.Bottom;
            }
            RenderCrosshair(context);
            RenderTrendLines(context);
            RenderRetracements(context);
            RenderFans(context);
            RenderArcs(context);
            RenderTextAnnotations(context);
            RenderImageAnnotations(context);
            if (_options.TimeViewport is { } time)
            {
                foreach (var tick in time.CreateTicks(Math.Clamp((int)(a.Width / 115), 2, 6)))
                {
                    var x = a.X + tick.Position * a.Width;
                    context.DrawLine(new Pen(Palette.Muted, 1), new(x, axisBottom), new(x, axisBottom + 4));
                    DrawingText.Draw(context, tick.Timestamp.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
                        new(Math.Clamp(x - 43, a.Left, Math.Max(a.Left, a.Right - 86)), axisBottom + 9), Palette.Muted, 10, 86);
                }
            }
            else DrawingText.Draw(context, AggregationSnapshot is null
                ? "Earlier ordinal bars → later ordinal bars · timestamps in selection"
                : "Earlier → later aggregate segments · ordinal X, not elapsed time · UTC bucket / originals in selection", new(a.X, axisBottom + 9), Palette.Muted, 10, Math.Max(1, a.Width));
        }
        DrawingText.Draw(context, IsPlotAvailable ? Status : InsufficientSpaceMessage, new(10, Math.Max(0, Bounds.Height - 40)), Palette.Muted, 11, Math.Max(1, Bounds.Width - 20));
        if (SelectedHit is not null || SelectedVolumeHit is not null) DrawingText.Draw(context, AccessibleSummary, new(10, Math.Max(0, Bounds.Height - 22)), Palette.Text, 10, Math.Max(1, Bounds.Width - 20));
    }
}
