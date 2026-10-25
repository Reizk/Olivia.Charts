using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>
/// An immutable dataset of up to eight stable-ID series mapped to one or two named unit axes.
/// There is at most one axis on each side. Callers must preserve series identity across dataset versions.
/// </summary>
public sealed class DualAxisHistorySource
{
    private readonly HashSet<string> _axisIds;
    private readonly HashSet<string> _seriesIds;

    public DualAxisHistorySource(IEnumerable<HistoryAxisDefinition> axes,
        IEnumerable<HistorySeriesDefinition> series, long datasetVersion = 0)
    {
        ArgumentNullException.ThrowIfNull(axes);
        ArgumentNullException.ThrowIfNull(series);
        if (datasetVersion < 0) throw new ArgumentOutOfRangeException(nameof(datasetVersion));
        // Bounded enumeration also rejects infinite enumerables without materializing them.
        Axes = axes.Take(3).ToImmutableArray();
        Series = series.Take(9).ToImmutableArray();
        if (Axes.Length is < 1 or > 2)
            throw new ArgumentException("One or two axes are required.", nameof(axes));
        if (Series.Length > 8) throw new ArgumentException("At most eight series are supported.", nameof(series));
        _axisIds = new HashSet<string>(StringComparer.Ordinal);
        _seriesIds = new HashSet<string>(StringComparer.Ordinal);
        var sides = new HashSet<HistoryAxisSide>();
        foreach (var axis in Axes)
        {
            ArgumentNullException.ThrowIfNull(axis);
            if (!_axisIds.Add(axis.Id)) throw new ArgumentException($"Duplicate axis identifier: {axis.Id}.", nameof(axes));
            if (!sides.Add(axis.Side)) throw new ArgumentException("Each side can have only one axis.", nameof(axes));
        }
        TimeWindow? extent = null;
        foreach (var definition in Series)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (!_seriesIds.Add(definition.Id))
                throw new ArgumentException($"Duplicate series identifier: {definition.Id}.", nameof(series));
            if (!_axisIds.Contains(definition.AxisId))
                throw new ArgumentException($"Unknown mapped axis: {definition.AxisId}.", nameof(series));
            Count += definition.Source.Count;
            if (definition.Source.Extent is not { } next) continue;
            extent = extent.HasValue
                ? new TimeWindow(Math.Min(extent.Value.StartTicks, next.StartTicks), Math.Max(extent.Value.EndTicks, next.EndTicks))
                : next;
        }
        Extent = extent;
        DatasetVersion = datasetVersion;
    }

    public long DatasetVersion { get; }
    public long SourceVersion => DatasetVersion;
    public long Count { get; }
    public TimeWindow? Extent { get; }
    public ImmutableArray<HistoryAxisDefinition> Axes { get; }
    public ImmutableArray<HistorySeriesDefinition> Series { get; }

    public DualAxisHistoryHit? FindNearest(string seriesId, long utcTicks, TimeWindow? window = null,
        bool includeGaps = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesId);
        var definition = Series.FirstOrDefault(item => item.Id == seriesId)
            ?? throw new ArgumentException($"Unknown series identifier: {seriesId}.", nameof(seriesId));
        var sample = definition.Source.FindNearest(utcTicks, window, includeGaps);
        return sample.HasValue ? new DualAxisHistoryHit(DatasetVersion, definition.Id,
            definition.Source.SourceVersion, sample.Value) : null;
    }

    /// <summary>
    /// Reserves every visible run's endpoints and extrema before allocating the remainder in proportion
    /// to unsampled finite population, using largest remainders and ordinal series-ID ties. Gaps do not
    /// consume the finite point budget. If reservations do not fit, every series has no drawable segments.
    /// </summary>
    public DualAxisChartSnapshot Query(TimeWindow window, int pointBudget, DualAxisChartOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (pointBudget < 0) throw new ArgumentOutOfRangeException(nameof(pointBudget));
        cancellationToken.ThrowIfCancellationRequested();
        options ??= DualAxisChartOptions.Default;
        options.Validate(_axisIds, _seriesIds);
        var probes = new HistoryChartSnapshot?[Series.Length];
        var visible = new bool[Series.Length];
        var finiteCounts = new int[Series.Length];
        var budgets = new int[Series.Length];
        var domains = new Dictionary<string, HistoryValueDomain>(StringComparer.Ordinal);
        long required = 0;
        long surplus = 0;
        for (var seriesIndex = 0; seriesIndex < Series.Length; seriesIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = Series[seriesIndex];
            visible[seriesIndex] = options.IsSeriesVisible(definition.Id) && options.IsAxisVisible(definition.AxisId);
            if (!visible[seriesIndex]) continue;
            var probe = definition.Source.Query(window, 0, cancellationToken);
            probes[seriesIndex] = probe;
            budgets[seriesIndex] = probe.RequiredPointBudget;
            required += probe.RequiredPointBudget;
            finiteCounts[seriesIndex] = probe.RawSampleCount - probe.Gaps.Sum(gap => gap.SampleCount);
            surplus += finiteCounts[seriesIndex] - probe.RequiredPointBudget;
            // Auto ranges derive from every visible raw finite value, not just the sampled display.
            if (options.GetAxisDomain(definition.AxisId).HasValue) continue;
            for (var index = probe.RawStartIndex; index < probe.RawEndIndexExclusive; index++)
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var sample = definition.Source.Samples[index];
                if (sample.IsGap) continue;
                var value = sample.Value!.Value;
                domains[definition.AxisId] = domains.TryGetValue(definition.AxisId, out var existing)
                    ? new HistoryValueDomain(Math.Min(existing.Minimum, value), Math.Max(existing.Maximum, value))
                    : new HistoryValueDomain(value, value);
            }
        }
        var status = required > pointBudget ? ChartBudgetStatus.InsufficientBudget : ChartBudgetStatus.Satisfied;
        if (status == ChartBudgetStatus.Satisfied && surplus > 0)
        {
            var remaining = (int)Math.Min(pointBudget - required, surplus);
            var remainders = new List<(int Index, long Remainder)>();
            var distributed = 0;
            for (var index = 0; index < Series.Length; index++)
            {
                var capacity = finiteCounts[index] - budgets[index];
                if (capacity <= 0) continue;
                var numerator = (long)remaining * capacity;
                var share = (int)(numerator / surplus);
                budgets[index] += share;
                distributed += share;
                remainders.Add((index, numerator % surplus));
            }
            foreach (var item in remainders.OrderByDescending(item => item.Remainder)
                         .ThenBy(item => Series[item.Index].Id, StringComparer.Ordinal).Take(remaining - distributed))
                budgets[item.Index]++;
        }
        var seriesSnapshots = ImmutableArray.CreateBuilder<DualAxisSeriesSnapshot>(Series.Length);
        for (var index = 0; index < Series.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var budget = status == ChartBudgetStatus.Satisfied ? budgets[index] : 0;
            var view = visible[index] && status == ChartBudgetStatus.Satisfied
                ? Series[index].Source.Query(window, budget, cancellationToken) : probes[index];
            seriesSnapshots.Add(new DualAxisSeriesSnapshot(Series[index], visible[index], budget, view));
        }
        var axisSnapshots = Axes.Select(axis => new DualAxisAxisSnapshot(axis, options.IsAxisVisible(axis.Id),
            options.GetAxisDomain(axis.Id) ?? (domains.TryGetValue(axis.Id, out var domain) ? domain : null))).ToImmutableArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new DualAxisChartSnapshot(this, window, pointBudget, required, status, options,
            axisSnapshots, seriesSnapshots.ToImmutable());
    }
}
