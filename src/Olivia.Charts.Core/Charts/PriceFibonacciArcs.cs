using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>
/// An authored Fibonacci Arc. First is the center; its distance to Second is the 100% radius.
/// The optional flags default to false as an explicit Olivia choice, not vendor-default parity.
/// Original endpoint order, identity and OHLC bits are retained without sorting.
/// </summary>
public sealed record PriceFibonacciArc(long Id, PriceTrendAnchor First, PriceTrendAnchor Second)
{
    public bool ShowLevel23_6 { get; init; }
    public bool ShowLevel76_4 { get; init; }
    public bool ShowLevel100 { get; init; }
}

/// <summary>Only the exact store-issued draft can commit or cancel. Edits preserve prior flags.</summary>
public sealed record PriceFibonacciArcDraft(long Revision, long? ArcId,
    PriceFibonacciArc? Original)
{
    public bool ShowLevel23_6 { get; init; }
    public bool ShowLevel76_4 { get; init; }
    public bool ShowLevel100 { get; init; }
}

/// <summary>
/// Separate bounded immutable arc snapshots. All operations serialize; separate property
/// reads are not a whole-store transaction. IDs never repeat and revisions advance on changes.
/// </summary>
public sealed class PriceFibonacciArcs
{
    public const int MaximumArcs = 32;
    private readonly object _gate = new();
    private ImmutableArray<PriceFibonacciArc> _items = ImmutableArray<PriceFibonacciArc>.Empty;
    private long _revision;
    private long _lastId;
    private long? _selectedId;
    private PriceFibonacciArcDraft? _draft;

    public ImmutableArray<PriceFibonacciArc> Items { get { lock (_gate) return _items; } }
    public long Revision { get { lock (_gate) return _revision; } }
    public long? SelectedId { get { lock (_gate) return _selectedId; } }
    public PriceFibonacciArcDraft? Draft { get { lock (_gate) return _draft; } }

    /// <summary>Starts an empty draft. Full capacity changes nothing, including an existing edit.</summary>
    public PriceFibonacciArcDraft? BeginNew(bool showLevel23_6 = false, bool showLevel76_4 = false, bool showLevel100 = false)
    {
        lock (_gate)
        {
            if (_items.Length >= MaximumArcs) return null;
            var revision = checked(_revision + 1);
            var draft = new PriceFibonacciArcDraft(revision, null, null) { ShowLevel23_6 = showLevel23_6, ShowLevel76_4 = showLevel76_4, ShowLevel100 = showLevel100 };
            _revision = revision;
            _selectedId = null;
            _draft = draft;
            return draft;
        }
    }

    /// <summary>Starts an edit and selects the original arc. A missing ID changes nothing.</summary>
    public PriceFibonacciArcDraft? BeginEdit(long id)
    {
        lock (_gate)
        {
            var index = FindIndex(id);
            if (index < 0) return null;
            var original = _items[index];
            var revision = checked(_revision + 1);
            var draft = new PriceFibonacciArcDraft(revision, id, original) { ShowLevel23_6 = original.ShowLevel23_6, ShowLevel76_4 = original.ShowLevel76_4, ShowLevel100 = original.ShowLevel100 };
            _revision = revision;
            _selectedId = id;
            _draft = draft;
            return draft;
        }
    }

    /// <summary>
    /// Commits both endpoints atomically. Stale, copied and foreign tokens return false before any
    /// payload validation. Invalid current anchors throw ArgumentException and retain all state.
    /// Anchors must have distinct keys and UTC instants in the same original series and dataset.
    /// </summary>
    public bool Commit(PriceFibonacciArcDraft draft, PriceTrendAnchor first, PriceTrendAnchor second)
    {
        lock (_gate)
        {
            if (!IsCurrent(draft)) return false;
            PriceTrendGeometry.ValidatePair(first, second);
            var revision = checked(_revision + 1);
            ImmutableArray<PriceFibonacciArc> items;
            long id;
            if (draft.ArcId is { } existingId)
            {
                var index = FindIndex(existingId);
                if (index < 0) return false;
                id = existingId;
                items = _items.SetItem(index, new(id, first, second) { ShowLevel23_6 = draft.ShowLevel23_6, ShowLevel76_4 = draft.ShowLevel76_4, ShowLevel100 = draft.ShowLevel100 });
            }
            else
            {
                if (_items.Length >= MaximumArcs) return false;
                id = checked(_lastId + 1);
                items = _items.Add(new(id, first, second) { ShowLevel23_6 = draft.ShowLevel23_6, ShowLevel76_4 = draft.ShowLevel76_4, ShowLevel100 = draft.ShowLevel100 });
            }
            _items = items;
            _lastId = Math.Max(_lastId, id);
            _selectedId = id;
            _draft = null;
            _revision = revision;
            return true;
        }
    }

