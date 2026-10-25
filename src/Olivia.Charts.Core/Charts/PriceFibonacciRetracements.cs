using System.Collections.Immutable;
using System.Numerics;

namespace Olivia.Core.Charts;

/// <summary>
/// An authored Fibonacci Retracement. First is 100% and Second is 0%, preserving input order.
/// The optional flags default to false as an explicit Olivia choice, not vendor-default parity.
/// Horizontal levels span only the two display anchor centers; there are no extension flags.
/// </summary>
public sealed record PriceFibonacciRetracement(long Id, PriceTrendAnchor First, PriceTrendAnchor Second)
{
    public bool ShowLevel23_6 { get; init; }
    public bool ShowLevel76_4 { get; init; }
    public bool ShowAdditionalLevels { get; init; }
}

/// <summary>Only the exact store-issued draft can commit or cancel. Edits preserve prior flags.</summary>
public sealed record PriceFibonacciRetracementDraft(long Revision, long? RetracementId,
    PriceFibonacciRetracement? Original)
{
    public bool ShowLevel23_6 { get; init; }
    public bool ShowLevel76_4 { get; init; }
    public bool ShowAdditionalLevels { get; init; }
}

/// <summary>
/// Separate bounded immutable retracement snapshots. All operations serialize; separate property
/// reads are not a whole-store transaction. IDs never repeat and revisions advance on changes.
/// </summary>
public sealed class PriceFibonacciRetracements
{
    public const int MaximumRetracements = 32;
    private readonly object _gate = new();
    private ImmutableArray<PriceFibonacciRetracement> _items = ImmutableArray<PriceFibonacciRetracement>.Empty;
    private long _revision;
    private long _lastId;
    private long? _selectedId;
    private PriceFibonacciRetracementDraft? _draft;

    public ImmutableArray<PriceFibonacciRetracement> Items { get { lock (_gate) return _items; } }
    public long Revision { get { lock (_gate) return _revision; } }
    public long? SelectedId { get { lock (_gate) return _selectedId; } }
    public PriceFibonacciRetracementDraft? Draft { get { lock (_gate) return _draft; } }

    /// <summary>Starts an empty draft. Full capacity changes nothing, including an existing edit.</summary>
    public PriceFibonacciRetracementDraft? BeginNew(bool showLevel23_6 = false, bool showLevel76_4 = false, bool showAdditionalLevels = false)
    {
        lock (_gate)
        {
            if (_items.Length >= MaximumRetracements) return null;
            var revision = checked(_revision + 1);
            var draft = new PriceFibonacciRetracementDraft(revision, null, null) { ShowLevel23_6 = showLevel23_6, ShowLevel76_4 = showLevel76_4, ShowAdditionalLevels = showAdditionalLevels };
            _revision = revision;
            _selectedId = null;
            _draft = draft;
            return draft;
        }
    }

