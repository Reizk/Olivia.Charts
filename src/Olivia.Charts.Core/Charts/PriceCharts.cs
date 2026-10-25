using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>One original immutable OHLC bar with optional volume. All four OHLC nulls denote an
/// explicit gap; partial, nonfinite and inconsistent OHLC values are rejected. Missing volume is
/// null, not zero; present volume must be finite and nonnegative. Requiring null volume on an OHLC
/// gap is an Olivia compatibility constraint, not a claim of official FinancialDataPoint identity.
/// Timestamp offsets and all original numeric bits, including signed zero, are retained.</summary>
public readonly record struct PriceBar
{
    public PriceBar(long key, DateTimeOffset timestamp, double? open, double? high, double? low, double? close)
        : this(key, timestamp, open, high, low, close, null) { }

    public PriceBar(long key, DateTimeOffset timestamp, double? open, double? high, double? low,
        double? close, double? volume)
    {
        var gap = open is null && high is null && low is null && close is null;
        if (!gap && (open is null || high is null || low is null || close is null ||
            !double.IsFinite(open.Value) || !double.IsFinite(high.Value) ||
            !double.IsFinite(low.Value) || !double.IsFinite(close.Value)))
            throw new ArgumentException("OHLC must be all null or all finite.");
        if (!gap && (low > high || open < low || open > high || close < low || close > high))
            throw new ArgumentException("OHLC requires low <= open, close <= high.");
        if (volume.HasValue && (!double.IsFinite(volume.Value) || volume.Value < 0))
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be null or finite and nonnegative.");
        if (gap && volume.HasValue)
            throw new ArgumentException("An explicit OHLC gap must have null volume.", nameof(volume));
        Key = key; Timestamp = timestamp; Open = open; High = high; Low = low; Close = close;
        Volume = volume;
    }
    public long Key { get; }
    public DateTimeOffset Timestamp { get; }
    public double? Open { get; }
    public double? High { get; }
    public double? Low { get; }
    public double? Close { get; }
    public double? Volume { get; }
    public bool IsGap => Open is null;
}

/// <summary>Raw ordinal viewport by default; TimeViewport opts aggregated sources into
/// compressed UTC bucket spacing and independent numeric bounds. Prices never anchor to zero.</summary>
public sealed class PriceChartOptions
{
    public PriceChartOptions(int barStart = 0, int barCount = PriceChartSource.MaximumBars,
        int geometryBudget = PriceChartSource.MaximumBars, HistoryValueDomain? fixedDomain = null,
        double bodyWidthFraction = .7, PriceTimeViewport? timeViewport = null)
    {
        if (barStart < 0) throw new ArgumentOutOfRangeException(nameof(barStart));
        if (barCount < 0 || barCount > PriceChartSource.MaximumBars) throw new ArgumentOutOfRangeException(nameof(barCount));
        if (geometryBudget < 0 || geometryBudget > PriceChartSource.MaximumBars) throw new ArgumentOutOfRangeException(nameof(geometryBudget));
        if (!double.IsFinite(bodyWidthFraction) || bodyWidthFraction <= 0 || bodyWidthFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(bodyWidthFraction));
        BarStart = barStart; BarCount = barCount; GeometryBudget = geometryBudget;
        FixedDomain = fixedDomain; BodyWidthFraction = bodyWidthFraction; TimeViewport = timeViewport;
    }
    public static PriceChartOptions Default { get; } = new();
    public int BarStart { get; }
    public int BarCount { get; }
    public int GeometryBudget { get; }
    public HistoryValueDomain? FixedDomain { get; }
    public double BodyWidthFraction { get; }
    public PriceTimeViewport? TimeViewport { get; }
    public PriceChartOptions WithTimeViewport(PriceTimeViewport? viewport) =>
        new(BarStart, BarCount, GeometryBudget, FixedDomain, BodyWidthFraction, viewport);
}

public readonly record struct PriceChartHit(long DatasetVersion, string SeriesId, long SourceVersion, PriceBar Bar)
{
    public long Key => Bar.Key;
    public DateTimeOffset Timestamp => Bar.Timestamp;
}

/// <summary>Normalized screen rectangle. Exact dojis and rounded-away spans retain zero height;
/// no minimum size is invented. A clipped-away body is null, not a boundary rectangle.</summary>
public readonly record struct PriceChartBody(double Left, double Top, double Width, double Height);

