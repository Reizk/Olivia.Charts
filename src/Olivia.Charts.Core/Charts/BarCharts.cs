using System.Collections.Immutable;

namespace Olivia.Core.Charts;

public enum BarChartLayout { Grouped, Overlap, Stacked, FullStacked }

/// <summary>Stable ordinal key and display-only label. Optional UTC ticks are metadata;
/// categories retain caller order and equal-width spacing, not continuous-time spacing.</summary>
public sealed record BarCategory
{
    public BarCategory(string key, string label, long? utcTicks = null)
    {
        BarChartSource.ValidateText(key, nameof(key), identity: true);
        BarChartSource.ValidateText(label, nameof(label));
        if (utcTicks is < 0 || utcTicks > DateTime.MaxValue.Ticks)
            throw new ArgumentOutOfRangeException(nameof(utcTicks));
        Key = key; Label = label; UtcTicks = utcTicks;
    }
    public string Key { get; }
    public string Label { get; }
    public long? UtcTicks { get; }
}

public readonly record struct BarSample(long Key, string CategoryKey, double? Value)
{
    public bool IsGap => !Value.HasValue || !double.IsFinite(Value.Value);
}

public sealed class BarSeries
{
    public BarSeries(string id, string label, IEnumerable<BarSample> samples, long sourceVersion = 0)
    {
        BarChartSource.ValidateText(id, nameof(id), identity: true);
        BarChartSource.ValidateText(label, nameof(label));
        if (sourceVersion < 0) throw new ArgumentOutOfRangeException(nameof(sourceVersion));
        Samples = BarChartSource.CopyBounded(samples, BarChartSource.MaximumRawEntries, nameof(samples));
        var keys = new HashSet<long>();
        foreach (var sample in Samples)
        {
            BarChartSource.ValidateText(sample.CategoryKey, nameof(samples), identity: true);
            if (!keys.Add(sample.Key)) throw new ArgumentException("Duplicate sample key within series.", nameof(samples));
        }
        Id = id; Label = label; SourceVersion = sourceVersion;
    }
    public string Id { get; }
    public string Label { get; }
    public long SourceVersion { get; }
    public ImmutableArray<BarSample> Samples { get; }
}

/// <summary>Immutable bounded viewport and geometry admission options. Fixed Y domains clip
/// zero-to-value intervals for Grouped/Overlap, cumulative intervals for stacks.
/// They do not move the underlying zero baseline or modify raw values.</summary>
public sealed class BarChartOptions
{
    public BarChartOptions(BarChartLayout layout = BarChartLayout.Grouped, int categoryStart = 0,
        int categoryCount = BarChartSource.MaximumCategories, int geometryBudget = BarChartSource.MaximumRawEntries,
        HistoryValueDomain? fixedDomain = null, int stackWorkBudget = BarChartSource.MaximumStackWork)
    {
        if (!Enum.IsDefined(layout)) throw new ArgumentOutOfRangeException(nameof(layout));
        if (categoryStart < 0) throw new ArgumentOutOfRangeException(nameof(categoryStart));
        if (categoryCount < 0 || categoryCount > BarChartSource.MaximumCategories)
            throw new ArgumentOutOfRangeException(nameof(categoryCount));
        if (geometryBudget < 0 || geometryBudget > BarChartSource.MaximumRawEntries)
            throw new ArgumentOutOfRangeException(nameof(geometryBudget));
        if (stackWorkBudget < 0 || stackWorkBudget > BarChartSource.MaximumStackWork)
            throw new ArgumentOutOfRangeException(nameof(stackWorkBudget));
        StackWorkBudget = stackWorkBudget;
        Layout = layout; CategoryStart = categoryStart; CategoryCount = categoryCount;
        GeometryBudget = geometryBudget; FixedDomain = fixedDomain;
    }
    public static BarChartOptions Default { get; } = new();
    public BarChartLayout Layout { get; }
    public int StackWorkBudget { get; }
    public int CategoryStart { get; }
    public int CategoryCount { get; }
    public int GeometryBudget { get; }
    public HistoryValueDomain? FixedDomain { get; }
}

public readonly record struct BarChartHit(long DatasetVersion, string SeriesId, long SeriesSourceVersion,
    BarSample Sample, BarCategory Category)
{
    public long Key => Sample.Key;
    public double? Value => Sample.Value;
    public string CategoryKey => Category.Key;
    public long? UtcTicks => Category.UtcTicks;
}

/// <summary>Finite normalized rectangle, with Y increasing downwards. Zero-valued bars have
/// zero height and remain individually keyboard-addressable. Finite precision can also round tiny
/// nonzero spans to zero height; constant fixed domains collapse intersecting intervals likewise.
/// No minimum pixel height is invented.</summary>
public readonly record struct BarChartBar(BarChartHit Hit, int CategoryIndex, int SeriesIndex,
    double Left, double Top, double Width, double Height);

