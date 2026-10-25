using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>The exact original OHLC field selected for an authored endpoint.</summary>
public enum PriceTrendValueLevel { Low, High, Open, Close }

/// <summary>
/// An immutable original hit and field, not an aggregated price or screen coordinate. Value reads
/// the captured immutable bar and retains its original bits. Construction alone does not establish
/// validity or source membership; capture through CreateAnchor and commit through PriceTrendLines.
/// </summary>
public readonly record struct PriceTrendAnchor(PriceChartHit Hit, PriceTrendValueLevel Level)
{
    public double Value => Level switch
    {
        PriceTrendValueLevel.Low => Hit.Bar.Low ?? throw new ArgumentException("A gap cannot anchor a trend line."),
        PriceTrendValueLevel.High => Hit.Bar.High ?? throw new ArgumentException("A gap cannot anchor a trend line."),
        PriceTrendValueLevel.Open => Hit.Bar.Open ?? throw new ArgumentException("A gap cannot anchor a trend line."),
        PriceTrendValueLevel.Close => Hit.Bar.Close ?? throw new ArgumentException("A gap cannot anchor a trend line."),
        _ => throw new ArgumentOutOfRangeException(nameof(Level))
    };
}

/// <summary>
/// An authored segment between two original bars, optionally extended in positive argument-axis
/// direction beyond the larger ordinal X, regardless of anchor input order. This is an Olivia
/// compatibility contract, not official reverse-anchor or coordinate/aggregation parity.
/// </summary>
public sealed record PriceTrendLine(long Id, PriceTrendAnchor First, PriceTrendAnchor Second)
{
    public bool ExtrapolateToInfinity { get; init; }
}

/// <summary>Only the exact store-issued instance can authorize a single commit or cancellation.</summary>
public sealed record PriceTrendLineDraft(long Revision, long? LineId, PriceTrendLine? Original)
{
    public bool ExtrapolateToInfinity { get; init; }
}

/// <summary>
/// Bounded in-memory authored trend lines. Every operation is serialized and Items is an immutable
/// snapshot; separate property reads are not one atomic whole-store snapshot. Drafts do not alter
/// committed endpoints. Revisions advance only on state changes, and IDs are never reused.
/// </summary>
public sealed class PriceTrendLines
{
    public const int MaximumLines = 32;
    private readonly object _gate = new();
    private ImmutableArray<PriceTrendLine> _items = ImmutableArray<PriceTrendLine>.Empty;
    private long _revision;
    private long _lastId;
    private long? _selectedId;
    private PriceTrendLineDraft? _draft;

    public ImmutableArray<PriceTrendLine> Items { get { lock (_gate) return _items; } }
    public long Revision { get { lock (_gate) return _revision; } }
    public long? SelectedId { get { lock (_gate) return _selectedId; } }
    public PriceTrendLineDraft? Draft { get { lock (_gate) return _draft; } }

    /// <summary>Starts an empty draft. Full capacity changes nothing, including an existing edit.</summary>
    public PriceTrendLineDraft? BeginNew(bool extrapolateToInfinity = false)
    {
        lock (_gate)
        {
            if (_items.Length >= MaximumLines) return null;
            var revision = checked(_revision + 1);
            var draft = new PriceTrendLineDraft(revision, null, null) { ExtrapolateToInfinity = extrapolateToInfinity };
            _revision = revision;
            _selectedId = null;
            _draft = draft;
            return draft;
        }
    }

