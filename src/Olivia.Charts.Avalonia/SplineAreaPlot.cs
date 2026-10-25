using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Olivia.Core.Charts;

namespace Olivia.Avalonia;

/// <summary>Independent glyphs for the official model selector. Names identify intent, not
/// DevExpress theme/pixel equivalence. Custom uses concentric rings and immediate 1.5 hover
/// scale; proprietary BackEase/CircleEase and chart on-load animation are not reproduced.</summary>
public enum SplineAreaMarkerKind
{
    BorderlessSimple, Simple, Circle, Cross, Dollar, Polygon, Ring, Square,
    Star, Triangle, InvertedTriangle, Diamond, Plus, Pentagon, Custom
}

/// <summary>Accepted data presentation, independent of geometry mode. Changing only options
/// must never relabel vendor observations as fuel prices or vice versa.</summary>
public enum SplineAreaPresentation { VendorCosts, FuelPrices, Numeric }

/// <summary>Ordinary, independently zero-filled linear, spline or standard step areas. Only admitted original
/// samples are selectable. Fill fragments are rasterized together once per series, with strokes
/// drawn only from admitted actual boundaries, never from fill closure or viewport edges.</summary>
public sealed class SplineAreaPlot : Control
{
    private AreaChartOptions _options = new(mode: AreaChartMode.SplineArea, includeZeroInAutoRange: false);
    private ImmutableArray<AreaChartHit> _originals = [];
    private ImmutableArray<AreaChartSegment> _boundarySegments = [];
    private int _selection = -1;
    private bool _attached;
    private bool _showLabels = true;
    private bool _showMarkers = true;
    private double _transparency = .3;
    private double _labelAngle = 45;
    private double _markerSize = 20;
    private SplineAreaMarkerKind _markerKind = SplineAreaMarkerKind.Ring;
    private double? _hoverArgument;
    private AreaChartHit? _hoveredMarker;
    private UiPalette _palette = UiPalette.Dark;
    private SplineAreaPresentation _presentation = SplineAreaPresentation.VendorCosts;
    private const double LabelFontSize = 10, LabelMaxWidth = 46;
    private Dictionary<string, Size> _labelSizes = [];
    private Size _maximumLabelSize;
    private Rect? _plotArea;

    public SplineAreaPlot()
    {
        Name = "SplineAreaPlot"; Focusable = true; ClipToBounds = true; MinHeight = 100;
        GotFocus += OnFocusChanged; LostFocus += OnFocusChanged;
        UpdateAccessibleName();
    }