public sealed class BarChartSnapshot
{
    internal BarChartSnapshot(BarChartSource source, BarChartOptions options, int start, int count,
        HistoryValueDomain? domain, ImmutableArray<BarChartBar> bars, ImmutableArray<BarChartHit> gaps,
        int requiredBarCount, BarChartStackStatus? stackStatus = null,
        ImmutableArray<BarChartContribution> contributions = default, int? requiredStackWork = null)
    {
        Source = source; Options = options; CategoryStart = start; CategoryCount = count;
        Domain = domain; Bars = bars; Gaps = gaps; RequiredBarCount = requiredBarCount;
        StackStatus = stackStatus; Contributions = contributions.IsDefault ? [] : contributions; RequiredStackWork = requiredStackWork;
        BudgetStatus = stackStatus is { } status ? status == BarChartStackStatus.Ready ? ChartBudgetStatus.Satisfied : ChartBudgetStatus.InsufficientBudget
            : requiredBarCount <= options.GeometryBudget ? ChartBudgetStatus.Satisfied : ChartBudgetStatus.InsufficientBudget;
        Ticks = DualAxisChartGeometry.CreateTicks(domain);
        BaselinePosition = domain.HasValue ? 1 - DualAxisChartGeometry.NormalizeY(0, domain.Value) : 0.5;
    }
    public BarChartSource Source { get; }
    public long DatasetVersion => Source.DatasetVersion;
    public BarChartOptions Options { get; }
    public int CategoryStart { get; }
    public int CategoryCount { get; }
    public HistoryValueDomain? Domain { get; }
    public ImmutableArray<HistoryAxisTick> Ticks { get; }
    /// <summary>Clamped screen location of mathematical zero; fixed domains may exclude zero.</summary>
    public double BaselinePosition { get; }
    public bool IsBaselineVisible => Domain is { Minimum: <= 0, Maximum: >= 0 };
    public ImmutableArray<BarChartBar> Bars { get; }
    public ImmutableArray<BarChartHit> Gaps { get; }
    public int RequiredBarCount { get; }
    public ChartBudgetStatus BudgetStatus { get; }
    /// <summary>Null for unchanged Grouped/Overlap queries. Specific failure reason for stack modes.</summary>
    public BarChartStackStatus? StackStatus { get; }
    public ImmutableArray<BarChartContribution> Contributions { get; }
    /// <summary>Null when even the bounded initial scan cannot be admitted.</summary>
    public int? RequiredStackWork { get; }
}

