using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>
/// One immutable source version. Input is copied and sorted by (UTC ticks, stable key).
/// Keys are unique within a source; callers must not reuse retired keys across source versions.
/// </summary>
public sealed class HistorySeries
{
    private readonly ImmutableArray<HistorySample> _samples;
    private readonly ImmutableArray<HistorySample> _finiteSamples;

    public HistorySeries(IEnumerable<HistorySample> samples, long sourceVersion = 0)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sourceVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceVersion));
        var copy = samples.ToArray();
        var keys = new HashSet<long>();
        foreach (var sample in copy)
            if (!keys.Add(sample.Key))
                throw new ArgumentException($"Duplicate stable sample key: {sample.Key}.", nameof(samples));
        Array.Sort(copy, static (left, right) =>
        {
            var time = left.UtcTicks.CompareTo(right.UtcTicks);
            return time != 0 ? time : left.Key.CompareTo(right.Key);
        });
        _samples = ImmutableArray.CreateRange(copy);
        _finiteSamples = _samples.Where(sample => !sample.IsGap).ToImmutableArray();
        SourceVersion = sourceVersion;
        Extent = copy.Length == 0 ? null : new TimeWindow(copy[0].UtcTicks, copy[^1].UtcTicks + 1);
    }

    public long SourceVersion { get; }
    public int Count => _samples.Length;
    public TimeWindow? Extent { get; }
    public ImmutableArray<HistorySample> Samples => _samples;

    /// <summary>Returns only points inside the half-open window; no hidden boundary-neighbor insertion.</summary>
    public RawHistoryWindow ReadWindow(TimeWindow window, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var first = LowerBound(_samples, window.StartTicks);
        var end = LowerBound(_samples, window.EndTicks);
        var result = _samples.Slice(first, end - first);
        cancellationToken.ThrowIfCancellationRequested();
        return new RawHistoryWindow(SourceVersion, window, first, result);
    }

    /// <summary>
    /// Exact nearest original X, independent of display sampling. Distance ties choose earlier timestamp,
    /// then lower stable key. By default only finite points are eligible; includeGaps queries any raw point.
    /// </summary>
    public HistorySample? FindNearest(long utcTicks, TimeWindow? window = null, bool includeGaps = false)
    {
        if (utcTicks < 0 || utcTicks >= TimeWindow.MaximumExclusiveTicks)
            throw new ArgumentOutOfRangeException(nameof(utcTicks));
        var source = includeGaps ? _samples : _finiteSamples;
        var first = window.HasValue ? LowerBound(source, window.Value.StartTicks) : 0;
        var end = window.HasValue ? LowerBound(source, window.Value.EndTicks) : source.Length;
        if (first == end)
            return null;
        var right = Math.Clamp(LowerBound(source, utcTicks), first, end);
        if (right == first)
            return source[first];
        var left = LowerBound(source, source[right - 1].UtcTicks);
        if (right == end)
            return source[left];
        return utcTicks - source[left].UtcTicks <= source[right].UtcTicks - utcTicks
            ? source[left] : source[right];
    }

    /// <summary>
    /// Keeps each finite run's first, last, minimum and maximum original samples before adding evenly
    /// distributed representatives. The budget counts finite points, not explicit gap metadata.
    /// If mandatory points do not fit, returns InsufficientBudget and no segments.
    /// </summary>
    public HistoryChartSnapshot Query(TimeWindow window, int pointBudget,
        CancellationToken cancellationToken = default)
    {
        if (pointBudget < 0)
            throw new ArgumentOutOfRangeException(nameof(pointBudget));
        cancellationToken.ThrowIfCancellationRequested();
        var first = LowerBound(_samples, window.StartTicks);
        var end = LowerBound(_samples, window.EndTicks);
        var runs = new List<Run>();
        var gaps = ImmutableArray.CreateBuilder<HistoryGapRange>();
        var required = 0;
        var finiteCount = 0;
        var index = first;
        while (index < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = index;
            if (_samples[index].IsGap)
            {
                var reasons = HistoryGapReason.None;
                do
                {
                    CheckCancellation(index, cancellationToken);
                    var value = _samples[index].Value;
                    reasons |= !value.HasValue ? HistoryGapReason.Missing
                        : double.IsNaN(value.Value) ? HistoryGapReason.NotANumber : HistoryGapReason.Infinite;
                    index++;
                } while (index < end && _samples[index].IsGap);
                gaps.Add(new HistoryGapRange(start, index, _samples[start].UtcTicks,
                    _samples[index - 1].UtcTicks, reasons));
                continue;
            }
            var min = index;
            var max = index;
            do
            {
                CheckCancellation(index, cancellationToken);
                if (_samples[index].Value!.Value < _samples[min].Value!.Value) min = index;
                if (_samples[index].Value!.Value > _samples[max].Value!.Value) max = index;
                index++;
            } while (index < end && !_samples[index].IsGap);
            var run = new Run(start, index, [start, index - 1, min, max]);
            required += run.Selected.Count;
            finiteCount += index - start;
            runs.Add(run);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var immutableGaps = gaps.ToImmutable();
        if (required > pointBudget)
            return new HistoryChartSnapshot(SourceVersion, window, pointBudget, required, first, end,
                ChartBudgetStatus.InsufficientBudget, ImmutableArray<HistoryLineSegment>.Empty, immutableGaps);

        // Split the largest remaining index interval. Original indices give deterministic tie-breaking.
        var queue = new PriorityQueue<Interval, (int NegativeLength, int Start)>();
        foreach (var run in runs)
        {
            var ordered = run.Selected.Order().ToArray();
            for (var i = 1; i < ordered.Length; i++)
                EnqueueInterval(queue, run, ordered[i - 1], ordered[i]);
        }
        var count = required;
        var target = Math.Min(pointBudget, finiteCount);
        while (count < target && queue.TryDequeue(out var interval, out _))
        {
            CheckCancellation(count, cancellationToken);
            var middle = interval.Start + (interval.End - interval.Start) / 2;
            interval.Run.Selected.Add(middle);
            count++;
            EnqueueInterval(queue, interval.Run, interval.Start, middle);
            EnqueueInterval(queue, interval.Run, middle, interval.End);
        }
        var segments = ImmutableArray.CreateBuilder<HistoryLineSegment>(runs.Count);
        foreach (var run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var points = run.Selected.Order().Select(i => new HistoryRepresentativePoint(_samples[i], i)).ToImmutableArray();
            segments.Add(new HistoryLineSegment(run.Start, run.End, points));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new HistoryChartSnapshot(SourceVersion, window, pointBudget, required, first, end,
            ChartBudgetStatus.Satisfied, segments.ToImmutable(), immutableGaps);
    }

    private static int LowerBound(ImmutableArray<HistorySample> samples, long ticks)
    {
        var low = 0;
        var high = samples.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (samples[middle].UtcTicks < ticks) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static void CheckCancellation(int index, CancellationToken token)
    {
        if ((index & 255) == 0) token.ThrowIfCancellationRequested();
    }

    private static void EnqueueInterval(PriorityQueue<Interval, (int, int)> queue, Run run, int start, int end)
    {
        if (end - start > 1)
            queue.Enqueue(new Interval(run, start, end), (-(end - start), start));
    }

    private sealed class Run(int start, int end, IEnumerable<int> selected)
    {
        public int Start { get; } = start;
        public int End { get; } = end;
        public HashSet<int> Selected { get; } = new(selected);
    }

    private readonly record struct Interval(Run Run, int Start, int End);
}