    /// <summary>
    /// Atomically replaces all optional-level flags on the exact retained object, never an active
    /// draft. Stale, copied and foreign records refuse the change. Matching options are a no-op.
    /// Anchors, IDs and selection are retained; previous immutable snapshots remain unchanged.
    /// </summary>
    public bool SetOptions(PriceFibonacciArc expected, bool showLevel23_6,
        bool showLevel76_4, bool showLevel100)
    {
        lock (_gate)
        {
            if (expected is null || _draft is not null) return false;
            var index = FindIndex(expected.Id);
            if (index < 0 || !ReferenceEquals(_items[index], expected)) return false;
            if (expected.ShowLevel23_6 == showLevel23_6 && expected.ShowLevel76_4 == showLevel76_4 &&
                expected.ShowLevel100 == showLevel100) return true;
            var revision = checked(_revision + 1);
            var items = _items.SetItem(index, expected with
            {
                ShowLevel23_6 = showLevel23_6,
                ShowLevel76_4 = showLevel76_4,
                ShowLevel100 = showLevel100
            });
            _items = items;
            _revision = revision;
            return true;
        }
    }

    /// <summary>Cancels only the current exact token, retaining selection and committed arcs.</summary>
    public bool Cancel(PriceFibonacciArcDraft draft)
    {
        lock (_gate)
        {
            if (!IsCurrent(draft)) return false;
            var revision = checked(_revision + 1);
            _draft = null;
            _revision = revision;
            return true;
        }
    }

    /// <summary>Selects an existing arc and cancels any draft. A missing ID changes nothing.</summary>
    public bool Select(long id)
    {
        lock (_gate)
        {
            if (FindIndex(id) < 0) return false;
            if (_selectedId == id && _draft is null) return true;
            var revision = checked(_revision + 1);
            _selectedId = id;
            _draft = null;
            _revision = revision;
            return true;
        }
    }

    /// <summary>Clears selection and any draft, but retains committed arcs and IDs.</summary>
    public bool ClearSelection()
    {
        lock (_gate)
        {
            if (_selectedId is null && _draft is null) return false;
            var revision = checked(_revision + 1);
            _selectedId = null;
            _draft = null;
            _revision = revision;
            return true;
        }
    }

    /// <summary>Cancels any draft and removes the selected arc, if any.</summary>
    public bool RemoveSelected()
    {
        lock (_gate)
        {
            var index = _selectedId is { } id ? FindIndex(id) : -1;
            if (index < 0 && _draft is null) return false;
            var revision = checked(_revision + 1);
            var items = index >= 0 ? _items.RemoveAt(index) : _items;
            _items = items;
            _selectedId = null;
            _draft = null;
            _revision = revision;
            return index >= 0;
        }
    }

    /// <summary>Clears arcs, selection and drafts without reusing IDs during this store's lifetime.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_items.IsEmpty && _selectedId is null && _draft is null) return;
            var revision = checked(_revision + 1);
            _items = ImmutableArray<PriceFibonacciArc>.Empty;
            _selectedId = null;
            _draft = null;
            _revision = revision;
        }
    }

    private bool IsCurrent(PriceFibonacciArcDraft? draft) =>
        draft is not null && ReferenceEquals(draft, _draft) && draft.Revision == _revision;

    private int FindIndex(long id)
    {
        for (var index = 0; index < _items.Length; index++)
            if (_items[index].Id == id) return index;
        return -1;
    }
}

/// <summary>Enabled circles are classified independently; numeric/budget refusal is never a fake curve.</summary>
public enum PriceFibonacciArcLevelStatus { Visible, OutsideViewport, OutOfRange, GeometryBudgetExceeded }

/// <summary>Additional explanation when the shared projection status cannot describe a layout refusal.</summary>
public enum PriceFibonacciArcGeometryIssue
{
    None, InvalidPlotSize, UnrepresentableAnchor, DegenerateRadius, UnrepresentableRadius, SegmentBudgetExceeded
}

/// <summary>
/// Ratio is a fraction; RadiusDips is the circle radius in plot DIPs, never a price. Segments
/// are independently clipped normalized [0,1] screen chords with Y down, not source bars or
/// exact-on-circle hit points. Empty segments never authorize joining disconnected visible pieces.
/// </summary>
public sealed record PriceFibonacciArcLevelProjection(double Ratio, double? RadiusDips,
    PriceFibonacciArcLevelStatus Status, ImmutableArray<HistoryChartLine> Segments);