/// <summary>Wick is the clipped raw low-high center line; the body overlays it.
/// Either component can be absent after raw-domain clipping. Y increases downwards.</summary>
public readonly record struct PriceChartCandle(PriceChartHit Hit, int BarIndex, double CenterX,
    PriceChartBody? Body, HistoryChartLine? Wick);

public sealed class PriceChartSnapshot
{
    internal PriceChartSnapshot(PriceChartSource source, PriceChartOptions options, int start, int count,
        HistoryValueDomain? domain, ImmutableArray<PriceChartCandle> candles,
        ImmutableArray<PriceChartHit> gaps, int required)
    {
        Source = source; Options = options; BarStart = start; BarCount = count; Domain = domain;
        Candles = candles; Gaps = gaps; RequiredCandleCount = required;
        BudgetStatus = required <= options.GeometryBudget ? ChartBudgetStatus.Satisfied : ChartBudgetStatus.InsufficientBudget;
        Ticks = DualAxisChartGeometry.CreateTicks(domain);
    }
    public PriceChartSource Source { get; }
    public PriceChartOptions Options { get; }
    public long DatasetVersion => Source.DatasetVersion;
    public int BarStart { get; }
    public int BarCount { get; }
    public HistoryValueDomain? Domain { get; }
    public ImmutableArray<HistoryAxisTick> Ticks { get; }
    public ImmutableArray<PriceChartCandle> Candles { get; }
    public ImmutableArray<PriceChartHit> Gaps { get; }
    public int RequiredCandleCount { get; }
    public ChartBudgetStatus BudgetStatus { get; }
    public HistoryValueDomain XDomain => Options.TimeViewport is { } time
        ? new(time.Range.Minimum, time.Range.Maximum) : new(BarStart, BarStart + BarCount);
    public double XUnitWidth => Options.TimeViewport is { } time ? 1 / time.Range.Length : BarCount > 0 ? 1d / BarCount : 0;
    public double GetBarCoordinate(int index) => Options.TimeViewport is { } time
        ? time.Axis.GetSegmentCoordinate(index) : index + .5;
    public double GetNormalizedX(int index) => Options.TimeViewport is { } time
        ? time.Range.Normalize(time.Axis.GetSegmentCoordinate(index)) : (index - BarStart + .5) / BarCount;
    public int? FindBarAt(double normalizedX)
    {
        if (!double.IsFinite(normalizedX) || normalizedX < 0 || normalizedX > 1 || BarCount == 0) return null;
        return Options.TimeViewport is { } time ? time.FindSegment(normalizedX)
            : BarStart + Math.Min((int)Math.Floor(normalizedX * BarCount), BarCount - 1);
    }
    /// <summary>Clips a full-source indicator segment using the same X projection as candles.
    /// Neighboring offscreen points can contribute a genuine segment crossing the boundary.</summary>
    public HistoryChartLine? ClipValueSegment(int firstIndex, double first, int secondIndex,
        double second, HistoryValueDomain domain) => Options.TimeViewport is not null
        ? AreaChartGeometry.CreateStackedSplineBoundary(GetBarCoordinate(firstIndex), first,
            GetBarCoordinate(secondIndex), second, XDomain, domain)
        : DualAxisChartGeometry.ClipSegment(new HistorySample(firstIndex, 2L * firstIndex + 1, first),
            new HistorySample(secondIndex, 2L * secondIndex + 1, second),
            new TimeWindow(2L * BarStart, 2L * (BarStart + BarCount)), domain, 0, 0, 1, 1);
}

/// <summary>Single-series immutable bounded OHLC source. At most MaximumBars + 1 enumeration advances admit
/// or reject input; no aggregation, sorting, imputation or downsampling. Keys must be stable across
/// versions (caller responsibility), unique here, and timestamps strictly increasing in UTC.</summary>
public sealed class PriceChartSource
{
    public const int MaximumBars = 18000;
    // A private, non-chain token certifies that every bar except the tail is unchanged.
    // Only this class can carry it into a new immutable source; versions alone never suffice.
    internal object HistoryIdentity { get; } = new();
    public PriceChartSource(string seriesId, string label, IEnumerable<PriceBar> bars,
        long datasetVersion = 0, long sourceVersion = 0)
    {
        BarChartSource.ValidateText(seriesId, nameof(seriesId), identity: true);
        BarChartSource.ValidateText(label, nameof(label));
        if (datasetVersion < 0) throw new ArgumentOutOfRangeException(nameof(datasetVersion));
        if (sourceVersion < 0) throw new ArgumentOutOfRangeException(nameof(sourceVersion));
        Bars = BarChartSource.CopyBounded(bars, MaximumBars, nameof(bars));
        var keys = new HashSet<long>();
        for (var i = 0; i < Bars.Length; i++)
        {
            if (!keys.Add(Bars[i].Key)) throw new ArgumentException("Duplicate bar key.", nameof(bars));
            if (i > 0 && Bars[i].Timestamp <= Bars[i - 1].Timestamp)
                throw new ArgumentException("Bar timestamps must be strictly increasing UTC instants.", nameof(bars));
        }
        SeriesId = seriesId; Label = label; DatasetVersion = datasetVersion; SourceVersion = sourceVersion;
    }
    public string SeriesId { get; }
    public string Label { get; }
    public long DatasetVersion { get; }
    public long SourceVersion { get; }
    public ImmutableArray<PriceBar> Bars { get; }

