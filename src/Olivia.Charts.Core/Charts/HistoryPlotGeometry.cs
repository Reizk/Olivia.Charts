using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>Binary64 label bounds; rounded labels are not the logical normalization domain.</summary>
public readonly record struct HistoryPlotValueDomain(double Minimum, double Maximum);

/// <summary>Exact representative identity and normalized position: X rightwards, Y upwards.</summary>
public readonly record struct HistoryPlotNode(HistoryRepresentativePoint Point, HistoryChartPoint Position)
{
    public HistorySample Sample => Point.Sample;
    public int RawIndex => Point.RawIndex;
    public long Key => Point.Key;
    public long UtcTicks => Point.UtcTicks;
    public double Value => Point.Value;
}

public readonly record struct HistoryPlotEdge(HistoryPlotNode Start, HistoryPlotNode End);

/// <summary>Contiguous ranges into the frame arrays. Each segment has exactly one first-node marker.</summary>
public readonly record struct HistoryPlotSegmentGeometry(int RawStartIndex, int RawEndIndexExclusive,
    int FirstNodeIndex, int NodeCount, int FirstEdgeIndex, int EdgeCount);

/// <summary>
/// Immutable geometry consumed by the single-axis HistoryPlot. Data edges and first-node markers
/// exclude background grids and selection decoration. Narrow finite domains retain origin-relative
/// differences; other domains preserve the original single-axis range and clamping behavior.
/// </summary>
public sealed class HistoryPlotGeometry
{
    private readonly double _scale, _minimum, _maximum;
    private readonly double _origin, _span;
    private readonly bool _originRelative;

    private HistoryPlotGeometry(HistoryChartSnapshot snapshot)
    {
        SourceVersion = snapshot.SourceVersion;
        Window = snapshot.Window;
        Gaps = snapshot.Gaps;
        if (snapshot.PointCount == 0) return;

        var values = snapshot.Segments.SelectMany(segment => segment.Points).Select(point => point.Value).ToArray();
        var rawMin = values.Min(); var rawMax = values.Max();
        RawDomain = new(rawMin, rawMax);
        var span = rawMax - rawMin;
        var magnitude = Math.Max(Math.Abs(rawMin), Math.Abs(rawMax));
        var localPad = span * .08;
        // Restrict the precision path to narrow, same-sign finite domains. Their endpoint
        // subtraction is exact (Sterbenz); do not scale away their significant low bits.
        // Keep legacy constant, ordinary-range and extreme/clipped-domain behavior below.
        _originRelative = double.IsFinite(span) && span > 0 && span <= magnitude * 1e-8
            && localPad <= double.MaxValue - rawMax && localPad <= rawMin + double.MaxValue;
        if (_originRelative)
        {
            _origin = rawMin;
            _span = span;
            DisplayDomain = new(RelativeLabel(4), RelativeLabel(0));
        }
        else
        {
            // Preserve the original single-axis operation order, including constant expansion.
            _scale = Math.Max(1, magnitude);
            var min = rawMin / _scale; var max = rawMax / _scale;
            if (max == min) { min -= .1; max += .1; }
            var pad = (max - min) * 0.08;
            _minimum = Math.Max(-double.MaxValue / _scale, min - pad);
            _maximum = Math.Min(double.MaxValue / _scale, max + pad);
            // At extreme magnitudes multiplication can round to infinity, as in the original labels.
            // Keep that existing label behavior; normalized coordinates remain finite.
            DisplayDomain = new(_minimum * _scale, _maximum * _scale);
        }

        var nodes = ImmutableArray.CreateBuilder<HistoryPlotNode>(snapshot.PointCount);
        var edges = ImmutableArray.CreateBuilder<HistoryPlotEdge>();
        var markers = ImmutableArray.CreateBuilder<HistoryPlotNode>(snapshot.Segments.Length);
        var segments = ImmutableArray.CreateBuilder<HistoryPlotSegmentGeometry>(snapshot.Segments.Length);
        foreach (var segment in snapshot.Segments)
        {
            var firstNode = nodes.Count;
            var firstEdge = edges.Count;
            HistoryPlotNode? previous = null;
            foreach (var point in segment.Points)
            {
                var node = new HistoryPlotNode(point, Normalize(point.UtcTicks, point.Value));
                nodes.Add(node);
                if (previous is { } prior) edges.Add(new(prior, node));
                else markers.Add(node);
                previous = node;
            }
            segments.Add(new(segment.RawStartIndex, segment.RawEndIndexExclusive,
                firstNode, nodes.Count - firstNode, firstEdge, edges.Count - firstEdge));
        }
        Nodes = nodes.ToImmutable();
        DataEdges = edges.ToImmutable();
        Markers = markers.ToImmutable();
        Segments = segments.ToImmutable();
        var ticks = ImmutableArray.CreateBuilder<HistoryAxisTick>(5);
        for (var i = 0; i <= 4; i++)
            ticks.Add(new(_originRelative ? RelativeLabel(i)
                : (_maximum - (_maximum - _minimum) * i / 4) * _scale, 1 - i / 4d));
        Ticks = ticks.ToImmutable();
    }

    public long SourceVersion { get; }
    public TimeWindow Window { get; }
    public HistoryPlotValueDomain? RawDomain { get; }
    public HistoryPlotValueDomain? DisplayDomain { get; }
    /// <summary>Top-to-bottom label order; Position uses the same upward Y convention as nodes.</summary>
    public ImmutableArray<HistoryAxisTick> Ticks { get; } = [];
    public ImmutableArray<HistoryPlotNode> Nodes { get; } = [];
    public ImmutableArray<HistoryPlotEdge> DataEdges { get; } = [];
    public ImmutableArray<HistoryPlotNode> Markers { get; } = [];
    public ImmutableArray<HistoryPlotSegmentGeometry> Segments { get; } = [];
    public ImmutableArray<HistoryGapRange> Gaps { get; }

    public static HistoryPlotGeometry Create(HistoryChartSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(snapshot);
    }

    // Compute each logical 8%-padded tick locally, then add the origin once. In the
    // 1e12 +/- .01 fixture this gives nearest label values (<= .5 ULP); this is not
    // a general correctly-rounded-arithmetic guarantee for all binary64 domains.
    private double RelativeLabel(int topToBottomIndex) =>
        _origin + _span * ((108 - 29 * topToBottomIndex) / 100d);

    /// <summary>Shared by representative nodes and the original-value selection decoration.</summary>
    public HistoryChartPoint Normalize(long utcTicks, double value)
    {
        if (RawDomain is null) throw new InvalidOperationException("An empty frame has no value domain.");
        return new((utcTicks - Window.StartTicks) / (double)Math.Max(1, Window.DurationTicks),
            _originRelative ? ((value - _origin) / _span + .08) / 1.16
                : (value / _scale - _minimum) / (_maximum - _minimum));
    }
}