/// <summary>
/// Arc retains authored original OHLC anchors; First and Second retain currently resolved original
/// provenance and raw/aggregate ordinal slots. No projection outcome mutates or replaces authorship.
/// GeometryBudgetExceeded also covers unusable layout/numeric mapping; GeometryIssue explains it.
/// Valid mapped projections retain every enabled level, including individually refused levels.
/// </summary>
public sealed record PriceFibonacciArcProjection(PriceFibonacciArc Arc,
    PriceTrendProjectionStatus Status, PriceTrendResolvedAnchor? First, PriceTrendResolvedAnchor? Second,
    ImmutableArray<PriceFibonacciArcLevelProjection> Levels)
{
    public PriceFibonacciArcGeometryIssue GeometryIssue { get; init; }
}

public static class PriceFibonacciArcGeometry
{
    public const double MaximumChordErrorDips = .25;
    public const int MaximumSegmentsPerLevel = 2048;

    /// <summary>
    /// First is the center and its Euclidean distance to Second after unclamped viewport mapping
    /// is the 100% radius. Fixed ratios are .382, .5, .618; only .236, .764, 1 are optional.
    /// Complete 360-degree circles, the plot-DIP metric, false optional defaults and Close/Close
    /// authoring are explicit Olivia compatibility choices, not verified native WPF defaults.
    /// Same aggregate slots suppress the entire arc rather than inventing another source anchor.
    /// Constant Y domains map matching values to the midpoint; other values cannot be represented.
    ///
    /// A circle is approximated by at most 2048 equal-angle chords, then each chord is clipped in
    /// both plot axes. Chord sagitta is at most .125 DIP, reserving the other .125 of the declared
    /// .25 DIP bound for binary64 coordinate rounding. Numeric or budget refusal never silently
    /// coarsens sampling or clamps an authored endpoint. Single-point intersections are omitted.
    /// </summary>
    public static PriceFibonacciArcProjection Project(PriceFibonacciArc arc,
        PriceChartSnapshot current, double plotWidth, double plotHeight,
        PriceTimeAggregationSnapshot? aggregation = null)
    {
        ArgumentNullException.ThrowIfNull(arc);
        ArgumentNullException.ThrowIfNull(current);
        var resolved = PriceTrendGeometry.ResolveOriginalAnchors(arc.First, arc.Second, current, aggregation);
        PriceFibonacciArcProjection Suppressed(PriceTrendProjectionStatus status,
            PriceFibonacciArcGeometryIssue issue = PriceFibonacciArcGeometryIssue.None) =>
            new(arc, status, resolved.First, resolved.Second, ImmutableArray<PriceFibonacciArcLevelProjection>.Empty)
                { GeometryIssue = issue };
        if (resolved.Status != PriceTrendProjectionStatus.Visible) return Suppressed(resolved.Status);
        if (current.GetBarCoordinate(resolved.First!.Value.DisplayIndex) == current.GetBarCoordinate(resolved.Second!.Value.DisplayIndex))
            return Suppressed(PriceTrendProjectionStatus.CollapsedAggregateSlot);
        if (current.BarCount == 0) return Suppressed(PriceTrendProjectionStatus.EmptyViewport);
        if (current.BudgetStatus != ChartBudgetStatus.Satisfied)
            return Suppressed(PriceTrendProjectionStatus.GeometryBudgetExceeded);
        if (current.Domain is not { } domain) return Suppressed(PriceTrendProjectionStatus.NoDomain);
        if (!double.IsFinite(plotWidth) || !double.IsFinite(plotHeight) || plotWidth <= 0 || plotHeight <= 0)
            return Suppressed(PriceTrendProjectionStatus.GeometryBudgetExceeded, PriceFibonacciArcGeometryIssue.InvalidPlotSize);

        var first = AreaChartGeometry.MapPriceFibonacciArcAnchor(current.GetBarCoordinate(resolved.First!.Value.DisplayIndex),
            arc.First.Value, current.XDomain, domain, plotWidth, plotHeight);
        var second = AreaChartGeometry.MapPriceFibonacciArcAnchor(current.GetBarCoordinate(resolved.Second!.Value.DisplayIndex),
            arc.Second.Value, current.XDomain, domain, plotWidth, plotHeight);
        if (first is not { } center || second is not { } reference)
            return Suppressed(PriceTrendProjectionStatus.GeometryBudgetExceeded, PriceFibonacciArcGeometryIssue.UnrepresentableAnchor);
        var dx = reference.X - center.X;
        var dy = reference.Y - center.Y;
        var scale = Math.Max(Math.Abs(dx), Math.Abs(dy));
        var radius = scale == 0 ? 0 : scale * Math.Sqrt((dx / scale) * (dx / scale) + (dy / scale) * (dy / scale));
        var levels = ImmutableArray.CreateBuilder<PriceFibonacciArcLevelProjection>(6);
        var visible = false;
        var refused = false;
        var issue = PriceFibonacciArcGeometryIssue.None;
        void Add(int permille)
        {
            var levelRadius = radius * (permille / 1000d);
            PriceFibonacciArcLevelProjection level;
            if (!double.IsFinite(levelRadius) || levelRadius <= 0)
            {
                level = new(permille / 1000d, double.IsFinite(levelRadius) ? levelRadius : null,
                    PriceFibonacciArcLevelStatus.OutOfRange, ImmutableArray<HistoryChartLine>.Empty);
                if (issue == PriceFibonacciArcGeometryIssue.None)
                    issue = levelRadius == 0 ? PriceFibonacciArcGeometryIssue.DegenerateRadius :
                        PriceFibonacciArcGeometryIssue.UnrepresentableRadius;
            }
            else
            {
                level = CreateCircle(permille / 1000d, center, levelRadius, plotWidth, plotHeight);
                if (issue == PriceFibonacciArcGeometryIssue.None)
                    issue = level.Status == PriceFibonacciArcLevelStatus.GeometryBudgetExceeded ?
                        PriceFibonacciArcGeometryIssue.SegmentBudgetExceeded :
                        level.Status == PriceFibonacciArcLevelStatus.OutOfRange ?
                            PriceFibonacciArcGeometryIssue.UnrepresentableRadius : PriceFibonacciArcGeometryIssue.None;
            }
            visible |= level.Status == PriceFibonacciArcLevelStatus.Visible;
            refused |= level.Status is PriceFibonacciArcLevelStatus.OutOfRange or PriceFibonacciArcLevelStatus.GeometryBudgetExceeded;
            levels.Add(level);
        }
        if (arc.ShowLevel23_6) Add(236);
        Add(382);
        Add(500);
        Add(618);
        if (arc.ShowLevel76_4) Add(764);
        if (arc.ShowLevel100) Add(1000);
        return new(arc, visible ? PriceTrendProjectionStatus.Visible : refused ?
            PriceTrendProjectionStatus.GeometryBudgetExceeded : PriceTrendProjectionStatus.OutsideViewport,
            resolved.First, resolved.Second, levels.ToImmutable()) { GeometryIssue = issue };
    }