/// <summary>Original bounded category-bar data. Construction rejects excess input after at most
/// limit + 1 enumerator advances. Query scans at most 2048 raw entries; it never samples, merges,
/// or silently drops colliding bars. Duplicate category labels and repeated category samples are valid.</summary>
public sealed partial class BarChartSource
{
    public const int MaximumTextLength = 256;
    public const int MaximumCategories = 256;
    public const int MaximumSeries = 8;
    public const int MaximumRawEntries = 2048;
    private readonly ImmutableDictionary<string, int> _categoryIndices;
    public BarChartSource(IEnumerable<BarCategory> categories, IEnumerable<BarSeries> series, long datasetVersion = 0)
    {
        if (datasetVersion < 0) throw new ArgumentOutOfRangeException(nameof(datasetVersion));
        Categories = CopyBounded(categories, MaximumCategories, nameof(categories));
        Series = CopyBounded(series, MaximumSeries, nameof(series));
        var indices = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Categories.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(Categories[i]);
            if (!indices.TryAdd(Categories[i].Key, i)) throw new ArgumentException("Duplicate category key.", nameof(categories));
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var item in Series)
        {
            ArgumentNullException.ThrowIfNull(item);
            total += item.Samples.Length;
            if (total > MaximumRawEntries) throw new ArgumentException("Raw-entry admission limit exceeded.", nameof(series));
            if (!ids.Add(item.Id)) throw new ArgumentException("Duplicate series identifier.", nameof(series));
        }
        // Validate keys only after aggregate admission, bounding this scan by the aggregate cap.
        foreach (var item in Series)
            foreach (var sample in item.Samples)
                if (!indices.ContainsKey(sample.CategoryKey)) throw new ArgumentException("Unknown category key.", nameof(series));
        _categoryIndices = indices.ToImmutable();
        DatasetVersion = datasetVersion; RawEntryCount = total;
    }
    public ImmutableArray<BarCategory> Categories { get; }
    public ImmutableArray<BarSeries> Series { get; }
    public long DatasetVersion { get; }
    public int RawEntryCount { get; }

    public BarChartSnapshot Query(BarChartOptions? options = null)
    {
        options ??= BarChartOptions.Default;
        if (options.Layout is BarChartLayout.Stacked or BarChartLayout.FullStacked) return QueryStack(options);
        var start = Math.Min(options.CategoryStart, Categories.Length);
        var count = Math.Min(options.CategoryCount, Categories.Length - start);
        var entries = new List<(BarChartHit Hit, int Category, int Series)>();
        var gaps = ImmutableArray.CreateBuilder<BarChartHit>();
        var minimum = 0d; var maximum = 0d;
        for (var s = 0; s < Series.Length; s++)
            foreach (var sample in Series[s].Samples)
            {
                var c = _categoryIndices[sample.CategoryKey];
                if (c < start || c >= start + count) continue;
                var hit = new BarChartHit(DatasetVersion, Series[s].Id, Series[s].SourceVersion, sample, Categories[c]);
                if (sample.IsGap) { gaps.Add(hit); continue; }
                minimum = Math.Min(minimum, sample.Value!.Value);
                maximum = Math.Max(maximum, sample.Value.Value);
                entries.Add((hit, c, s));
            }
        HistoryValueDomain? domain = count == 0 ? null : options.FixedDomain ??
            (entries.Count == 0 ? null : new HistoryValueDomain(minimum, maximum));
        var bars = ImmutableArray.CreateBuilder<BarChartBar>();
        if (domain.HasValue)
        {
            // Stable ordering preserves same-series duplicate samples and defines overlap z-order.
            foreach (var entry in entries.OrderBy(item => item.Category).ThenBy(item => item.Series))
            {
                var value = entry.Hit.Value!.Value;
                var low = Math.Max(Math.Min(0, value), domain.Value.Minimum);
                var high = Math.Min(Math.Max(0, value), domain.Value.Maximum);
                if (low > high) continue;
                var bottom = 1 - DualAxisChartGeometry.NormalizeY(low, domain.Value);
                var top = 1 - DualAxisChartGeometry.NormalizeY(high, domain.Value);
                var groups = options.Layout == BarChartLayout.Grouped ? Series.Length : 1;
                var groupIndex = options.Layout == BarChartLayout.Grouped ? entry.Series : 0;
                var width = 0.8 / count / groups;
                var left = (entry.Category - start + 0.1) / count + groupIndex * width;
                bars.Add(new(entry.Hit, entry.Category, entry.Series, left, top, width, Math.Max(0, bottom - top)));
            }
        }
        var required = bars.Count;
        return new(this, options, start, count, domain,
            required <= options.GeometryBudget ? bars.ToImmutable() : [], gaps.ToImmutable(), required);
    }

    internal static void ValidateText(string text, string parameter, bool identity = false)
    {
        ArgumentNullException.ThrowIfNull(text, parameter);
        if (text.Length > MaximumTextLength) throw new ArgumentException("Text admission limit exceeded.", parameter);
        if (identity && string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Identity must not be blank.", parameter);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i]))
                    throw new ArgumentException("Text must contain valid UTF-16.", parameter);
            }
            else if (char.IsLowSurrogate(text[i])) throw new ArgumentException("Text must contain valid UTF-16.", parameter);
        }
    }

    internal static ImmutableArray<T> CopyBounded<T>(IEnumerable<T> source, int limit, string parameter)
    {
        ArgumentNullException.ThrowIfNull(source, parameter);
        var result = ImmutableArray.CreateBuilder<T>();
        using var iterator = source.GetEnumerator();
        while (iterator.MoveNext())
        {
            if (result.Count == limit) throw new ArgumentException($"Admission limit {limit} exceeded.", parameter);
            result.Add(iterator.Current);
        }
        return result.ToImmutable();
    }
}

public static class BarChartGeometry
{
    /// <summary>Normalized hit test, last drawn wins. Zero bars hit their exact baseline with zero
    /// tolerance; positive tolerance expands every rectangle, clipped to the [0,1] viewport.</summary>
    public static BarChartHit? HitTest(BarChartSnapshot snapshot, double x, double y, double tolerance = 0)
        => HitTest(snapshot, x, y, tolerance, tolerance);

    public static BarChartHit? HitTest(BarChartSnapshot snapshot, double x, double y,
        double xTolerance, double yTolerance)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(xTolerance) || xTolerance < 0 || xTolerance > 1) throw new ArgumentOutOfRangeException(nameof(xTolerance));
        if (!double.IsFinite(yTolerance) || yTolerance < 0 || yTolerance > 1) throw new ArgumentOutOfRangeException(nameof(yTolerance));
        if (x < 0 || x > 1 || y < 0 || y > 1) return null;
        for (var i = snapshot.Bars.Length - 1; i >= 0; i--)
        {
            var bar = snapshot.Bars[i];
            if (x >= bar.Left - xTolerance && x <= bar.Left + bar.Width + xTolerance &&
                y >= bar.Top - yTolerance && y <= bar.Top + bar.Height + yTolerance) return bar.Hit;
        }
        return null;
    }
    /// <summary>Stable visible geometry enumeration, including zero-height and occluded bars.
    /// Out-of-range indices return null; gaps and wholly clipped bars have no geometry.</summary>
    public static BarChartHit? GetHit(BarChartSnapshot snapshot, int index)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return index >= 0 && index < snapshot.Bars.Length ? snapshot.Bars[index].Hit : null;
    }
}
