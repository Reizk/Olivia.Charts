using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>
/// An authored Fibonacci Fan. First is the fan origin; the line through Second is level 0.
/// The optional flags default to false as an explicit Olivia choice, not vendor-default parity.
/// Original endpoint order, identity and OHLC bits are retained without sorting.
/// </summary>
public sealed record PriceFibonacciFan(long Id, PriceTrendAnchor First, PriceTrendAnchor Second)
{
    public bool ShowLevel23_6 { get; init; }
    public bool ShowLevel76_4 { get; init; }
    public bool ShowLevel0 { get; init; }
}

/// <summary>Only the exact store-issued draft can commit or cancel. Edits preserve prior flags.</summary>
public sealed record PriceFibonacciFanDraft(long Revision, long? FanId,
    PriceFibonacciFan? Original)
{
    public bool ShowLevel23_6 { get; init; }
    public bool ShowLevel76_4 { get; init; }
    public bool ShowLevel0 { get; init; }
}

/// <summary>
/// Separate bounded immutable fan snapshots. All operations serialize; separate property
/// reads are not a whole-store transaction. IDs never repeat and revisions advance on changes.
/// </summary>
public sealed class PriceFibonacciFans
{
    public const int MaximumFans = 32;
    private readonly object _gate = new();
    private ImmutableArray<PriceFibonacciFan> _items = ImmutableArray<PriceFibonacciFan>.Empty;
    private long _revision;
    private long _lastId;
    private long? _selectedId;
    private PriceFibonacciFanDraft? _draft;

    public ImmutableArray<PriceFibonacciFan> Items { get { lock (_gate) return _items; } }
    public long Revision { get { lock (_gate) return _revision; } }
    public long? SelectedId { get { lock (_gate) return _selectedId; } }
    public PriceFibonacciFanDraft? Draft { get { lock (_gate) return _draft; } }

    /// <summary>Starts an empty draft. Full capacity changes nothing, including an existing edit.</summary>
    public PriceFibonacciFanDraft? BeginNew(bool showLevel0 = false, bool showLevel23_6 = false, bool showLevel76_4 = false)
    {
        lock (_gate)
        {
            if (_items.Length >= MaximumFans) return null;
            var revision = checked(_revision + 1);
            var draft = new PriceFibonacciFanDraft(revision, null, null) { ShowLevel23_6 = showLevel23_6, ShowLevel76_4 = showLevel76_4, ShowLevel0 = showLevel0 };
            _revision = revision;
            _selectedId = null;
            _draft = draft;
            return draft;
        }
    }