    /// <summary>Starts an edit and selects the original retracement. A missing ID changes nothing.</summary>
    public PriceFibonacciRetracementDraft? BeginEdit(long id)
    {
        lock (_gate)
        {
            var index = FindIndex(id);
            if (index < 0) return null;
            var original = _items[index];
            var revision = checked(_revision + 1);
            var draft = new PriceFibonacciRetracementDraft(revision, id, original) { ShowLevel23_6 = original.ShowLevel23_6, ShowLevel76_4 = original.ShowLevel76_4, ShowAdditionalLevels = original.ShowAdditionalLevels };
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
    public bool Commit(PriceFibonacciRetracementDraft draft, PriceTrendAnchor first, PriceTrendAnchor second)
    {
        lock (_gate)
        {
            if (!IsCurrent(draft)) return false;
            PriceTrendGeometry.ValidatePair(first, second);
            var revision = checked(_revision + 1);
            ImmutableArray<PriceFibonacciRetracement> items;
            long id;
            if (draft.RetracementId is { } existingId)
            {
                var index = FindIndex(existingId);
                if (index < 0) return false;
                id = existingId;
                items = _items.SetItem(index, new(id, first, second) { ShowLevel23_6 = draft.ShowLevel23_6, ShowLevel76_4 = draft.ShowLevel76_4, ShowAdditionalLevels = draft.ShowAdditionalLevels });
            }
            else
            {
                if (_items.Length >= MaximumRetracements) return false;
                id = checked(_lastId + 1);
                items = _items.Add(new(id, first, second) { ShowLevel23_6 = draft.ShowLevel23_6, ShowLevel76_4 = draft.ShowLevel76_4, ShowAdditionalLevels = draft.ShowAdditionalLevels });
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
    public bool SetOptions(PriceFibonacciRetracement expected, bool showLevel23_6,
        bool showLevel76_4, bool showAdditionalLevels)
    {
        lock (_gate)
        {
            if (expected is null || _draft is not null) return false;
            var index = FindIndex(expected.Id);
            if (index < 0 || !ReferenceEquals(_items[index], expected)) return false;
            if (expected.ShowLevel23_6 == showLevel23_6 && expected.ShowLevel76_4 == showLevel76_4 &&
                expected.ShowAdditionalLevels == showAdditionalLevels) return true;
            var revision = checked(_revision + 1);
            var items = _items.SetItem(index, expected with
            {
                ShowLevel23_6 = showLevel23_6,
                ShowLevel76_4 = showLevel76_4,
                ShowAdditionalLevels = showAdditionalLevels
            });
            _items = items;
            _revision = revision;
            return true;
        }
    }

    /// <summary>Cancels only the current exact token, retaining selection and committed retracements.</summary>
    public bool Cancel(PriceFibonacciRetracementDraft draft)
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

    /// <summary>Selects an existing retracement and cancels any draft. A missing ID changes nothing.</summary>
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

    /// <summary>Clears selection and any draft, but retains committed retracements and IDs.</summary>
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

    /// <summary>Cancels any draft and removes the selected retracement, if any.</summary>
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

    /// <summary>Clears retracements, selection and drafts without reusing IDs during this store's lifetime.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_items.IsEmpty && _selectedId is null && _draft is null) return;
            var revision = checked(_revision + 1);
            _items = ImmutableArray<PriceFibonacciRetracement>.Empty;
            _selectedId = null;
            _draft = null;
            _revision = revision;
        }
    }

    private bool IsCurrent(PriceFibonacciRetracementDraft? draft) =>
        draft is not null && ReferenceEquals(draft, _draft) && draft.Revision == _revision;

    private int FindIndex(long id)
    {
        for (var index = 0; index < _items.Length; index++)
            if (_items[index].Id == id) return index;
        return -1;
    }
}

/// <summary>OutOfRange means the exact mathematical price exceeds finite binary64 range.</summary>
public enum PriceFibonacciRetracementLevelStatus { Visible, OutsideViewport, OutOfRange }

/// <summary>
/// Ratio is a fraction, for example .382 for 38.2%. Price is nearest-even binary64 when the exact
/// price is within finite range, otherwise null. Segment is present only for a visible level.
/// Clipping uses the exact price, not its rounded display value, so no boundary level is invented.
/// </summary>
public sealed record PriceFibonacciRetracementLevelProjection(double Ratio, double? Price,
    PriceFibonacciRetracementLevelStatus Status, HistoryChartLine? Segment);

/// <summary>
/// First and Second retain current original provenance and their mapped ordinal indexes. Invalid
/// provenance suppresses every level without changing the authored record. Valid projection keeps
/// every enabled level, including offscreen and out-of-range levels; Visible means any is visible.
/// </summary>
public sealed record PriceFibonacciRetracementProjection(PriceFibonacciRetracement Retracement,
    PriceTrendProjectionStatus Status, PriceTrendResolvedAnchor? First, PriceTrendResolvedAnchor? Second,
    ImmutableArray<PriceFibonacciRetracementLevelProjection> Levels);

public static class PriceFibonacciRetracementGeometry
{
    /// <summary>
    /// Uses the exact original-anchor resolution, selected-field bit validation and aggregation
    /// mapping shared with trend lines. Each enabled horizontal level is
    /// independently clipped in both axes. Same-slot aggregates suppress the whole retracement.
    /// Levels are Second + ratio * (First - Second), never sorted by price or chronology. Official
    /// first/second endpoint roles inform that formula; bounded extent and flag defaults are Olivia
    /// compatibility choices, not vendor geometry, aggregation or default parity.
    /// </summary>
    public static PriceFibonacciRetracementProjection Project(PriceFibonacciRetracement retracement,
        PriceChartSnapshot current, PriceTimeAggregationSnapshot? aggregation = null)
    {
        ArgumentNullException.ThrowIfNull(retracement);
        ArgumentNullException.ThrowIfNull(current);
        var resolved = PriceTrendGeometry.ResolveOriginalAnchors(retracement.First, retracement.Second,
            current, aggregation);
        PriceFibonacciRetracementProjection Suppressed(PriceTrendProjectionStatus status) =>
            new(retracement, status, resolved.First, resolved.Second,
                ImmutableArray<PriceFibonacciRetracementLevelProjection>.Empty);
        if (resolved.Status != PriceTrendProjectionStatus.Visible) return Suppressed(resolved.Status);
        // Bounded horizontal spans have no width when both original anchors map to one slot.
        // This independent Olivia compatibility policy does not replace either authored anchor.
        if (current.GetBarCoordinate(resolved.First!.Value.DisplayIndex) == current.GetBarCoordinate(resolved.Second!.Value.DisplayIndex))
            return Suppressed(PriceTrendProjectionStatus.CollapsedAggregateSlot);
        if (current.BarCount == 0) return Suppressed(PriceTrendProjectionStatus.EmptyViewport);
        if (current.BudgetStatus != ChartBudgetStatus.Satisfied)
            return Suppressed(PriceTrendProjectionStatus.GeometryBudgetExceeded);
        if (current.Domain is not { } yDomain) return Suppressed(PriceTrendProjectionStatus.NoDomain);

        var levels = ImmutableArray.CreateBuilder<PriceFibonacciRetracementLevelProjection>(10);
        var xDomain = current.XDomain;
        var visible = false;
        void Add(int permille)
        {
            var (price, segment) = AreaChartGeometry.CreatePriceFibonacciRetracementLevel(
                current.GetBarCoordinate(resolved.First!.Value.DisplayIndex), retracement.First.Value,
                current.GetBarCoordinate(resolved.Second!.Value.DisplayIndex), retracement.Second.Value,
                permille, xDomain, yDomain);
            var status = price is null ? PriceFibonacciRetracementLevelStatus.OutOfRange :
                segment.HasValue ? PriceFibonacciRetracementLevelStatus.Visible :
                PriceFibonacciRetracementLevelStatus.OutsideViewport;
            visible |= status == PriceFibonacciRetracementLevelStatus.Visible;
            levels.Add(new(permille / 1000d, price, status, segment));
        }
        Add(0);
        if (retracement.ShowLevel23_6) Add(236);
        Add(382);
        Add(500);
        Add(618);
        if (retracement.ShowLevel76_4) Add(764);
        Add(1000);
        if (retracement.ShowAdditionalLevels)
        {
            Add(1618);
            Add(2618);
            Add(4236);
        }
        return new(retracement, visible ? PriceTrendProjectionStatus.Visible : PriceTrendProjectionStatus.OutsideViewport,
            resolved.First, resolved.Second, levels.ToImmutable());
    }
}

public static partial class AreaChartGeometry
{
    // Reuse exact rational clipping. In particular (MaxValue - -MaxValue) is never evaluated as
    // double, and a level a fraction of an ULP outside a bound is not rounded onto that boundary.
    internal static (double? Price, HistoryChartLine? Segment) CreatePriceFibonacciRetracementLevel(
        double x0, double first, double x1, double second, int permille,
        HistoryValueDomain xd, HistoryValueDomain yd)
    {
        var firstExact = Exact.FromDouble(first);
        var secondExact = Exact.FromDouble(second);
        var ratio = Exact.FromDouble(permille) / Exact.FromDouble(1000);
        var priceExact = secondExact + (firstExact - secondExact) * ratio;
        if (priceExact.CompareTo(Exact.FromDouble(-double.MaxValue)) < 0 ||
            priceExact.CompareTo(Exact.FromDouble(double.MaxValue)) > 0) return (null, null);

        // Preserve original endpoint bits, including negative zero, at 0% and 100%.
        var price = permille == 0 ? second : permille == 1000 ? first :
            FibonacciPriceToDouble(FibonacciBinaryUnits(second) * 1000 +
                (FibonacciBinaryUnits(first) - FibonacciBinaryUnits(second)) * permille, 1000);
        var a = new ExactPoint(Exact.FromDouble(x0), priceExact);
        var b = new ExactPoint(Exact.FromDouble(x1), priceExact);
        var bounds = new ExactBounds(Exact.FromDouble(xd.Minimum), Exact.FromDouble(xd.Maximum),
            Exact.FromDouble(yd.Minimum), Exact.FromDouble(yd.Maximum));
        if (!ClipSegmentPlane(ref a, ref b, bounds.MinX, true, true) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MaxX, true, false) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MinY, false, true) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MaxY, false, false) || a == b) return (price, null);
        return (price, new(ProjectSegmentPoint(a, bounds), ProjectSegmentPoint(b, bounds)));
    }