    private PriceChartSource(PriceChartSource previous, PriceBar tail, long sourceVersion)
    {
        if (previous.Bars.IsEmpty || tail.Key != previous.Bars[^1].Key ||
            !tail.Timestamp.EqualsExact(previous.Bars[^1].Timestamp) || sourceVersion <= previous.SourceVersion)
            throw new ArgumentException("A tail update must retain its identity and advance the source version.");
        Bars = previous.Bars.SetItem(previous.Bars.Length - 1, tail);
        SeriesId = previous.SeriesId; Label = previous.Label;
        DatasetVersion = previous.DatasetVersion; SourceVersion = sourceVersion;
        HistoryIdentity = previous.HistoryIdentity;
    }

    internal PriceChartSource ReplaceTail(PriceBar tail, long sourceVersion) => new(this, tail, sourceVersion);

    internal bool HasSameHistory(PriceChartSource previous) =>
        ReferenceEquals(HistoryIdentity, previous.HistoryIdentity) && Bars.Length == previous.Bars.Length;

    public PriceChartSnapshot Query(PriceChartOptions? options = null) => Query(options, null, 0);

    /// <summary>Combines visible price values with a caller's visible price overlay and then
    /// applies absolute padding before constructing geometry once. FixedDomain takes precedence;
    /// empty/gap-only views cannot acquire a domain from an overlay. Options remains the request.</summary>
    public PriceChartSnapshot Query(PriceChartOptions? options, HistoryValueDomain? autoDomainOverlay,
        double autoDomainMargin)
    {
        if (!double.IsFinite(autoDomainMargin) || autoDomainMargin < 0)
            throw new ArgumentOutOfRangeException(nameof(autoDomainMargin));
        options ??= PriceChartOptions.Default;
        var start = Math.Min(options.BarStart, Bars.Length);
        var count = Math.Min(options.BarCount, Bars.Length - start);
        var time = options.TimeViewport;
        if (time is not null)
        {
            if (!ReferenceEquals(time.Axis.Aggregation.Source, this))
                throw new ArgumentException("The time viewport must belong to this exact aggregated source.", nameof(options));
            (start, count) = time.SelectSegments();
        }
        double? minimum = null, maximum = null;
        var gaps = ImmutableArray.CreateBuilder<PriceChartHit>();
        for (var i = start; i < start + count; i++)
        {
            var bar = Bars[i];
            if (bar.IsGap) { gaps.Add(Hit(bar)); continue; }
            minimum = minimum.HasValue ? Math.Min(minimum.Value, bar.Low!.Value) : bar.Low;
            maximum = maximum.HasValue ? Math.Max(maximum.Value, bar.High!.Value) : bar.High;
        }
        HistoryValueDomain? domain = count == 0 ? null : options.FixedDomain ??
            (minimum.HasValue ? new HistoryValueDomain(minimum.Value, maximum!.Value) : null);
        if (options.FixedDomain is null && domain is { } rawDomain)
        {
            var low = autoDomainOverlay is { } overlay ? Math.Min(rawDomain.Minimum, overlay.Minimum) : rawDomain.Minimum;
            var high = autoDomainOverlay is { } upper ? Math.Max(rawDomain.Maximum, upper.Maximum) : rawDomain.Maximum;
            domain = new(Math.Max(-double.MaxValue, low - autoDomainMargin),
                Math.Min(double.MaxValue, high + autoDomainMargin));
        }
        var candles = ImmutableArray.CreateBuilder<PriceChartCandle>();
        if (domain.HasValue)
        {
            var bounds = domain.Value;
            for (var i = start; i < start + count; i++)
            {
                var bar = Bars[i];
                if (bar.IsGap) continue;
                var low = Math.Max(bar.Low!.Value, bounds.Minimum);
                var high = Math.Min(bar.High!.Value, bounds.Maximum);
                // Reject before normalization: clamping alone would invent an off-scale wick.
                if (low > high) continue;
                var center = time is null ? (i - start + .5) / count : time.Range.Normalize(time.Axis.GetSegmentCoordinate(i));
                var wick = new HistoryChartLine(new(center, ScreenY(high)), new(center, ScreenY(low)));
                PriceChartBody? body = null;
                var bodyLow = Math.Max(Math.Min(bar.Open!.Value, bar.Close!.Value), bounds.Minimum);
                var bodyHigh = Math.Min(Math.Max(bar.Open.Value, bar.Close.Value), bounds.Maximum);
                if (bodyLow <= bodyHigh)
                {
                    var top = ScreenY(bodyHigh);
                    var width = options.BodyWidthFraction / (time?.Range.Length ?? count);
                    var left = Math.Max(0, center - width / 2);
                    var right = Math.Min(1, center + width / 2);
                    body = time is null
                        ? new((i - start + (1 - options.BodyWidthFraction) / 2) / count,
                            top, width, Math.Max(0, ScreenY(bodyLow) - top))
                        : new(left, top, Math.Max(0, right - left), Math.Max(0, ScreenY(bodyLow) - top));
                }
                candles.Add(new(Hit(bar), i, center, body, wick));
            }
            // Normalize directly in the reversed domain: 1 - normalizedY would erase
            // representable tiny screen coordinates near the upper boundary.
            double ScreenY(double value) => DualAxisChartGeometry.NormalizeY(-value,
                new HistoryValueDomain(-bounds.Maximum, -bounds.Minimum));
        }
        var required = candles.Count;
        return new(this, options, start, count, domain,
            required <= options.GeometryBudget ? candles.ToImmutable() : [], gaps.ToImmutable(), required);
    }
    private PriceChartHit Hit(PriceBar bar) => new(DatasetVersion, SeriesId, SourceVersion, bar);
}