    /// <summary>Starts an edit and selects the original fan. A missing ID changes nothing.</summary>
    public PriceFibonacciFanDraft? BeginEdit(long id)
    {
        lock (_gate)
        {
            var index = FindIndex(id);
            if (index < 0) return null;
            var original = _items[index];
            var revision = checked(_revision + 1);
            var draft = new PriceFibonacciFanDraft(revision, id, original) { ShowLevel23_6 = original.ShowLevel23_6, ShowLevel76_4 = original.ShowLevel76_4, ShowLevel0 = original.ShowLevel0 };
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
    public bool Commit(PriceFibonacciFanDraft draft, PriceTrendAnchor first, PriceTrendAnchor second)
    {
        lock (_gate)
        {
            if (!IsCurrent(draft)) return false;
            PriceTrendGeometry.ValidatePair(first, second);
            var revision = checked(_revision + 1);
            ImmutableArray<PriceFibonacciFan> items;
            long id;
            if (draft.FanId is { } existingId)
            {
                var index = FindIndex(existingId);
                if (index < 0) return false;
                id = existingId;
                items = _items.SetItem(index, new(id, first, second) { ShowLevel23_6 = draft.ShowLevel23_6, ShowLevel76_4 = draft.ShowLevel76_4, ShowLevel0 = draft.ShowLevel0 });
            }
            else
            {
                if (_items.Length >= MaximumFans) return false;
                id = checked(_lastId + 1);
                items = _items.Add(new(id, first, second) { ShowLevel23_6 = draft.ShowLevel23_6, ShowLevel76_4 = draft.ShowLevel76_4, ShowLevel0 = draft.ShowLevel0 });
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
    public bool SetOptions(PriceFibonacciFan expected, bool showLevel0,
        bool showLevel23_6, bool showLevel76_4)
    {
        lock (_gate)
        {
            if (expected is null || _draft is not null) return false;
            var index = FindIndex(expected.Id);
            if (index < 0 || !ReferenceEquals(_items[index], expected)) return false;
            if (expected.ShowLevel23_6 == showLevel23_6 && expected.ShowLevel76_4 == showLevel76_4 &&
                expected.ShowLevel0 == showLevel0) return true;
            var revision = checked(_revision + 1);
            var items = _items.SetItem(index, expected with
            {
                ShowLevel23_6 = showLevel23_6,
                ShowLevel76_4 = showLevel76_4,
                ShowLevel0 = showLevel0
            });
            _items = items;
            _revision = revision;
            return true;
        }
    }

    /// <summary>Cancels only the current exact token, retaining selection and committed fans.</summary>
    public bool Cancel(PriceFibonacciFanDraft draft)
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

    /// <summary>Selects an existing fan and cancels any draft. A missing ID changes nothing.</summary>
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

    /// <summary>Clears selection and any draft, but retains committed fans and IDs.</summary>
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

    /// <summary>Cancels any draft and removes the selected fan, if any.</summary>
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

    /// <summary>Clears fans, selection and drafts without reusing IDs during this store's lifetime.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_items.IsEmpty && _selectedId is null && _draft is null) return;
            var revision = checked(_revision + 1);
            _items = ImmutableArray<PriceFibonacciFan>.Empty;
            _selectedId = null;
            _draft = null;
            _revision = revision;
        }
    }

    private bool IsCurrent(PriceFibonacciFanDraft? draft) =>
        draft is not null && ReferenceEquals(draft, _draft) && draft.Revision == _revision;

    private int FindIndex(long id)
    {
        for (var index = 0; index < _items.Length; index++)
            if (_items[index].Id == id) return index;
        return -1;
    }
}

/// <summary>Each enabled ray is classified independently after exact clipping.</summary>
public enum PriceFibonacciFanLevelStatus { Visible, OutsideViewport, OutOfRange }

/// <summary>
/// Ratio is a fraction, for example .382 for 38.2%. SecondPrice is the nearest-even binary64
/// price where this ray meets the second anchor's display X, not a synthetic original hit.
/// Segment is present only for a visible ray. Geometry uses the exact rational price rather
/// than the rounded metadata, and may remain visible beyond binary64 price range.
/// </summary>
public sealed record PriceFibonacciFanLevelProjection(double Ratio, double? SecondPrice,
    PriceFibonacciFanLevelStatus Status, HistoryChartLine? Segment);

/// <summary>
/// First and Second retain current original provenance and mapped ordinal indexes. Suppression
/// does not alter authorship. Valid projections retain every enabled level, including offscreen
/// levels; the fan is Visible if at least one independently clipped ray is visible.
/// </summary>
public sealed record PriceFibonacciFanProjection(PriceFibonacciFan Fan,
    PriceTrendProjectionStatus Status, PriceTrendResolvedAnchor? First, PriceTrendResolvedAnchor? Second,
    ImmutableArray<PriceFibonacciFanLevelProjection> Levels);

public static class PriceFibonacciFanGeometry
{
    /// <summary>
    /// Resolves original identities, selected OHLC bits and aggregate display slots without
    /// changing either authored anchor. First is the fan origin. At Second's display X each
    /// ray passes through Second.Value + ratio * (First.Value - Second.Value). The three fixed
    /// ratios are .382, .5 and .618; only 0, .236 and .764 are optional. No 100% or extra group.
    /// Rays extend from First toward Second, including leftwards authorship, and are clipped
    /// independently against both viewport axes. This extent, mandatory-level policy and false
    /// option defaults are explicit Olivia compatibility choices, not WPF vendor-default parity.
    /// Same-slot aggregates suppress the entire fan rather than invent a direction or anchor.
    /// </summary>
    public static PriceFibonacciFanProjection Project(PriceFibonacciFan fan,
        PriceChartSnapshot current, PriceTimeAggregationSnapshot? aggregation = null)
    {
        ArgumentNullException.ThrowIfNull(fan);
        ArgumentNullException.ThrowIfNull(current);
        var resolved = PriceTrendGeometry.ResolveOriginalAnchors(fan.First, fan.Second, current, aggregation);
        PriceFibonacciFanProjection Suppressed(PriceTrendProjectionStatus status) =>
            new(fan, status, resolved.First, resolved.Second, ImmutableArray<PriceFibonacciFanLevelProjection>.Empty);
        if (resolved.Status != PriceTrendProjectionStatus.Visible) return Suppressed(resolved.Status);
        if (current.GetBarCoordinate(resolved.First!.Value.DisplayIndex) == current.GetBarCoordinate(resolved.Second!.Value.DisplayIndex))
            return Suppressed(PriceTrendProjectionStatus.CollapsedAggregateSlot);
        if (current.BarCount == 0) return Suppressed(PriceTrendProjectionStatus.EmptyViewport);
        if (current.BudgetStatus != ChartBudgetStatus.Satisfied)
            return Suppressed(PriceTrendProjectionStatus.GeometryBudgetExceeded);
        if (current.Domain is not { } yDomain) return Suppressed(PriceTrendProjectionStatus.NoDomain);

        var xDomain = current.XDomain;
        var levels = ImmutableArray.CreateBuilder<PriceFibonacciFanLevelProjection>(6);
        var visible = false;
        void Add(int permille)
        {
            var (price, segment) = AreaChartGeometry.CreatePriceFibonacciFanRay(
                current.GetBarCoordinate(resolved.First!.Value.DisplayIndex), fan.First.Value,
                current.GetBarCoordinate(resolved.Second!.Value.DisplayIndex), fan.Second.Value,
                permille, xDomain, yDomain);
            // A nonrepresentable reference price must not suppress an otherwise visible ray.
            var status = segment.HasValue ? PriceFibonacciFanLevelStatus.Visible :
                price is null ? PriceFibonacciFanLevelStatus.OutOfRange : PriceFibonacciFanLevelStatus.OutsideViewport;
            visible |= status == PriceFibonacciFanLevelStatus.Visible;
            levels.Add(new(permille / 1000d, price, status, segment));
        }
        if (fan.ShowLevel0) Add(0);
        if (fan.ShowLevel23_6) Add(236);
        Add(382);
        Add(500);
        Add(618);
        if (fan.ShowLevel76_4) Add(764);
        return new(fan, visible ? PriceTrendProjectionStatus.Visible : PriceTrendProjectionStatus.OutsideViewport,
            resolved.First, resolved.Second, levels.ToImmutable());
    }
}

public static partial class AreaChartGeometry
{
    // Exact binary-unit rational arithmetic avoids both overflow in opposite-sign subtraction
    // and underflow in subnormal slopes. Only price metadata is rounded, once, to nearest-even.
    // Extent follows authored X direction, deliberately not CreatePriceTrendPositiveRay.
    internal static (double? SecondPrice, HistoryChartLine? Segment) CreatePriceFibonacciFanRay(
        double x0, double first, double x1, double second, int permille,
        HistoryValueDomain xd, HistoryValueDomain yd)
    {
        var firstExact = Exact.FromDouble(first);
        var secondExact = Exact.FromDouble(second);
        var ratio = Exact.FromDouble(permille) / Exact.FromDouble(1000);
        var referenceExact = secondExact + (firstExact - secondExact) * ratio;
        double? price = referenceExact.CompareTo(Exact.FromDouble(-double.MaxValue)) < 0 ||
            referenceExact.CompareTo(Exact.FromDouble(double.MaxValue)) > 0 ? null :
            permille == 0 ? second : FibonacciPriceToDouble(FibonacciBinaryUnits(second) * 1000 +
                (FibonacciBinaryUnits(first) - FibonacciBinaryUnits(second)) * permille, 1000);
        var a = new ExactPoint(Exact.FromDouble(x0), firstExact);
        var q = new ExactPoint(Exact.FromDouble(x1), referenceExact);
        var bounds = new ExactBounds(Exact.FromDouble(xd.Minimum), Exact.FromDouble(xd.Maximum),
            Exact.FromDouble(yd.Minimum), Exact.FromDouble(yd.Maximum));
        var direction = q.X.CompareTo(a.X);
        if (direction == 0 || (direction > 0 ? a.X.CompareTo(bounds.MaxX) >= 0 : a.X.CompareTo(bounds.MinX) <= 0))
            return (price, null);
        var endX = direction > 0 ? bounds.MaxX : bounds.MinX;
        var b = new ExactPoint(endX, a.Y + (q.Y - a.Y) * ((endX - a.X) / (q.X - a.X)));
        if (!ClipSegmentPlane(ref a, ref b, bounds.MinX, true, true) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MaxX, true, false) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MinY, false, true) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MaxY, false, false) || a == b) return (price, null);
        return (price, new(ProjectSegmentPoint(a, bounds), ProjectSegmentPoint(b, bounds)));
    }
}