    public AreaChartSnapshot? Snapshot { get; private set; }
    public AreaChartSource? Source => Snapshot?.Source;
    public AreaChartOptions Options => _options;
    public SplineAreaPresentation Presentation => _presentation;
    public string ChartTitle => Presentation == SplineAreaPresentation.Numeric ? "Series comparison" : Presentation == SplineAreaPresentation.FuelPrices
        ? "U.S. No 2 Diesel Ultra Low Sulfur Retail Prices" : "Outside Vendor Costs";
    public string ValueUnit => Presentation == SplineAreaPresentation.Numeric ? "Units" : Presentation == SplineAreaPresentation.FuelPrices ? "$ per Gallon" : "Thousands US$";
    public long FrameRevision { get; private set; }
    public AreaChartHit? SelectedHit => _selection >= 0 && _selection < _originals.Length ? _originals[_selection] : null;
    public AreaChartPoint? SelectedPoint
    {
        get
        {
            if (SelectedHit is not { } hit || Snapshot is not { } snapshot) return null;
            foreach (var point in snapshot.Points) if (point.Hit.Equals(hit)) return point;
            return null;
        }
    }
    public Point? MarkerPoint => IsPlotAvailable && SelectedPoint is { } point ? At(point.X, point.Y) : null;
    /// <summary>Admitted original-provenance stroke segments, including the separate exact
    /// Line projection used for LinearArea and zero-tension splines, or StepLineAfter
    /// for StepAreaAfter regardless of remembered spline tension. They do not include
    /// zero-fill closing edges.</summary>
    public ImmutableArray<AreaChartSegment> BoundarySegments => _boundarySegments;
    /// <summary>Physical clip rectangle. Automatic axes leave room for complete original
    /// glyphs and labels inside it; explicitly fixed axes still clip at their exact domain.</summary>
    public Rect ViewportArea => new(66, 72, Math.Max(0, Bounds.Width - 86), Math.Max(0, Bounds.Height - 120));
    /// <summary>Normalized data-to-screen rectangle, shared by geometry, ticks and input.
    /// Insets are screen-space only: resizing or styling never requeries or expands data domains.</summary>
    public Rect PlotArea => _plotArea ??= CreatePlotArea();
    private Rect CreatePlotArea()
    {
        var viewport = ViewportArea;
        // Include the selection stroke and reserve Custom's maximum hover size in advance,
        // so selecting or hovering an extreme never clips it or moves the accepted frame.
        var radius = ShowMarkers ? MarkerSize / 2 + 4 : 8;
        if (ShowMarkers && MarkerKind == SplineAreaMarkerKind.Custom) radius = Math.Max(radius, MarkerSize * .75);
        if (ShowMarkers && MarkerKind == SplineAreaMarkerKind.Dollar)
        {
            var glyph = new FormattedText("$", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, MarkerSize, Palette.Text);
            radius = Math.Max(radius, Math.Max(glyph.Width, glyph.Height) / 2);
        }
        var left = radius; var right = radius; var top = radius; var bottom = radius;
        if (ShowLabels && _labelSizes.Count > 0)
        {
            var offset = LabelOffset(_maximumLabelSize);
            left = Math.Max(left, -offset.X); right = Math.Max(right, offset.X + _maximumLabelSize.Width);
            top = Math.Max(top, -offset.Y); bottom = Math.Max(bottom, offset.Y + _maximumLabelSize.Height);
        }
        // Two physical pixels protect antialiasing and keep text off the viewport edge.
        left = Options.FixedXDomain.HasValue ? 0 : left + 2;
        right = Options.FixedXDomain.HasValue ? 0 : right + 2;
        top = Options.FixedYDomain.HasValue ? 0 : top + 2;
        bottom = Options.FixedYDomain.HasValue ? 0 : bottom + 2;
        return new(viewport.X + left, viewport.Y + top,
            Math.Max(0, viewport.Width - left - right), Math.Max(0, viewport.Height - top - bottom));
    }
    public bool IsPlotAvailable => Bounds.Width >= 220 && PlotArea.Width >= 120 && PlotArea.Height >= 35;
    public double? HoverArgument => _hoverArgument;
    public AreaChartHit? HoveredMarkerHit => _hoveredMarker;
    public bool CrosshairEnabled => !ShowLabels;
    public ImmutableArray<AreaChartPoint> HoverPoints => _hoverArgument is { } argument && Snapshot is { } snapshot
        ? snapshot.Points.Where(point => point.Hit.X == argument).ToImmutableArray() : [];
    public double? CrosshairX
    {
        get
        {
            if (!CrosshairEnabled || !IsPlotAvailable || _hoverArgument is not { } argument || Snapshot is not { } snapshot) return null;
            foreach (var point in snapshot.Points) if (point.Hit.X == argument) return At(point.X, point.Y).X;
            return null;
        }
    }
    public UiPalette Palette
    {
        get => _palette;
        set { Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(value); _palette = value; InvalidateFrame(); }
    }
    public bool ShowLabels
    {
        get => _showLabels;
        set { Dispatcher.UIThread.VerifyAccess(); if (_showLabels == value) return; _showLabels = value; InvalidateFrame(); }
    }
    public bool ShowMarkers
    {
        get => _showMarkers;
        set { Dispatcher.UIThread.VerifyAccess(); if (_showMarkers == value) return; _showMarkers = value; InvalidateFrame(); }
    }
    public double Transparency
    {
        get => _transparency;
        set { VerifyRange(value, 0, 1); if (_transparency == value) return; _transparency = value; InvalidateFrame(); }
    }
    public double LabelAngle
    {
        get => _labelAngle;
        set { VerifyRange(value, 0, 360); if (_labelAngle == value) return; _labelAngle = value; InvalidateFrame(); }
    }
    public double MarkerSize
    {
        get => _markerSize;
        set { VerifyRange(value, 10, 40); if (_markerSize == value) return; _markerSize = value; InvalidateFrame(); }
    }
    public SplineAreaMarkerKind MarkerKind
    {
        get => _markerKind;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (_markerKind == value) return;
            _markerKind = value; InvalidateFrame();
        }
    }
    private static void VerifyRange(double value, double minimum, double maximum)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!double.IsFinite(value) || value < minimum || value > maximum) throw new ArgumentOutOfRangeException(nameof(value));
    }
    private string ModeDescription => Options.Mode switch
    {
        AreaChartMode.LinearArea => "area",
        AreaChartMode.StepAreaAfter => "step area",
        _ => "spline area"
    };
    private string ArgumentName => Presentation == SplineAreaPresentation.Numeric ? "Period" : Presentation == SplineAreaPresentation.FuelPrices ? "Month" : "Year";
    private string ObservationName => Presentation == SplineAreaPresentation.Numeric ? "values" : Presentation == SplineAreaPresentation.FuelPrices ? "prices" : "costs";
    public string Status => Snapshot is not { } snapshot ? $"No {ModeDescription} source"
        : $"Independent {ModeDescription} · {snapshot.Source.RawEntryCount} original observations · {snapshot.Points.Length} visible · {snapshot.Source.Series.Length} series"
            + (Options.Mode == AreaChartMode.SplineArea
                ? " · " + (Options.SplineAlgorithm == AreaSplineAlgorithm.StrictMinMax ? "Strict Min-Max" : "Cardinal")
                    + " · tension " + Options.LineTension.ToString("0.0", CultureInfo.InvariantCulture)
                : Options.Mode == AreaChartMode.StepAreaAfter
                    ? " · standard hold-left then jump at right X" : " · linear boundaries")
            + $" · dataset {snapshot.DatasetVersion}";
    public string SelectionReadback => SelectedHit is { } hit
        ? DescribeOriginal(hit) + (SelectedPoint.HasValue ? " · visible original" : " · retained outside observation; no selection marker")
        : "No selected observation. Click an original marker or use Left / Right, Home / End.";
    public string GroupedReadback
    {
        get
        {
            if (!CrosshairEnabled) return $"Hide labels to inspect grouped original-{ArgumentName.ToLowerInvariant()} {ObservationName} with the hover crosshair.";
            if (_hoverArgument is not { } argument || Snapshot is not { } snapshot)
                return $"Move over the plot to inspect an original {ArgumentName.ToLowerInvariant()}. Interpolated values and step corners are not observations.";
            var rows = snapshot.Source.Series.SelectMany(series => series.Samples
                .Where(sample => !sample.IsGap && sample.X == argument)
                .Select(sample => $"{series.Label}: {FormatValue(sample.Y)}"));
            return $"{ArgumentName}: {FormatArgument(argument)} · " + string.Join(" · ", rows);
        }
    }
    public string AccessibleSummary => $"{ChartTitle}. {ValueUnit}. {Status}. {SelectionReadback}. {GroupedReadback}";
    public event EventHandler? StateChanged;

    public void SetSource(AreaChartSource source, AreaChartOptions? options = null)
        => SetSource(source, options, null);

    public void SetSource(AreaChartSource source, AreaChartOptions? options,
        SplineAreaPresentation? presentation)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(source);
        var next = options ?? _options; var nextPresentation = presentation ?? _presentation;
        CheckOptions(next);
        if (!Enum.IsDefined(nextPresentation)) throw new ArgumentOutOfRangeException(nameof(presentation));
        // Query and admit fill and its exact matching stroke before changing any accepted state.
        AcceptSnapshot(source.Query(next), nextPresentation);
    }
    public void SetOptions(AreaChartOptions options)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(options); CheckOptions(options);
        if (Source is { } source) AcceptSnapshot(source.Query(options), _presentation);
        else { _options = options; InvalidateFrame(); }
    }
    private static void CheckOptions(AreaChartOptions options)
    {
        if (options.Mode is not (AreaChartMode.LinearArea or AreaChartMode.SplineArea or AreaChartMode.StepAreaAfter))
            throw new ArgumentException("SplineAreaPlot requires ordinary LinearArea, SplineArea or standard StepAreaAfter mode.", nameof(options));
    }
    private void AcceptSnapshot(AreaChartSnapshot snapshot, SplineAreaPresentation presentation)
    {
        if (snapshot.BudgetStatus != ChartBudgetStatus.Satisfied ||
            (snapshot.SplineStatus is { } status && status != AreaSplineStatus.Ready))
            throw new ArgumentException($"Area snapshot rejected: {snapshot.SplineStatus?.ToString() ?? snapshot.BudgetStatus.ToString()}. The previous accepted snapshot is unchanged.", nameof(snapshot));
        var boundaries = snapshot.Segments;
        var boundaryMode = snapshot.Options.Mode switch
        {
            AreaChartMode.StepAreaAfter => AreaChartMode.StepLineAfter,
            AreaChartMode.LinearArea => AreaChartMode.Line,
            AreaChartMode.SplineArea when snapshot.Options.LineTension == 0 => AreaChartMode.Line,
            _ => (AreaChartMode?)null
        };
        if (boundaryMode.HasValue)
        {
            // Area fills deliberately contain no strokes. Query the existing bounded path
            // with exactly the accepted domains and budgets. Never outline closing edges,
            // and never let remembered zero spline tension turn a step boundary into a line.
            var o = snapshot.Options;
            var boundary = snapshot.Source.Query(new AreaChartOptions(o.GeometryBudget, snapshot.XDomain, snapshot.YDomain,
                o.PolygonBudget, o.VertexBudget, o.WorkBudget, boundaryMode.Value, o.SegmentBudget, o.StrokeVertexBudget,
                o.SplineAlgorithm, o.LineTension, o.IncludeZeroInAutoRange));
            if (boundary.BudgetStatus != ChartBudgetStatus.Satisfied ||
                boundary.XDomain != snapshot.XDomain || boundary.YDomain != snapshot.YDomain ||
                !boundary.Points.SequenceEqual(snapshot.Points))
                throw new ArgumentException("Exact boundary projection rejected. The previous accepted snapshot is unchanged.", nameof(snapshot));
            boundaries = boundary.Segments;
        }
        var labelSizes = snapshot.Points.Select(point => FormatLabel(point, presentation)).Distinct()
            .ToDictionary(text => text, MeasureLabel);
        var maximumLabelSize = new Size(labelSizes.Count == 0 ? 0 : labelSizes.Values.Max(size => size.Width),
            labelSizes.Count == 0 ? 0 : labelSizes.Values.Max(size => size.Height));
        var selected = ReferenceEquals(Source, snapshot.Source) && presentation == _presentation ? SelectedHit : null;
        var originals = ImmutableArray.CreateBuilder<AreaChartHit>();
        foreach (var series in snapshot.Source.Series)
            foreach (var sample in series.Samples)
                if (!sample.IsGap) originals.Add(new(snapshot.DatasetVersion, series.Id, series.SourceVersion, sample));
        var acceptedOriginals = originals.ToImmutable();
        var selection = selected.HasValue ? acceptedOriginals.IndexOf(selected.Value) : -1;
        Snapshot = snapshot; _options = snapshot.Options; _presentation = presentation;
        _originals = acceptedOriginals; _boundarySegments = boundaries; _selection = selection;
        _labelSizes = labelSizes; _maximumLabelSize = maximumLabelSize;
        InvalidateFrame();
    }
    public void ReleaseSource()
    {
        Dispatcher.UIThread.VerifyAccess(); Snapshot = null; _originals = []; _boundarySegments = []; _selection = -1; _labelSizes = []; _maximumLabelSize = default; InvalidateFrame();
    }
    public bool SelectAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanAcceptInput(expectedRevision) || !ViewportArea.Contains(point) || Snapshot is not { } snapshot) return false;
        var hit = HitAt(snapshot, point, Math.Max(7, ShowMarkers ? MarkerSize / 2 : 0));
        _selection = hit.HasValue ? _originals.IndexOf(hit.Value) : -1;
        Changed(); return hit.HasValue;
    }
    public bool TrySelect(AreaChartSource expectedSource, string? seriesId, long key, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanAcceptInput(expectedRevision) || !ReferenceEquals(Source, expectedSource)) return false;
        var index = -1;
        if (seriesId is not null)
        {
            for (var i = 0; i < _originals.Length; i++)
                if (StringComparer.Ordinal.Equals(_originals[i].SeriesId, seriesId) && _originals[i].Key == key) { index = i; break; }
            if (index < 0) return false;
        }
        if (_selection == index) return true;
        _selection = index; Changed(); return true;
    }
    /// <summary>Updates both the optional grouped crosshair and original-marker hover. Custom
    /// hover remains operable when labels are shown and the crosshair is intentionally off.</summary>
    public bool HoverAt(Point point, long expectedRevision)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanAcceptInput(expectedRevision) || Snapshot is not { } snapshot) return false;
        if (!ViewportArea.Contains(point)) { ClearHover(); return false; }
        var marker = ShowMarkers ? HitAt(snapshot, point, MarkerSize / 2) : null;
        double? argument = null;
        if (CrosshairEnabled)
        {
            var distance = double.PositiveInfinity;
            foreach (var original in snapshot.Points)
            {
                var candidate = Math.Abs(At(original.X, original.Y).X - point.X);
                if (candidate >= distance) continue;
                distance = candidate; argument = original.Hit.X;
            }
        }
        if (_hoverArgument != argument || _hoveredMarker != marker)
        { _hoverArgument = argument; _hoveredMarker = marker; Changed(); }
        return argument.HasValue || marker.HasValue;
    }
    private AreaChartHit? HitAt(AreaChartSnapshot snapshot, Point point, double tolerance)
    {
        // The auto side margin can contain part of a real marker. Core's unit-square hit
        // guard would reject that half; compare the same original-only distances in pixels.
        AreaChartHit? hit = null; var best = tolerance * tolerance;
        foreach (var original in snapshot.Points)
        {
            var center = At(original.X, original.Y);
            var dx = center.X - point.X; var dy = center.Y - point.Y;
            var distance = dx * dx + dy * dy;
            if (distance > best) continue;
            best = distance; hit = original.Hit; // Last drawn wins an equal-distance tie.
        }
        return hit;
    }
    public double MarkerScale(AreaChartHit hit) => ShowMarkers && MarkerKind == SplineAreaMarkerKind.Custom && _hoveredMarker == hit ? 1.5 : 1;
    public void ClearSelection()
    { Dispatcher.UIThread.VerifyAccess(); if (_selection < 0) return; _selection = -1; Changed(); }
    private bool CanAcceptInput(long revision) => _attached && IsEffectivelyEnabled && revision == FrameRevision && IsPlotAvailable;
    private void ClearHover()
    { if (_hoverArgument is null && _hoveredMarker is null) return; _hoverArgument = null; _hoveredMarker = null; Changed(); }
    private void InvalidateFrame() { _plotArea = null; FrameRevision++; _hoverArgument = null; _hoveredMarker = null; Changed(); }
    private void UpdateAccessibleName() => AutomationProperties.SetName(this,
        $"{ChartTitle} {ModeDescription} chart. {ValueUnit}. Left and right, Home and End select original observations. Escape clears. Hide labels for grouped {ArgumentName.ToLowerInvariant()} crosshair.");
    private void Changed()
    { UpdateAccessibleName(); AutomationProperties.SetHelpText(this, AccessibleSummary); InvalidateVisual(); StateChanged?.Invoke(this, EventArgs.Empty); }
    private void OnFocusChanged(object? sender, RoutedEventArgs args) => InvalidateVisual();
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == BoundsProperty) InvalidateFrame(); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); _attached = true; InvalidateFrame(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _attached = false; InvalidateFrame(); base.OnDetachedFromVisualTree(e); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsPlotAvailable || !ViewportArea.Contains(e.GetPosition(this))) return;
        Focus(); SelectAt(e.GetPosition(this), FrameRevision); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    { base.OnPointerMoved(e); HoverAt(e.GetPosition(this), FrameRevision); }
    protected override void OnPointerExited(PointerEventArgs e)
    { base.OnPointerExited(e); ClearHover(); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    { base.OnPointerWheelChanged(e); if (IsPlotAvailable && ViewportArea.Contains(e.GetPosition(this))) e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (!CanAcceptInput(FrameRevision)) return;
        switch (e.Key)
        {
            case Key.Left: case Key.Right:
                if (!_originals.IsEmpty) _selection = _selection < 0 ? (e.Key == Key.Right ? 0 : _originals.Length - 1)
                    : Math.Clamp(_selection + (e.Key == Key.Right ? 1 : -1), 0, _originals.Length - 1);
                break;
            case Key.Home: _selection = _originals.IsEmpty ? -1 : 0; break;
            case Key.End: _selection = _originals.Length - 1; break;
            case Key.Escape: _selection = -1; _hoverArgument = null; _hoveredMarker = null; break;
            default: return;
        }
        Changed(); e.Handled = true;
    }

    private Point At(double x, double y) => new(PlotArea.X + x * PlotArea.Width, PlotArea.Y + y * PlotArea.Height);
    private IBrush SeriesBrush(int i) => i switch
    { 0 => Palette.Accent, 1 => Palette.Error, 2 => Brushes.CornflowerBlue, 3 => Brushes.Orchid, 4 => Brushes.Goldenrod, 5 => Brushes.LimeGreen, 6 => Brushes.DeepPink, _ => Brushes.SlateBlue };
    private string FormatArgument(double argument, bool axis = false) => Presentation != SplineAreaPresentation.Numeric && argument is >= -657434 and < 2958466
        ? DateTime.FromOADate(argument).ToString(Presentation == SplineAreaPresentation.FuelPrices
            ? axis ? "MMM yyyy" : "MMMM yyyy" : "yyyy", CultureInfo.InvariantCulture)
        : argument.ToString("G", CultureInfo.InvariantCulture);
    private string FormatValue(double? value) => Presentation == SplineAreaPresentation.Numeric
        ? value?.ToString("G4", CultureInfo.InvariantCulture) ?? "missing" : value is { } raw
        ? "$" + raw.ToString(Presentation == SplineAreaPresentation.FuelPrices ? "0.000" : "0.0", CultureInfo.InvariantCulture)
            + (Presentation == SplineAreaPresentation.FuelPrices ? " per Gallon" : "K") : "missing";
    private string DescribeOriginal(AreaChartHit hit)
    {
        var series = Source!.Series.First(item => StringComparer.Ordinal.Equals(item.Id, hit.SeriesId));
        return $"{series.Label} · {ArgumentName.ToLowerInvariant()} {(hit.X is { } x ? FormatArgument(x) : "missing")} · raw {FormatValue(hit.Y)} · key {hit.Key}";
    }
    private static string FormatLabel(AreaChartPoint point, SplineAreaPresentation presentation) => point.Hit.Y?.ToString(
        presentation == SplineAreaPresentation.FuelPrices ? "0.000" : "0.0", CultureInfo.InvariantCulture) ?? "missing";
    public string LabelText(AreaChartPoint point) => FormatLabel(point, Presentation);
    private static Size MeasureLabel(string text)
    {
        var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, LabelFontSize, Brushes.Black)
        { MaxTextWidth = LabelMaxWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        return new(label.Width, label.Height);
    }
    private Point LabelOffset(Size size)
    {
        var radians = LabelAngle * Math.PI / 180; var direction = Math.Cos(radians);
        var indent = MarkerSize / 2 + 5;
        return new(direction * indent - (direction < 0 ? size.Width : 0), -Math.Sin(radians) * indent - size.Height / 2);
    }
    /// <summary>Actual unrotated label layout, including boundary adjustment for manual axes.
    /// Automatic axes reserve these extents without relocating or inventing observations.</summary>
    public Rect LabelBounds(AreaChartPoint point)
    {
        var text = LabelText(point);
        var size = _labelSizes.TryGetValue(text, out var measured) ? measured : MeasureLabel(text);
        var marker = At(point.X, point.Y); var offset = LabelOffset(size); var area = ViewportArea;
        return new(new Point(Math.Clamp(marker.X + offset.X, area.Left + 2, Math.Max(area.Left + 2, area.Right - size.Width - 2)),
            Math.Clamp(marker.Y + offset.Y, area.Top + 2, Math.Max(area.Top + 2, area.Bottom - size.Height - 2))), size);
    }
    public Point LabelOrigin(AreaChartPoint point) => LabelBounds(point).Position;
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Palette.Surface, new Rect(Bounds.Size));
        if (IsFocused) context.DrawRectangle(null, new Pen(Palette.Accent, 2), new Rect(Bounds.Size).Deflate(1));
        DrawingText.Draw(context, ChartTitle, new(10, 6), Palette.Text, 16, Math.Max(1, Bounds.Width - 20));
        DrawingText.Draw(context, $"{ValueUnit} · independent {ModeDescription}s, not stacked", new(10, 30), Palette.Muted, 11, Math.Max(1, Bounds.Width - 20));
        if (Snapshot is { } snapshot && IsPlotAvailable && snapshot.XDomain.HasValue && snapshot.YDomain.HasValue)
        {
            var area = ViewportArea; var slot = Math.Max(1, (Bounds.Width - 20) / Math.Max(1, snapshot.Source.Series.Length));
            for (var i = 0; i < snapshot.Source.Series.Length; i++)
            {
                var x = 10 + i * slot;
                context.FillRectangle(SeriesBrush(i), new Rect(x, 53, 8, 8));
                DrawingText.Draw(context, snapshot.Source.Series[i].Label, new(x + 12, 49), Palette.Text, 10, Math.Max(1, slot - 16));
            }
            foreach (var tick in snapshot.YTicks)
            {
                var y = At(0, 1 - tick.Position).Y;
                context.DrawLine(new Pen(Palette.GridLine, 1), new(area.Left, y), new(area.Right, y));
                DrawingText.Draw(context, tick.Value.ToString(Presentation == SplineAreaPresentation.FuelPrices ? "0.000" : "G4", CultureInfo.InvariantCulture), new(5, y - 7), Palette.Muted, 10, 56);
            }
            using (context.PushClip(area))
            {
                // A single same-series coverage pass avoids transparent antialias seams between
                // adjacent tessellation fragments. Closed figures preserve gaps and clipped lobes.
                foreach (var series in snapshot.Polygons.GroupBy(polygon => polygon.SeriesIndex))
                {
                    var geometry = new StreamGeometry();
                    using (var path = geometry.Open())
                    {
                        path.SetFillRule(FillRule.NonZero);
                        foreach (var polygon in series)
                        {
                            if (polygon.Vertices.IsEmpty) continue;
                            path.BeginFigure(At(polygon.Vertices[0].X, polygon.Vertices[0].Y), true);
                            for (var i = 1; i < polygon.Vertices.Length; i++) path.LineTo(At(polygon.Vertices[i].X, polygon.Vertices[i].Y));
                            path.EndFigure(true);
                        }
                    }
                    using (context.PushOpacity(1 - Transparency)) context.DrawGeometry(SeriesBrush(series.Key), null, geometry);
                }
                foreach (var segment in _boundarySegments)
                    context.DrawLine(new Pen(SeriesBrush(segment.SeriesIndex), 1.5), At(segment.Start.X, segment.Start.Y), At(segment.End.X, segment.End.Y));
                if (CrosshairX is { } crosshair)
                    context.DrawLine(new Pen(Palette.Text, 1), new(crosshair, area.Top), new(crosshair, area.Bottom));
                if (ShowMarkers) foreach (var point in snapshot.Points) DrawMarker(context, point);
                if (CrosshairEnabled) foreach (var point in HoverPoints)
                    context.DrawEllipse(null, new Pen(Palette.Text, 1), At(point.X, point.Y), 4, 4);
                if (MarkerPoint is { } selected) context.DrawEllipse(null, new Pen(Palette.Text, 2), selected, (ShowMarkers ? MarkerSize / 2 : 4) + 3, (ShowMarkers ? MarkerSize / 2 : 4) + 3);
            }
            if (ShowLabels)
                foreach (var point in snapshot.Points)
                {
                    DrawingText.Draw(context, LabelText(point), LabelOrigin(point), Palette.Text, LabelFontSize, LabelMaxWidth);
                }
            var arguments = snapshot.Source.Series.SelectMany(series => series.Samples).Where(sample => !sample.IsGap)
                .Select(sample => sample.X!.Value).Distinct().Order().Where(x => x >= snapshot.XDomain.Value.Minimum && x <= snapshot.XDomain.Value.Maximum).ToArray();
            var labelWidth = Presentation == SplineAreaPresentation.FuelPrices ? 68 : 55;
            var stride = Math.Max(1, (int)Math.Ceiling(arguments.Length / Math.Max(2, area.Width / (labelWidth + 5))));
            for (var i = 0; i < arguments.Length; i += stride)
            {
                var x = At(DualAxisChartGeometry.NormalizeY(arguments[i], snapshot.XDomain.Value), 0).X;
                DrawingText.Draw(context, FormatArgument(arguments[i], axis: true),
                    new(Math.Clamp(x - (Presentation == SplineAreaPresentation.FuelPrices ? labelWidth / 2d : 15),
                        area.Left, Math.Max(area.Left, Bounds.Width - labelWidth - 3)), area.Bottom + 7), Palette.Muted, 10, labelWidth);
            }
            DrawCrosshairLabel(context);
        }
        DrawingText.Draw(context, IsPlotAvailable ? Status : $"Enlarge the plot to inspect original {ObservationName}", new(10, Math.Max(0, Bounds.Height - 20)), Palette.Muted, 10, Math.Max(1, Bounds.Width - 20));
    }
    private void DrawCrosshairLabel(DrawingContext context)
    {
        if (!CrosshairEnabled || _hoverArgument is not { } argument || Source is not { } source) return;
        var rows = source.Series.SelectMany(series => series.Samples.Where(sample => !sample.IsGap && sample.X == argument)
            .Select(sample => $"{series.Label}: {FormatValue(sample.Y)}")).ToArray();
        var width = Math.Min(225, Math.Max(1, PlotArea.Width - 12)); var x = PlotArea.Right - width - 6; var y = PlotArea.Top + 6;
        context.DrawRectangle(Palette.Surface, new Pen(Palette.GridLine, 1), new Rect(x, y, width, 24 + rows.Length * 17));
        DrawingText.Draw(context, ArgumentName + ": " + FormatArgument(argument), new(x + 6, y + 4), Palette.Text, 11, Math.Max(1, width - 12));
        for (var i = 0; i < rows.Length; i++) DrawingText.Draw(context, rows[i], new(x + 6, y + 22 + i * 17), Palette.Text, 10, Math.Max(1, width - 12));
    }
    private void DrawMarker(DrawingContext context, AreaChartPoint point)
    {
        var center = At(point.X, point.Y); var size = MarkerSize * MarkerScale(point.Hit); var r = size / 2; var brush = SeriesBrush(point.SeriesIndex);
        switch (MarkerKind)
        {
            case SplineAreaMarkerKind.BorderlessSimple: context.DrawEllipse(brush, null, center, r, r); break;
            case SplineAreaMarkerKind.Simple: context.DrawEllipse(brush, new Pen(Palette.Surface, 1.5), center, Math.Max(0, r - .75), Math.Max(0, r - .75)); break;
            case SplineAreaMarkerKind.Circle: context.DrawEllipse(null, new Pen(brush, 1.5), center, Math.Max(0, r - .75), Math.Max(0, r - .75)); break;
            case SplineAreaMarkerKind.Ring: context.DrawEllipse(null, new Pen(brush, Math.Max(2, size * .2)), center, r * .8, r * .8); break;
            case SplineAreaMarkerKind.Square: context.FillRectangle(brush, new Rect(center.X - r, center.Y - r, size, size)); break;
            case SplineAreaMarkerKind.Dollar:
                var text = new FormattedText("$", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
                context.DrawText(text, new(center.X - text.Width / 2, center.Y - text.Height / 2)); break;
            case SplineAreaMarkerKind.Custom:
                var gray = brush is ISolidColorBrush solid ? new SolidColorBrush(Color.FromArgb(solid.Color.A,
                    (byte)((solid.Color.R + 128) / 2), (byte)((solid.Color.G + 128) / 2), (byte)((solid.Color.B + 128) / 2))) : brush;
                var scale = MarkerScale(point.Hit);
                context.DrawEllipse(null, new Pen(gray, 2 * scale), center, Math.Max(0, r - scale), Math.Max(0, r - scale));
                if (r > 4 * scale) context.DrawEllipse(null, new Pen(gray, 2 * scale), center, r - 4 * scale, r - 4 * scale);
                if (r > 8 * scale) context.DrawEllipse(gray, null, center, r - 8 * scale, r - 8 * scale);
                break;
            default:
                var vertices = MarkerVertices(MarkerKind);
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    path.BeginFigure(new(center.X + vertices[0].X * r, center.Y + vertices[0].Y * r), true);
                    for (var i = 1; i < vertices.Length; i++) path.LineTo(new(center.X + vertices[i].X * r, center.Y + vertices[i].Y * r));
                    path.EndFigure(true);
                }
                context.DrawGeometry(brush, null, geometry); break;
        }
    }
    private static Point[] MarkerVertices(SplineAreaMarkerKind kind)
    {
        if (kind == SplineAreaMarkerKind.Diamond) return [new(0, -1), new(1, 0), new(0, 1), new(-1, 0)];
        if (kind is SplineAreaMarkerKind.Plus or SplineAreaMarkerKind.Cross)
        {
            Point[] plus = [new(-.3, -1), new(.3, -1), new(.3, -.3), new(1, -.3), new(1, .3), new(.3, .3), new(.3, 1), new(-.3, 1), new(-.3, .3), new(-1, .3), new(-1, -.3), new(-.3, -.3)];
            return kind == SplineAreaMarkerKind.Plus ? plus : plus.Select(p => new Point((p.X - p.Y) / 1.3, (p.X + p.Y) / 1.3)).ToArray();
        }
        var count = kind switch { SplineAreaMarkerKind.Triangle or SplineAreaMarkerKind.InvertedTriangle => 3, SplineAreaMarkerKind.Pentagon => 5, SplineAreaMarkerKind.Star => 10, _ => 6 };
        var offset = kind == SplineAreaMarkerKind.InvertedTriangle ? Math.PI / 2 : -Math.PI / 2;
        return Enumerable.Range(0, count).Select(i =>
        {
            var radius = kind == SplineAreaMarkerKind.Star && i % 2 == 1 ? .42 : 1;
            var angle = offset + i * Math.PI * 2 / count;
            return new Point(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
        }).ToArray();
    }
}