    private static PriceFibonacciArcLevelProjection CreateCircle(double ratio, HistoryChartPoint center,
        double radius, double width, double height)
    {
        PriceFibonacciArcLevelProjection Empty(PriceFibonacciArcLevelStatus status) =>
            new(ratio, radius, status, ImmutableArray<HistoryChartLine>.Empty);
        // Refuse values whose coordinate rounding cannot fit the reserved .125-DIP allowance.
        // This guard is deliberately conservative; finite does not imply renderable precision.
        var magnitude = Math.Max(Math.Abs(center.X), Math.Abs(center.Y)) + radius;
        if (!double.IsFinite(magnitude))
            return Empty(PriceFibonacciArcLevelStatus.OutOfRange);
        var ulp = Math.BitIncrement(magnitude) - magnitude;
        if (!double.IsFinite(ulp) || ulp * 32 > MaximumChordErrorDips / 2)
            return Empty(PriceFibonacciArcLevelStatus.OutOfRange);
        // 2*r*sin(pi/(2*n))^2 is the stable chord sagitta, including very small angles.
        var halfStep = Math.Asin(Math.Sqrt(Math.Min(1, (MaximumChordErrorDips / 2) / radius / 2)));
        var required = Math.Ceiling(Math.PI / (2 * halfStep));
        if (!double.IsFinite(required) || required > MaximumSegmentsPerLevel)
            return Empty(PriceFibonacciArcLevelStatus.GeometryBudgetExceeded);
        var count = Math.Max(8, (int)required);
        // Rounding up to a multiple of four gives exact cardinal samples and a closed final seam.
        count = (count + 3) / 4 * 4;
        if (count > MaximumSegmentsPerLevel) return Empty(PriceFibonacciArcLevelStatus.GeometryBudgetExceeded);
        var sagitta = 2 * radius * Math.Pow(Math.Sin(Math.PI / (2 * count)), 2);
        if (!double.IsFinite(sagitta) || sagitta + ulp * 32 > MaximumChordErrorDips)
            return Empty(PriceFibonacciArcLevelStatus.GeometryBudgetExceeded);
        var segments = ImmutableArray.CreateBuilder<HistoryChartLine>(count);
        HistoryChartPoint At(int index)
        {
            if (index == 0 || index == count) return new(center.X + radius, center.Y);
            if (index == count / 4) return new(center.X, center.Y + radius);
            if (index == count / 2) return new(center.X - radius, center.Y);
            if (index == 3 * count / 4) return new(center.X, center.Y - radius);
            var angle = 2 * Math.PI * index / count;
            return new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
        }
        var start = At(0);
        for (var i = 1; i <= count; i++)
        {
            var end = At(i);
            if (!double.IsFinite(start.X) || !double.IsFinite(start.Y) ||
                !double.IsFinite(end.X) || !double.IsFinite(end.Y))
                return Empty(PriceFibonacciArcLevelStatus.OutOfRange);
            var clipped = AreaChartGeometry.ClipPriceFibonacciArcChord(start, end, width, height);
            if (clipped is { } segment) segments.Add(segment);
            start = end;
        }
        return new(ratio, radius, segments.Count == 0 ? PriceFibonacciArcLevelStatus.OutsideViewport :
            PriceFibonacciArcLevelStatus.Visible, segments.ToImmutable());
    }
}