    /// <summary>Starts an edit and selects the original line. A missing ID changes nothing.</summary>
    public PriceTrendLineDraft? BeginEdit(long id)
    {
        lock (_gate)
        {
            var index = FindIndex(id);
            if (index < 0) return null;
            var original = _items[index];
            var revision = checked(_revision + 1);
            var draft = new PriceTrendLineDraft(revision, id, original) { ExtrapolateToInfinity = original.ExtrapolateToInfinity };
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
    public bool Commit(PriceTrendLineDraft draft, PriceTrendAnchor first, PriceTrendAnchor second)
    {
        lock (_gate)
        {
            if (!IsCurrent(draft)) return false;
            PriceTrendGeometry.ValidatePair(first, second);
            var revision = checked(_revision + 1);
            ImmutableArray<PriceTrendLine> items;
            long id;
            if (draft.LineId is { } existingId)
            {
                var index = FindIndex(existingId);
                if (index < 0) return false;
                id = existingId;
                items = _items.SetItem(index, new(id, first, second) { ExtrapolateToInfinity = draft.ExtrapolateToInfinity });
            }
            else
            {
                if (_items.Length >= MaximumLines) return false;
                id = checked(_lastId + 1);
                items = _items.Add(new(id, first, second) { ExtrapolateToInfinity = draft.ExtrapolateToInfinity });
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
    /// Changes only the extension flag of the exact retained line instance. Stale or copied lines
    /// and any active draft refuse the change. An already matching value succeeds without mutation.
    /// IDs, original anchors and selection are preserved; prior immutable snapshots remain valid.
    /// </summary>
    public bool SetExtrapolateToInfinity(PriceTrendLine expected, bool value)
    {
        lock (_gate)
        {
            if (expected is null || _draft is not null) return false;
            var index = FindIndex(expected.Id);
            if (index < 0 || !ReferenceEquals(_items[index], expected)) return false;
            if (expected.ExtrapolateToInfinity == value) return true;
            var revision = checked(_revision + 1);
            var items = _items.SetItem(index, expected with { ExtrapolateToInfinity = value });
            _items = items;
            _revision = revision;
            return true;
        }
    }

    /// <summary>Cancels only the current exact token, retaining selection and committed lines.</summary>
    public bool Cancel(PriceTrendLineDraft draft)
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

    /// <summary>Selects an existing line and cancels any draft. A missing ID changes nothing.</summary>
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

    /// <summary>Clears selection and any draft, but retains committed lines and IDs.</summary>
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

    /// <summary>Cancels any draft and removes the selected line, if any.</summary>
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

    /// <summary>Clears lines, selection and drafts without reusing IDs during this store's lifetime.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_items.IsEmpty && _selectedId is null && _draft is null) return;
            var revision = checked(_revision + 1);
            _items = ImmutableArray<PriceTrendLine>.Empty;
            _selectedId = null;
            _draft = null;
            _revision = revision;
        }
    }

    private bool IsCurrent(PriceTrendLineDraft? draft) =>
        draft is not null && ReferenceEquals(draft, _draft) && draft.Revision == _revision;

    private int FindIndex(long id)
    {
        for (var index = 0; index < _items.Length; index++)
            if (_items[index].Id == id) return index;
        return -1;
    }
}

public enum PriceTrendProjectionStatus
{
    Visible, OutsideViewport, EmptyViewport, NoDomain, GeometryBudgetExceeded,
    DatasetMismatch, MissingAnchor, GapAnchor, ChangedAnchorValue,
    AggregationMismatch, CollapsedAggregateSlot
}

/// <summary>The current original hit and its raw/derived ordinal indexes, never an aggregate hit.</summary>
public readonly record struct PriceTrendResolvedAnchor(PriceChartHit Hit, int OriginalIndex, int DisplayIndex);

/// <summary>
/// Authored values remain in Line; resolved anchors contain current original provenance when
/// available. Segment is a finite normalized [0,1] screen line, with Y increasing downwards, only
/// for Visible. Suppression never modifies the authored line.
/// </summary>
public sealed record PriceTrendProjection(PriceTrendLine Line, PriceTrendProjectionStatus Status,
    PriceTrendResolvedAnchor? First, PriceTrendResolvedAnchor? Second, HistoryChartLine? Segment);

public static class PriceTrendGeometry
{
    /// <summary>
    /// Captures an actual visible original candle by original bar index. The caller must supply
    /// a raw, not aggregated, snapshot. Gaps, clipped-away chosen levels, offscreen bars and
    /// budget-refused frames cannot be captured. No synthetic endpoint or nearest bar is invented.
    /// </summary>
    public static PriceTrendAnchor? CreateAnchor(PriceChartSnapshot raw, int barIndex, PriceTrendValueLevel level)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ValidateLevel(level, nameof(level));
        if (raw.BudgetStatus != ChartBudgetStatus.Satisfied || raw.Domain is not { } domain) return null;
        foreach (var candle in raw.Candles)
        {
            if (candle.BarIndex != barIndex) continue;
            var anchor = new PriceTrendAnchor(candle.Hit, level);
            return anchor.Value >= domain.Minimum && anchor.Value <= domain.Maximum ? anchor : null;
        }
        return null;
    }

    /// <summary>
    /// Resolves exact original identity (series, dataset, key, timestamp ticks and offset), checks
    /// the selected OHLC bits, then clips the ordinal segment or positive-X ray in both axes.
    /// Finite segments retain input direction; rays project from lower to higher X. SourceVersion
    /// may advance; changed selected values, evicted anchors and gaps suppress the whole line.
    /// For aggregation, current must be queried from aggregation.Source. Each original index maps
    /// to its containing segment, retaining its original Y. Same-slot endpoints are suppressed.
    /// Constant domains retain the existing center convention; a single-point intersection is not
    /// a segment. This is an Olivia compatibility projection, not official aggregation parity.
    /// </summary>
    public static PriceTrendProjection Project(PriceTrendLine line, PriceChartSnapshot current,
        PriceTimeAggregationSnapshot? aggregation = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(current);
        var resolved = ResolveOriginalAnchors(line.First, line.Second, current, aggregation);
        var first = resolved.First;
        var second = resolved.Second;
        PriceTrendProjection Result(PriceTrendProjectionStatus status, HistoryChartLine? segment = null)
            => new(line, status, first, second, segment);
        if (resolved.Status != PriceTrendProjectionStatus.Visible) return Result(resolved.Status);
        if (aggregation is not null && current.GetBarCoordinate(first!.Value.DisplayIndex) == current.GetBarCoordinate(second!.Value.DisplayIndex))
            return Result(PriceTrendProjectionStatus.CollapsedAggregateSlot);

        if (current.BarCount == 0) return Result(PriceTrendProjectionStatus.EmptyViewport);
        if (current.BudgetStatus != ChartBudgetStatus.Satisfied)
            return Result(PriceTrendProjectionStatus.GeometryBudgetExceeded);
        if (current.Domain is not { } domain) return Result(PriceTrendProjectionStatus.NoDomain);
        var xDomain = current.XDomain;
        var segment = line.ExtrapolateToInfinity
            ? AreaChartGeometry.CreatePriceTrendPositiveRay(
                current.GetBarCoordinate(first!.Value.DisplayIndex), line.First.Value,
                current.GetBarCoordinate(second!.Value.DisplayIndex), line.Second.Value, xDomain, domain)
            : AreaChartGeometry.CreateStackedSplineBoundary(
                current.GetBarCoordinate(first!.Value.DisplayIndex), line.First.Value,
                current.GetBarCoordinate(second!.Value.DisplayIndex), line.Second.Value, xDomain, domain);
        return Result(segment.HasValue ? PriceTrendProjectionStatus.Visible : PriceTrendProjectionStatus.OutsideViewport, segment);
    }

    // Shared original provenance resolution only. Callers retain their own geometry, extent,
    // viewport and same-slot policies; this does not decide whether anything is drawable.
    internal static (PriceTrendProjectionStatus Status, PriceTrendResolvedAnchor? First,
        PriceTrendResolvedAnchor? Second) ResolveOriginalAnchors(PriceTrendAnchor firstAnchor,
        PriceTrendAnchor secondAnchor, PriceChartSnapshot current, PriceTimeAggregationSnapshot? aggregation)
    {
        ArgumentNullException.ThrowIfNull(current);
        ValidatePair(firstAnchor, secondAnchor);
        PriceTrendResolvedAnchor? first = null, second = null;
        (PriceTrendProjectionStatus, PriceTrendResolvedAnchor?, PriceTrendResolvedAnchor?) Result(
            PriceTrendProjectionStatus status) => (status, first, second);
        if (aggregation is not null && !ReferenceEquals(current.Source, aggregation.Source))
            return Result(PriceTrendProjectionStatus.AggregationMismatch);
        var original = aggregation?.OriginalSource ?? current.Source;
        if (!string.Equals(original.SeriesId, firstAnchor.Hit.SeriesId, StringComparison.Ordinal) ||
            original.DatasetVersion != firstAnchor.Hit.DatasetVersion)
            return Result(PriceTrendProjectionStatus.DatasetMismatch);
        var firstStatus = Resolve(firstAnchor, original, out first);
        var secondStatus = Resolve(secondAnchor, original, out second);
        if (firstStatus != PriceTrendProjectionStatus.Visible) return Result(firstStatus);
        if (secondStatus != PriceTrendProjectionStatus.Visible) return Result(secondStatus);
        if (aggregation is not null)
        {
            var firstIndex = AggregateIndex(aggregation, first!.Value.OriginalIndex);
            var secondIndex = AggregateIndex(aggregation, second!.Value.OriginalIndex);
            if (firstIndex < 0 || secondIndex < 0) return Result(PriceTrendProjectionStatus.AggregationMismatch);
            first = first!.Value with { DisplayIndex = firstIndex };
            second = second!.Value with { DisplayIndex = secondIndex };
        }
        return Result(PriceTrendProjectionStatus.Visible);
    }

    internal static void ValidatePair(PriceTrendAnchor first, PriceTrendAnchor second)
    {
        ValidateAnchor(first, nameof(first));
        ValidateAnchor(second, nameof(second));
        if (!string.Equals(first.Hit.SeriesId, second.Hit.SeriesId, StringComparison.Ordinal) ||
            first.Hit.DatasetVersion != second.Hit.DatasetVersion)
            throw new ArgumentException("Both anchors must belong to the same original series and dataset.", nameof(second));
        if (first.Hit.Key == second.Hit.Key || first.Hit.Timestamp == second.Hit.Timestamp)
            throw new ArgumentException("Anchors must have distinct original keys and UTC timestamps.", nameof(second));
    }

    private static void ValidateAnchor(PriceTrendAnchor anchor, string parameterName)
    {
        ValidateLevel(anchor.Level, parameterName);
        BarChartSource.ValidateText(anchor.Hit.SeriesId, parameterName, identity: true);
        if (anchor.Hit.DatasetVersion < 0 || anchor.Hit.SourceVersion < 0 || anchor.Hit.Bar.IsGap)
            throw new ArgumentException("An anchor requires valid original versions and a non-gap OHLC bar.", parameterName);
    }

    private static void ValidateLevel(PriceTrendValueLevel level, string parameterName)
    {
        if (level is not (PriceTrendValueLevel.Low or PriceTrendValueLevel.High or PriceTrendValueLevel.Open or PriceTrendValueLevel.Close))
            throw new ArgumentOutOfRangeException(parameterName, "Choose an original OHLC value level.");
    }

    private static PriceTrendProjectionStatus Resolve(PriceTrendAnchor anchor, PriceChartSource source,
        out PriceTrendResolvedAnchor? resolved)
    {
        resolved = null;
        for (var index = 0; index < source.Bars.Length; index++)
        {
            var bar = source.Bars[index];
            if (bar.Key != anchor.Hit.Key) continue;
            if (bar.Timestamp.Ticks != anchor.Hit.Timestamp.Ticks || bar.Timestamp.Offset != anchor.Hit.Timestamp.Offset)
                return PriceTrendProjectionStatus.MissingAnchor;
            var hit = new PriceChartHit(source.DatasetVersion, source.SeriesId, source.SourceVersion, bar);
            resolved = new(hit, index, index);
            if (bar.IsGap) return PriceTrendProjectionStatus.GapAnchor;
            if (BitConverter.DoubleToInt64Bits(new PriceTrendAnchor(hit, anchor.Level).Value) !=
                BitConverter.DoubleToInt64Bits(anchor.Value))
                return PriceTrendProjectionStatus.ChangedAnchorValue;
            return PriceTrendProjectionStatus.Visible;
        }
        return PriceTrendProjectionStatus.MissingAnchor;
    }

    private static int AggregateIndex(PriceTimeAggregationSnapshot aggregation, int originalIndex)
    {
        for (var index = 0; index < aggregation.Segments.Length; index++)
        {
            var segment = aggregation.Segments[index];
            if (originalIndex >= segment.OriginalStartIndex &&
                originalIndex < segment.OriginalStartIndex + segment.OriginalCount) return index;
        }
        return -1;
    }
}

public static partial class AreaChartGeometry
{
    // Intersect the positive-X ray with the finite viewport before projecting. Exact rational Y
    // at the right boundary can exceed binary64 without overflow or a synthetic Infinity endpoint.
    // The authored segment is included; input order never reverses the extension toward negative X.
    internal static HistoryChartLine? CreatePriceTrendPositiveRay(double x0, double y0, double x1, double y1,
        HistoryValueDomain xd, HistoryValueDomain yd)
    {
        var a = new ExactPoint(Exact.FromDouble(x0), Exact.FromDouble(y0));
        var b = new ExactPoint(Exact.FromDouble(x1), Exact.FromDouble(y1));
        if (a.X.CompareTo(b.X) > 0) (a, b) = (b, a);
        var bounds = new ExactBounds(Exact.FromDouble(xd.Minimum), Exact.FromDouble(xd.Maximum),
            Exact.FromDouble(yd.Minimum), Exact.FromDouble(yd.Maximum));
        if (a.X == b.X || a.X.CompareTo(bounds.MaxX) >= 0) return null;
        b = new(bounds.MaxX, a.Y + (b.Y - a.Y) * ((bounds.MaxX - a.X) / (b.X - a.X)));
        if (!ClipSegmentPlane(ref a, ref b, bounds.MinX, true, true) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MinY, false, true) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MaxY, false, false) || a == b) return null;
        return new(ProjectSegmentPoint(a, bounds), ProjectSegmentPoint(b, bounds));
    }
}