    private static BigInteger FibonacciBinaryUnits(double value)
    {
        var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        var exponent = (int)((bits >> 52) & 0x7ff);
        var significand = bits & 0x000f_ffff_ffff_ffff;
        if (exponent != 0) significand |= 1UL << 52;
        var units = new BigInteger(significand) << Math.Max(0, exponent - 1);
        return (bits >> 63) != 0 ? -units : units;
    }

    // Convert rational units of 2^-1074 only after the exact finite-range check. Division rounds
    // once, ties to even, including subnormals. No Infinity/Infinity intermediate or price clamp.
    private static double FibonacciPriceToDouble(BigInteger numerator, BigInteger denominator)
    {
        if (numerator.IsZero) return 0;
        var negative = numerator.Sign < 0;
        numerator = BigInteger.Abs(numerator);
        var exponent = (int)(numerator.GetBitLength() - denominator.GetBitLength());
        if (exponent >= 0 ? numerator < (denominator << exponent) :
            (numerator << -exponent) < denominator) exponent--;
        var shift = Math.Max(0, exponent - 52);
        var divisor = denominator << shift;
        var significand = BigInteger.DivRem(numerator, divisor, out var remainder);
        var comparison = (remainder << 1).CompareTo(divisor);
        if (comparison > 0 || (comparison == 0 && !significand.IsEven)) significand++;
        var value = Math.ScaleB((double)significand, shift - 1074);
        return negative ? -value : value;
    }
}
