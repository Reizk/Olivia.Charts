using System.Collections.Immutable;
using Olivia.Core.Charts;

namespace Olivia.Core.Workspaces;

/// <summary>Immutable host-owned input for the shared Area/Scatter/Bar workspace.
/// X is an original signed integer coordinate, not a key or category position. Samples
/// must already be in strictly increasing X order, including null-valued gaps.</summary>
public sealed class MultiSeriesChartSource
{
    public const int MaximumSeries = BarChartSource.MaximumSeries;
    public const int MaximumSamples = BarChartSource.MaximumRawEntries;
    public const int MaximumCategories = BarChartSource.MaximumCategories;
    public const double MaximumMagnitude = 1_000_000;

    public MultiSeriesChartSource(IEnumerable<MultiSeriesData> series, long version = 1)
    {
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        var input = BarChartSource.CopyBounded(series, MaximumSeries, nameof(series));
        var copy = ImmutableArray.CreateBuilder<MultiSeriesData>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var categories = new SortedSet<int>();
        var count = 0;
        foreach (var item in input)
        {
            ArgumentNullException.ThrowIfNull(item);
            BarChartSource.ValidateText(item.Id, nameof(series), identity: true);
            BarChartSource.ValidateText(item.Label, nameof(series));
            if (!ids.Add(item.Id)) throw new ArgumentException("Duplicate series identifier.", nameof(series));
            var samples = BarChartSource.CopyBounded(item.Samples, MaximumSamples - count, nameof(series));
            var keys = new HashSet<long>();
            int? previous = null;
            foreach (var sample in samples)
            {
                ArgumentNullException.ThrowIfNull(sample);
                if (!keys.Add(sample.Key)) throw new ArgumentException("Duplicate sample key within series.", nameof(series));
                if (previous is { } x && sample.X <= x)
                    throw new ArgumentException("X must strictly increase within each series, including gaps.", nameof(series));
                if (sample.Value is { } value && (!double.IsFinite(value) || Math.Abs(value) > MaximumMagnitude))
                    throw new ArgumentOutOfRangeException(nameof(series), "Values must be null or finite within +/-1,000,000.");
                categories.Add(sample.X);
                if (categories.Count > MaximumCategories) throw new ArgumentException("Distinct X category limit exceeded.", nameof(series));
                previous = sample.X;
            }
            count += samples.Length;
            copy.Add(new(item.Id, item.Label, samples));
        }
        Series = copy.ToImmutable(); Coordinates = categories.ToImmutableArray();
        Version = version; SampleCount = count;
    }
    public ImmutableArray<MultiSeriesData> Series { get; }
    public ImmutableArray<int> Coordinates { get; }
    public long Version { get; }
    public int SampleCount { get; }
    // Empty sources have only a neutral display extent, never invented samples/categories.
    public int MinimumX => Coordinates.IsEmpty ? 0 : Coordinates[0];
    public int MaximumX => Coordinates.IsEmpty ? 1 : Coordinates[^1];
}
