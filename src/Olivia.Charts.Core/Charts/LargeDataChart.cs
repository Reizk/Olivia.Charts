using System.Collections.Immutable;
using System.Diagnostics;

namespace Olivia.Core.Charts;

public readonly record struct LargeDataView
{
    public LargeDataView(double xMinimum, double xMaximum, double yMinimum, double yMaximum)
    {
        if (!double.IsFinite(xMinimum) || !double.IsFinite(xMaximum) || xMaximum <= xMinimum
            || !double.IsFinite(yMinimum) || !double.IsFinite(yMaximum) || yMaximum <= yMinimum
            || !double.IsFinite(xMaximum - xMinimum) || !double.IsFinite(yMaximum - yMinimum))
            throw new ArgumentOutOfRangeException(nameof(xMaximum));
        XMinimum = xMinimum; XMaximum = xMaximum; YMinimum = yMinimum; YMaximum = yMaximum;
    }
    public double XMinimum { get; }
    public double XMaximum { get; }
    public double YMinimum { get; }
    public double YMaximum { get; }
    public double Width => XMaximum - XMinimum;
    public double Height => YMaximum - YMinimum;
    // Official AxisX/AxisYMaxZoomPercent=1,000,000 means 10,000x relative to full range.
    public const double MaximumZoom = 10_000;
    public LargeDataView Clamp(LargeDataView extent)
    {
        var width = Math.Clamp(Width, extent.Width / MaximumZoom, extent.Width);
        var height = Math.Clamp(Height, extent.Height / MaximumZoom, extent.Height);
        var x = Math.Clamp(XMinimum, extent.XMinimum, extent.XMaximum - width);
        var y = Math.Clamp(YMinimum, extent.YMinimum, extent.YMaximum - height);
        return new(x, x + width, y, y + height);
    }
    public LargeDataView Zoom(double factor, double xAnchor, double yAnchor, bool yOnly = false)
    {
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        xAnchor = Math.Clamp(xAnchor, 0, 1); yAnchor = Math.Clamp(yAnchor, 0, 1);
        var width = yOnly ? Width : Width / factor; var height = yOnly ? Height / factor : Height;
        var x = XMinimum + (Width - width) * xAnchor; var y = YMinimum + (Height - height) * yAnchor;
        return new(x, x + width, y, y + height);
    }
    /// <summary>Clamp the target span before applying the anchor, so zooming at a limit never pans.</summary>
    public LargeDataView ZoomWithin(LargeDataView extent, double factor, double xAnchor = .5, double yAnchor = .5, bool yOnly = false)
    {
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        var current = Clamp(extent);
        var span = yOnly ? current.Height : current.Width;
        var full = yOnly ? extent.Height : extent.Width;
        var target = Math.Clamp(span / factor, full / MaximumZoom, full);
        if (Math.Abs(target - span) <= span * 1e-12) return current;
        return current.Zoom(span / target, xAnchor, yAnchor, yOnly).Clamp(extent);
    }
    public LargeDataView Pan(double xFraction, double yFraction) => new(XMinimum + Width * xFraction,
        XMaximum + Width * xFraction, YMinimum + Height * yFraction, YMaximum + Height * yFraction);
}

public sealed record LargeDataSourceSnapshot(long Version, ImmutableArray<LargeDataSeries> Series)
{
    public long TotalPoints => Series.Sum(series => (long)series.Count);
    public long StorageBytes => Series.Sum(series => series.StorageBytes);
    public LargeDataView? Extent
    {
        get
        {
            var nonempty = Series.Where(series => series.Extrema.HasValue).ToArray();
            if (nonempty.Length == 0) return null;
            var min = nonempty.Min(series => (double)series.Extrema!.Value.Minimum.Value);
            var max = nonempty.Max(series => (double)series.Extrema!.Value.Maximum.Value);
            var pad = max == min ? Math.Max(1, Math.Abs(min) * .08) : (max - min) * .08;
            return new(0, Math.Max(1, nonempty.Max(series => series.Count) - 1), min - pad, max + pad);
        }
    }
}

