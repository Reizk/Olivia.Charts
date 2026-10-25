using System.Collections.Immutable;

namespace Olivia.Core.Charts;

public readonly record struct LargeDataPoint(int Index, float Value);
public readonly record struct LargeDataExtrema(LargeDataPoint Minimum, LargeDataPoint Maximum);
public sealed record LargeDataLine(int SeriesId, int SourceCount, int VisibleCount, ImmutableArray<LargeDataPoint> Points);

/// <summary>
/// Immutable dense numeric LineSeries: the original integer argument is its index, Y is binary32
/// exactly as in the official LargeDataGenerator. No per-point objects, sorting, or raw-sized query copies.
/// Every 64-point block stores original min/max indices; a query only scans partial block boundaries.
/// This source deliberately accepts finite values only, not missing/gap samples (use HistorySeries for those).
/// </summary>
public sealed class LargeDataSeries
{
    public const int InitialPointCount = 2_500_000;
    public const int MaximumSeriesPointCount = 2_500_000;
    private const int BlockSize = 64;
    private readonly float[] _values;
    private readonly int[] _minima, _maxima;

    private LargeDataSeries(int id, float[] ownedValues, CancellationToken token)
    {
        if (id < 1) throw new ArgumentOutOfRangeException(nameof(id));
        Id = id; _values = ownedValues;
        _minima = new int[(Count + BlockSize - 1) / BlockSize];
        _maxima = new int[_minima.Length];
        for (var block = 0; block < _minima.Length; block++)
        {
            token.ThrowIfCancellationRequested();
            var start = block * BlockSize; var end = Math.Min(start + BlockSize, Count);
            var min = start; var max = start;
            for (var index = start; index < end; index++)
            {
                if (!float.IsFinite(_values[index])) throw new ArgumentException("Large data lines require finite values.");
                if (_values[index] < _values[min]) min = index;
                if (_values[index] > _values[max]) max = index;
            }
            _minima[block] = min; _maxima[block] = max;
        }
        if (Count > 0) Extrema = FindExtrema(0, Count, token);
    }

    public int Id { get; }
    public string Name => $"Series {Id}";
    public int Count => _values.Length;
    public bool AllowResample => true;
    /// <summary>Array payload only, not process memory or CLR object overhead.</summary>
    public long StorageBytes => (long)Count * sizeof(float) + (long)_minima.Length * sizeof(int) * 2;
    public LargeDataExtrema? Extrema { get; }
    public float GetValue(int index) => _values[index];

    public static LargeDataSeries FromValues(int id, ReadOnlySpan<float> values, CancellationToken token = default)
    {
        if (values.Length > MaximumSeriesPointCount) throw new ArgumentOutOfRangeException(nameof(values));
        token.ThrowIfCancellationRequested();
        return new(id, values.ToArray(), token);
    }

    public static LargeDataSeries Generate(int pointCount, int seriesId, CancellationToken token = default)
    {
        if (pointCount < 1 || pointCount > MaximumSeriesPointCount) throw new ArgumentOutOfRangeException(nameof(pointCount));
        if (seriesId < 1) throw new ArgumentOutOfRangeException(nameof(seriesId));
        token.ThrowIfCancellationRequested();
        var values = new float[pointCount];
        // Preserve the official expression/order, including the remainder applied AFTER 0.1 * counter.
        var amplitude = 1000 * (1 + 0.1 * seriesId % 10);
        var phase = Math.PI / 6 * seriesId;
        for (var i = 0; i < pointCount; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            values[i] = (float)(Math.Sin(i) + Math.Sin(i / 100.0) + 30 * Math.Sin(i / 1000.0)
                + amplitude * Math.Sin(i / 100000.0 + phase));
        }
        return new(seriesId, values, token);
    }

    /// <summary>Exact extrema of [start,end), ties use the earliest original index.</summary>
    public LargeDataExtrema FindExtrema(int start, int end, CancellationToken token = default)
    {
        if (start < 0 || start >= end || end > Count) throw new ArgumentOutOfRangeException(nameof(start));
        token.ThrowIfCancellationRequested();
        var min = start; var max = start; var cursor = start;
        void Consider(int lo, int hi)
        {
            if (_values[lo] < _values[min] || (_values[lo] == _values[min] && lo < min)) min = lo;
            if (_values[hi] > _values[max] || (_values[hi] == _values[max] && hi < max)) max = hi;
        }
        while (cursor < end && cursor % BlockSize != 0) { Consider(cursor, cursor); cursor++; }
        while (cursor + BlockSize <= end)
        {
            if ((cursor & 4095) == 0) token.ThrowIfCancellationRequested();
            var block = cursor / BlockSize; Consider(_minima[block], _maxima[block]); cursor += BlockSize;
        }
        while (cursor < end) { Consider(cursor, cursor); cursor++; }
        return new(new(min, _values[min]), new(max, _values[max]));
    }

    /// <summary>
    /// At most 2*pixelWidth+2 original points: each screen-X bucket's min/max in original order,
    /// plus boundary neighbors so a line crossing the viewport is not lost. VisibleCount excludes
    /// those neighbors; no shorter series is extrapolated. This is rendering reduction, not data loss.
    /// </summary>
    public LargeDataLine Query(double xMinimum, double xMaximum, int pixelWidth, CancellationToken token = default)
    {
        if (!double.IsFinite(xMinimum) || !double.IsFinite(xMaximum) || xMaximum <= xMinimum || !double.IsFinite(xMaximum - xMinimum))
            throw new ArgumentOutOfRangeException(nameof(xMaximum));
        if (pixelWidth < 1 || pixelWidth > 4096) throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        token.ThrowIfCancellationRequested();
        if (Count == 0 || xMaximum < 0 || xMinimum > Count - 1)
            return new(Id, Count, 0, []);
        int CeilingIndex(double value) => (int)Math.Clamp(Math.Ceiling(value), 0, Count);
        var first = CeilingIndex(xMinimum);
        var end = (int)Math.Clamp(Math.Floor(xMaximum) + 1, 0, Count);
        var points = ImmutableArray.CreateBuilder<LargeDataPoint>(Math.Min(Count, pixelWidth * 2 + 2));
        void Add(int index)
        {
            if (points.Count == 0 || points[^1].Index != index) points.Add(new(index, _values[index]));
        }
        Add((int)Math.Clamp(Math.Floor(xMinimum), 0, Count - 1));
        for (var column = 0; column < pixelWidth; column++)
        {
            token.ThrowIfCancellationRequested();
            var start = CeilingIndex(xMinimum + (xMaximum - xMinimum) * column / pixelWidth);
            var stop = column == pixelWidth - 1 ? end
                : CeilingIndex(xMinimum + (xMaximum - xMinimum) * (column + 1) / pixelWidth);
            if (stop <= start) continue;
            var range = FindExtrema(start, stop, token);
            Add(Math.Min(range.Minimum.Index, range.Maximum.Index));
            Add(Math.Max(range.Minimum.Index, range.Maximum.Index));
        }
        Add((int)Math.Clamp(Math.Ceiling(xMaximum), 0, Count - 1));
        return new(Id, Count, Math.Max(0, end - first), points.ToImmutable());
    }
}
