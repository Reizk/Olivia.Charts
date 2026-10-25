namespace Olivia.Core.Charts;

/// <summary>Single-owner, synchronous finite synthetic input. Pause freezes presentation only;
/// input continues with exact oldest-first retention. There is no producer, timer or queue.</summary>
public sealed class ScatterLiveSession
{
    public const int DefaultCapacity = 64;
    public const int MaximumCapacity = ScatterChartSource.MaximumRawEntries;
    public const int MaximumTotalSamples = 4096;
    public const int MaximumBatchSize = 64;

    public ScatterLiveSession(int capacity = DefaultCapacity, int totalSamples = 256)
    {
        if (capacity < 1 || capacity > MaximumCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (totalSamples < 0 || totalSamples > MaximumTotalSamples) throw new ArgumentOutOfRangeException(nameof(totalSamples));
        Capacity = capacity; TotalSamples = totalSamples;
        Source = new([new ScatterSeries("synthetic-xy", "Synthetic XY", [])]);
        ShownSource = Source;
    }

    public int Capacity { get; }
    public int TotalSamples { get; }
    public int AcceptedCount { get; private set; }
    public int EvictedCount => Math.Max(0, AcceptedCount - Capacity);
    public int RemainingCount => TotalSamples - AcceptedCount;
    public bool IsPaused { get; private set; }
    public ScatterChartSource Source { get; private set; }
    public ScatterChartSource ShownSource { get; private set; }

    public int Append(int count = 8)
    {
        if (count < 1 || count > MaximumBatchSize) throw new ArgumentOutOfRangeException(nameof(count));
        var accepted = Math.Min(count, RemainingCount);
        if (accepted == 0) return 0;
        var next = AcceptedCount + accepted;
        var first = Math.Max(1, next - Capacity + 1);
        var samples = new ScatterSample[next - first + 1];
        for (var i = 0; i < samples.Length; i++) samples[i] = Generate(first + i);
        var source = new ScatterChartSource([new ScatterSeries("synthetic-xy", "Synthetic XY", samples, next)], next);
        AcceptedCount = next; Source = source;
        if (!IsPaused) ShownSource = source;
        return accepted;
    }

    public void Pause() => IsPaused = true;
    public void Resume() { IsPaused = false; ShownSource = Source; }

    /// <summary>The caller supplies the settled shown snapshot, including its actual view options.
    /// A foreign/stale source is refused rather than exporting requested data as displayed data.</summary>
    public ScatterLiveFrame Freeze(ScatterChartSnapshot shownSnapshot)
    {
        ArgumentNullException.ThrowIfNull(shownSnapshot);
        if (!ReferenceEquals(shownSnapshot.Source, ShownSource))
            throw new InvalidOperationException("Only the current shown source can be frozen.");
        return new(shownSnapshot, Capacity, TotalSamples, AcceptedCount, EvictedCount, RemainingCount, IsPaused);
    }

    private static ScatterSample Generate(int sequence)
    {
        var r = (sequence - 1) % 16;
        double? x = r / 2; double? y = (r / 2) % 4;
        switch (r)
        {
            case 4: x = null; break;
            case 5: y = double.NaN; break;
            case 6: x = double.PositiveInfinity; break;
            case 7: y = double.NegativeInfinity; break;
            case 8: x = -0d; y = -0d; break;
        }
        return new(sequence, x, y);
    }
}

/// <summary>Immutable bounded shown frame plus acquisition accounting at capture time.
/// Shown dataset version can lag accepted count while presentation is paused.</summary>
public sealed class ScatterLiveFrame
{
    internal ScatterLiveFrame(ScatterChartSnapshot snapshot, int capacity, int totalSamples,
        int acceptedCount, int evictedCount, int remainingCount, bool isPaused)
    {
        SnapshotId = Guid.NewGuid().ToString("N"); Snapshot = snapshot; Capacity = capacity;
        TotalSamples = totalSamples; AcceptedCount = acceptedCount; EvictedCount = evictedCount;
        RemainingCount = remainingCount; IsPaused = isPaused;
    }
    public string SnapshotId { get; }
    public ScatterChartSnapshot Snapshot { get; }
    public ScatterChartSource Source => Snapshot.Source;
    public int Capacity { get; }
    public int TotalSamples { get; }
    public int AcceptedCount { get; }
    public int EvictedCount { get; }
    public int RemainingCount { get; }
    public bool IsPaused { get; }
}
