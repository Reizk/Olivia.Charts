using System.Collections.Immutable;
using Olivia.Core.Charts;

namespace Olivia.Core.Workspaces;

/// <summary>
/// Queries already ordered visible acquisitions without constructing/sorting eight full HistorySeries.
/// Uses HistorySeries' original-key, finite-run endpoints/extrema and explicit-gap contract. Only the
/// small selected-index sets are sorted. Insufficient budgets do not draw misleading partial lines.
/// </summary>
internal static class RealtimeSensorSampler
{
    public static HistoryChartSnapshot Query(ImmutableArray<RealtimeSensorRow> rows, int channel,
        long sourceVersion, TimeWindow window, int budget, CancellationToken token)
    {
        var runs = new List<Run>();
        var gaps = ImmutableArray.CreateBuilder<HistoryGapRange>();
        var required = 0; var finite = 0; var index = 0;
        while (index < rows.Length)
        {
            token.ThrowIfCancellationRequested();
            var start = index;
            if (rows[index].Sample(channel).IsGap)
            {
                var reasons = HistoryGapReason.None;
                do
                {
                    var value = rows[index][channel];
                    reasons |= !value.HasValue ? HistoryGapReason.Missing
                        : double.IsNaN(value.Value) ? HistoryGapReason.NotANumber : HistoryGapReason.Infinite;
                    index++;
                } while (index < rows.Length && rows[index].Sample(channel).IsGap);
                gaps.Add(new(start, index, rows[start].UtcTicks, rows[index - 1].UtcTicks, reasons));
                continue;
            }
            var minimum = index; var maximum = index;
            do
            {
                if ((index & 255) == 0) token.ThrowIfCancellationRequested();
                if (rows[index][channel]!.Value < rows[minimum][channel]!.Value) minimum = index;
                if (rows[index][channel]!.Value > rows[maximum][channel]!.Value) maximum = index;
                index++;
            } while (index < rows.Length && !rows[index].Sample(channel).IsGap);
            var run = new Run(start, index, [start, index - 1, minimum, maximum]);
            required += run.Selected.Count; finite += index - start; runs.Add(run);
        }
        if (required > budget)
            return new(sourceVersion, window, budget, required, 0, rows.Length,
                ChartBudgetStatus.InsufficientBudget, [], gaps.ToImmutable());
        if (finite <= budget)
        {
            var complete = ImmutableArray.CreateBuilder<HistoryLineSegment>(runs.Count);
            foreach (var run in runs)
            {
                token.ThrowIfCancellationRequested();
                var points = ImmutableArray.CreateBuilder<HistoryRepresentativePoint>(run.End - run.Start);
                for (var i = run.Start; i < run.End; i++) points.Add(new(rows[i].Sample(channel), i));
                complete.Add(new(run.Start, run.End, points.MoveToImmutable()));
            }
            return new(sourceVersion, window, budget, required, 0, rows.Length, ChartBudgetStatus.Satisfied,
                complete.MoveToImmutable(), gaps.ToImmutable());
        }
        var queue = new PriorityQueue<Interval, (int NegativeLength, int Start)>();
        foreach (var run in runs)
        {
            var selected = run.Selected.Order().ToArray();
            for (var i = 1; i < selected.Length; i++) Enqueue(queue, run, selected[i - 1], selected[i]);
        }
        var count = required;
        while (count < Math.Min(budget, finite) && queue.TryDequeue(out var interval, out _))
        {
            if ((count & 255) == 0) token.ThrowIfCancellationRequested();
            var middle = interval.Start + (interval.End - interval.Start) / 2;
            interval.Run.Selected.Add(middle); count++;
            Enqueue(queue, interval.Run, interval.Start, middle); Enqueue(queue, interval.Run, middle, interval.End);
        }
        var segments = ImmutableArray.CreateBuilder<HistoryLineSegment>(runs.Count);
        foreach (var run in runs)
        {
            token.ThrowIfCancellationRequested();
            var points = run.Selected.Order().Select(i => new HistoryRepresentativePoint(rows[i].Sample(channel), i)).ToImmutableArray();
            segments.Add(new(run.Start, run.End, points));
        }
        return new(sourceVersion, window, budget, required, 0, rows.Length, ChartBudgetStatus.Satisfied,
            segments.MoveToImmutable(), gaps.ToImmutable());
    }

    private static void Enqueue(PriorityQueue<Interval, (int, int)> queue, Run run, int start, int end)
    {
        if (end - start > 1) queue.Enqueue(new(run, start, end), (-(end - start), start));
    }
    private sealed class Run(int start, int end, IEnumerable<int> selected)
    {
        public int Start { get; } = start;
        public int End { get; } = end;
        public HashSet<int> Selected { get; } = new(selected);
    }
    private readonly record struct Interval(Run Run, int Start, int End);
}