public static class PriceChartGeometry
{
    /// <summary>Tests the union of actual clipped body and wick geometry. Tolerance is explicit,
    /// independently bounded by [0,1] per axis. Last drawn (latest ordinal) wins overlap ties.
    /// Coordinates outside the viewport never hit, including when tolerance is positive.</summary>
    public static PriceChartHit? HitTest(PriceChartSnapshot snapshot, double x, double y, double tolerance = 0)
        => HitTest(snapshot, x, y, tolerance, tolerance);
    public static PriceChartHit? HitTest(PriceChartSnapshot snapshot, double x, double y,
        double xTolerance, double yTolerance)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (!double.IsFinite(xTolerance) || xTolerance < 0 || xTolerance > 1) throw new ArgumentOutOfRangeException(nameof(xTolerance));
        if (!double.IsFinite(yTolerance) || yTolerance < 0 || yTolerance > 1) throw new ArgumentOutOfRangeException(nameof(yTolerance));
        if (x < 0 || x > 1 || y < 0 || y > 1) return null;
        // A neighbor's expanded hit target must not steal a pointer inside actual ink.
        // Dense compressed time windows can put several candles within five screen pixels.
        if ((xTolerance > 0 || yTolerance > 0) && HitTest(snapshot, x, y, 0, 0) is { } exact)
            return exact;
        for (var i = snapshot.Candles.Length - 1; i >= 0; i--)
        {
            var candle = snapshot.Candles[i];
            if (candle.Body is { } body && x >= body.Left - xTolerance && x <= body.Left + body.Width + xTolerance &&
                y >= body.Top - yTolerance && y <= body.Top + body.Height + yTolerance) return candle.Hit;
            if (candle.Wick is { } wick && x >= wick.Start.X - xTolerance && x <= wick.Start.X + xTolerance &&
                y >= wick.Start.Y - yTolerance && y <= wick.End.Y + yTolerance) return candle.Hit;
        }
        return null;
    }
    /// <summary>Stable keyboard traversal includes dojis, collapsed and occluded geometry.
    /// Gaps, wholly clipped bars and budget-rejected frames have no selectable geometry.</summary>
    public static PriceChartHit? GetHit(PriceChartSnapshot snapshot, int index)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return index >= 0 && index < snapshot.Candles.Length ? snapshot.Candles[index].Hit : null;
    }
}