public sealed record LargeDataFrame(long SourceVersion, LargeDataView View, int PixelWidth,
    ImmutableArray<LargeDataLine> Lines, double QueryMilliseconds)
{
    public long VisiblePoints => Lines.Sum(line => (long)line.VisibleCount);
    public int DisplayPoints => Lines.Sum(line => line.Points.Length);
    public static LargeDataFrame Query(LargeDataSourceSnapshot source, LargeDataView view, int pixelWidth,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var watch = Stopwatch.StartNew();
        var lines = ImmutableArray.CreateBuilder<LargeDataLine>(source.Series.Length);
        foreach (var series in source.Series)
        {
            token.ThrowIfCancellationRequested();
            lines.Add(series.Query(view.XMinimum, view.XMaximum, pixelWidth, token));
        }
        return new(source.Version, view, pixelWidth, lines.ToImmutable(), watch.Elapsed.TotalMilliseconds);
    }
}

public enum LargeDataAddStatus { Added, Busy, CapacityReached, Cancelled, Closed }
public sealed record LargeDataAddResult(LargeDataAddStatus Status, double GenerationMilliseconds);

/// <summary>
/// One in-flight producer, no add queue; total raw storage is explicitly bounded. Clear/Dispose invalidate
/// the generation epoch even if a borrowed generator ignores cancellation. Existing sources are immutable.
/// </summary>
public sealed class LargeDataSession : IDisposable
{
    public const long MaximumTotalPoints = 20_000_000;
    public const int MaximumSeries = 64;
    private readonly object _gate = new();
    private readonly Func<int, int, CancellationToken, LargeDataSeries> _generate;
    private LargeDataSourceSnapshot _snapshot = new(0, []);
    private CancellationTokenSource? _active;
    private Task _idle = Task.CompletedTask;
    private long _epoch;
    private int _ordinal;
    private bool _disposed;

    public LargeDataSession(Func<int, int, CancellationToken, LargeDataSeries>? generate = null) =>
        _generate = generate ?? LargeDataSeries.Generate;
    public LargeDataSourceSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public bool IsBusy { get { lock (_gate) return _active is not null; } }
    public Task WhenIdle { get { lock (_gate) return _idle; } }

    public Task<LargeDataAddResult> AddSeriesAsync(int count)
    {
        if (count < 1 || count > LargeDataSeries.MaximumSeriesPointCount) throw new ArgumentOutOfRangeException(nameof(count));
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(new LargeDataAddResult(LargeDataAddStatus.Closed, 0));
            if (_active is not null) return Task.FromResult(new LargeDataAddResult(LargeDataAddStatus.Busy, 0));
            if (_snapshot.TotalPoints + count > MaximumTotalPoints || _snapshot.Series.Length >= MaximumSeries)
                return Task.FromResult(new LargeDataAddResult(LargeDataAddStatus.CapacityReached, 0));
            var cancellation = _active = new CancellationTokenSource();
            var epoch = _epoch; var id = checked(++_ordinal);
            var task = Task.Run(() => GenerateAndCommit(count, id, epoch, cancellation));
            _idle = task;
            return task;
        }
    }

    private LargeDataAddResult GenerateAndCommit(int count, int id, long epoch, CancellationTokenSource cancellation)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var series = _generate(count, id, cancellation.Token);
            if (series.Count != count || series.Id != id) throw new InvalidOperationException("Generator returned a different series.");
            lock (_gate)
            {
                if (_disposed || epoch != _epoch || cancellation.IsCancellationRequested)
                    return new(LargeDataAddStatus.Cancelled, watch.Elapsed.TotalMilliseconds);
                _snapshot = new(_snapshot.Version + 1, _snapshot.Series.Add(series));
                return new(LargeDataAddStatus.Added, watch.Elapsed.TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { return new(LargeDataAddStatus.Cancelled, watch.Elapsed.TotalMilliseconds); }
        finally
        {
            lock (_gate) { _active = null; }
            cancellation.Dispose();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _epoch++; _active?.Cancel(); _snapshot = new(_snapshot.Version + 1, []);
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _epoch++; _active?.Cancel(); _snapshot = new(_snapshot.Version + 1, []);
        }
    }
}