public static partial class AreaChartGeometry
{
    // Exact rational binary-unit mapping avoids overflow from opposite-sign prices and preserves
    // tiny finite domains. Unlike general chart normalization, authored offscreen anchors are not
    // clamped. Only the final DIP coordinate is rounded once; overflow/underflow refuses mapping.
    internal static HistoryChartPoint? MapPriceFibonacciArcAnchor(double coordinate, double value,
        HistoryValueDomain xDomain, HistoryValueDomain domain, double width, double height)
    {
        double? Convert(System.Numerics.BigInteger numerator, System.Numerics.BigInteger denominator)
        {
            if (System.Numerics.BigInteger.Abs(numerator) > FibonacciBinaryUnits(double.MaxValue) * denominator)
                return null;
            var result = FibonacciPriceToDouble(numerator, denominator);
            return double.IsFinite(result) && (result != 0 || numerator.IsZero) ? result : null;
        }
        var x = Convert((FibonacciBinaryUnits(coordinate) - FibonacciBinaryUnits(xDomain.Minimum)) * FibonacciBinaryUnits(width),
            FibonacciBinaryUnits(xDomain.Maximum) - FibonacciBinaryUnits(xDomain.Minimum));
        double? y;
        if (domain.Minimum == domain.Maximum)
            y = value == domain.Minimum ? Convert(FibonacciBinaryUnits(height), 2) : null;
        else
        {
            var maximum = FibonacciBinaryUnits(domain.Maximum);
            y = Convert((maximum - FibonacciBinaryUnits(value)) * FibonacciBinaryUnits(height),
                maximum - FibonacciBinaryUnits(domain.Minimum));
        }
        return x is { } px && y is { } py ? new(px, py) : null;
    }

    internal static HistoryChartLine? ClipPriceFibonacciArcChord(HistoryChartPoint start,
        HistoryChartPoint end, double width, double height)
    {
        var a = new ExactPoint(Exact.FromDouble(start.X), Exact.FromDouble(start.Y));
        var b = new ExactPoint(Exact.FromDouble(end.X), Exact.FromDouble(end.Y));
        var maxX = Exact.FromDouble(width);
        var maxY = Exact.FromDouble(height);
        if (!ClipSegmentPlane(ref a, ref b, Exact.Zero, true, true) ||
            !ClipSegmentPlane(ref a, ref b, maxX, true, false) ||
            !ClipSegmentPlane(ref a, ref b, Exact.Zero, false, true) ||
            !ClipSegmentPlane(ref a, ref b, maxY, false, false) || a == b) return null;
        var first = new HistoryChartPoint((a.X / maxX).ToUnitDouble(), (a.Y / maxY).ToUnitDouble());
        var second = new HistoryChartPoint((b.X / maxX).ToUnitDouble(), (b.Y / maxY).ToUnitDouble());
        return first != second ? new(first, second) : null;
    }
}
