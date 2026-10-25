using System.Collections.Immutable;
using System.Numerics;

namespace Olivia.Core.Charts;

/// <summary>Stable ordinal identity and display-only label; equal labels are allowed.</summary>
public sealed record FlowNode
{
    public FlowNode(string key, string label)
    {
        BarChartSource.ValidateText(key, nameof(key), identity: true);
        BarChartSource.ValidateText(label, nameof(label));
        Key = key; Label = label;
    }
    public string Key { get; }
    public string Label { get; }
}

/// <summary>One original directed edge. Null, nonfinite and negative weights are rejected.
/// Zero, including signed zero, retains its identity and participates in graph topology.</summary>
public sealed record FlowEdge
{
    public FlowEdge(string key, string sourceKey, string targetKey, double? weight)
    {
        BarChartSource.ValidateText(key, nameof(key), identity: true);
        BarChartSource.ValidateText(sourceKey, nameof(sourceKey), identity: true);
        BarChartSource.ValidateText(targetKey, nameof(targetKey), identity: true);
        if (!weight.HasValue || !double.IsFinite(weight.Value) || weight.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(weight), "A flow weight must be present, finite and nonnegative.");
        Key = key; SourceKey = sourceKey; TargetKey = targetKey; Weight = weight.Value;
    }
    public string Key { get; }
    public string SourceKey { get; }
    public string TargetKey { get; }
    public double Weight { get; }
}

/// <summary>An exact nonnegative dyadic sum: Units * 2^-1074. The integer never loses tiny
/// addends. IsOverflow compares the exact sum with the largest finite double, independently
/// of RoundedValue (which rounds to nearest, ties to even, and can be positive infinity).</summary>
public readonly record struct FlowWeightTotal
{
    internal FlowWeightTotal(BigInteger units) => Units = units;
    public const int UnitExponent = -1074;
    public BigInteger Units { get; }
    public bool IsOverflow => Units > MaximumFiniteUnits;
    /// <summary>Whether the rounded double preserves every exact dyadic unit.</summary>
    public bool IsExact
    {
        get { var rounded = RoundedValue; return double.IsFinite(rounded) && ExactUnits(rounded) == Units; }
    }
    public double RoundedValue
    {
        get
        {
            if (Units.IsZero) return 0;
            var shift = Math.Max(0, (int)Units.GetBitLength() - 53);
            var significand = Units >> shift;
            if (shift > 0)
            {
                var remainder = Units - (significand << shift);
                var half = BigInteger.One << (shift - 1);
                if (remainder > half || (remainder == half && !significand.IsEven)) significand++;
            }
            return Math.ScaleB((double)significand, shift + UnitExponent);
        }
    }
    internal static BigInteger ExactUnits(double weight)
    {
        var bits = (ulong)BitConverter.DoubleToInt64Bits(weight);
        var exponent = (int)((bits >> 52) & 0x7ff);
        var fraction = bits & 0x000f_ffff_ffff_ffffUL;
        return exponent == 0 ? new BigInteger(fraction) : new BigInteger(fraction | (1UL << 52)) << (exponent - 1);
    }
    private static readonly BigInteger MaximumFiniteUnits = ExactUnits(double.MaxValue);
}

public enum FlowNodeKind { Isolated, Source, Sink, Internal }
public enum FlowBalanceStatus { NotApplicable, Balanced, Imbalanced }
public enum FlowRibbonStatus { Visible, Zero, Collapsed }

public sealed class FlowChartOptions
{
    public FlowChartOptions(int geometryBudget = FlowChartSource.MaximumGeometry,
        int workBudget = FlowChartSource.MaximumWork)
    {
        if (geometryBudget < 0 || geometryBudget > FlowChartSource.MaximumGeometry)
            throw new ArgumentOutOfRangeException(nameof(geometryBudget));
        if (workBudget < 0 || workBudget > FlowChartSource.MaximumWork)
            throw new ArgumentOutOfRangeException(nameof(workBudget));
        GeometryBudget = geometryBudget; WorkBudget = workBudget;
    }
    public static FlowChartOptions Default { get; } = new();
    public int GeometryBudget { get; }
    /// <summary>Conservative query visit allowance: 5 per node and 2 per edge. Source admission
    /// and hit testing have separate fixed caps; this is not an elapsed-time guarantee.</summary>
    public int WorkBudget { get; }
}

public readonly record struct FlowNodeHit(long DatasetVersion, FlowNode Node)
{
    public string Key => Node.Key;
    public string Label => Node.Label;
}
public readonly record struct FlowEdgeHit(long DatasetVersion, FlowEdge Edge)
{
    public string Key => Edge.Key;
    public string SourceKey => Edge.SourceKey;
    public string TargetKey => Edge.TargetKey;
    public double Weight => Edge.Weight;
}
public readonly record struct FlowChartHit(FlowNodeHit? NodeHit, FlowEdgeHit? EdgeHit)
{
    public bool IsNode => NodeHit.HasValue;
    public string Key => NodeHit?.Key ?? EdgeHit?.Key ?? string.Empty;
    public long DatasetVersion => NodeHit?.DatasetVersion ?? EdgeHit?.DatasetVersion ?? 0;
}

/// <summary>Finite normalized node rectangle. Kind is structural, including zero edges.
/// Only internal nodes receive a conservation verdict, from exact dyadic equality.</summary>
public readonly record struct FlowChartNode(FlowNodeHit Hit, int NodeIndex, int Layer,
    double Left, double Top, double Right, double Bottom, FlowNodeKind Kind,
    FlowBalanceStatus BalanceStatus, FlowWeightTotal Incoming, FlowWeightTotal Outgoing)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

/// <summary>A straight sloped strip, with one common non-tapering vertical Thickness at both
/// ends. Coordinates use the fixed 2^-48 grid; positive weights below this geometry precision
/// are explicitly Collapsed, with zero thickness. Zero and collapsed edges keep their raw hit.</summary>
public readonly record struct FlowChartRibbon(FlowEdgeHit Hit, int EdgeIndex, int SourceNodeIndex,
    int TargetNodeIndex, double StartX, double EndX, double SourceTop, double TargetTop,
    double Thickness, FlowRibbonStatus Status)
{
    public double SourceBottom => SourceTop + Thickness;
    public double TargetBottom => TargetTop + Thickness;
}

public sealed class FlowChartSnapshot
{
    internal FlowChartSnapshot(FlowChartSource source, FlowChartOptions options,
        ImmutableArray<FlowChartNode> nodes, ImmutableArray<FlowChartRibbon> ribbons,
        FlowWeightTotal scale, ChartBudgetStatus budgetStatus)
    {
        Source = source; Options = options; Nodes = nodes; Ribbons = ribbons;
        Scale = scale; BudgetStatus = budgetStatus;
    }
    public FlowChartSource Source { get; }
    public long DatasetVersion => Source.DatasetVersion;
    public FlowChartOptions Options { get; }
    public ImmutableArray<FlowChartNode> Nodes { get; }
    public ImmutableArray<FlowChartRibbon> Ribbons { get; }
    /// <summary>Largest exact layer sum of node capacities max(incoming,outgoing).
    /// Zero on a refused frame. Every ribbon uses this one global denominator.</summary>
    public FlowWeightTotal Scale { get; }
    public int LayerCount => Source.LayerCount;
    public int RequiredGeometryCount => Source.Nodes.Length + Source.Edges.Length;
    public int RequiredWorkCount => Source.Nodes.Length * 5 + Source.Edges.Length * 2;
    public ChartBudgetStatus BudgetStatus { get; }
    public bool IsEmpty => Source.Nodes.IsEmpty;
}

/// <summary>Immutable bounded weighted DAG: at most 64 nodes, 256 edges and 256 UTF-16 code
/// units per key/label. Rejects duplicate ordinal keys, repeated directed endpoint pairs,
/// unknown endpoints, self edges and cycles, including all-zero cycles. Input enumerators
/// advance at most cap+1 times and are disposed. Nodes and edges preserve caller order.
/// Source admission uses at most N^2 selection visits, 2E edge visits and bounded linear
/// node passes. Exact totals use at most 2106 bits; query ratio numerators use at most 2156
/// bits. No merging, balancing adjustment or sampling occurs.</summary>
public sealed class FlowChartSource
{
    public const int MaximumNodes = 64;
    public const int MaximumEdges = 256;
    public const int MaximumGeometry = MaximumNodes + MaximumEdges;
    public const int MaximumWork = MaximumNodes * 5 + MaximumEdges * 2;
    public const int MaximumTextLength = 256;
    private readonly ImmutableArray<NodeState> _states;
    private readonly ImmutableArray<int> _edgeSources;
    private readonly ImmutableArray<int> _edgeTargets;
    private readonly ImmutableArray<BigInteger> _edgeUnits;

    public FlowChartSource(IEnumerable<FlowNode> nodes, IEnumerable<FlowEdge> edges, long datasetVersion = 0)
    {
        if (datasetVersion < 0) throw new ArgumentOutOfRangeException(nameof(datasetVersion));
        Nodes = BarChartSource.CopyBounded(nodes, MaximumNodes, nameof(nodes));
        Edges = BarChartSource.CopyBounded(edges, MaximumEdges, nameof(edges));
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Nodes.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(Nodes[i]);
            if (!indices.TryAdd(Nodes[i].Key, i)) throw new ArgumentException("Duplicate node key.", nameof(nodes));
        }
        var outgoing = Enumerable.Range(0, Nodes.Length).Select(_ => new List<int>()).ToArray();
        var indegree = new int[Nodes.Length]; var outdegree = new int[Nodes.Length];
        var incomingUnits = new BigInteger[Nodes.Length]; var outgoingUnits = new BigInteger[Nodes.Length];
        var keys = new HashSet<string>(StringComparer.Ordinal); var pairs = new HashSet<(int, int)>();
        var sources = ImmutableArray.CreateBuilder<int>(Edges.Length);
        var targets = ImmutableArray.CreateBuilder<int>(Edges.Length);
        var edgeUnits = ImmutableArray.CreateBuilder<BigInteger>(Edges.Length);
        foreach (var edge in Edges)
        {
            ArgumentNullException.ThrowIfNull(edge);
            if (!keys.Add(edge.Key)) throw new ArgumentException("Duplicate edge key.", nameof(edges));
            if (!indices.TryGetValue(edge.SourceKey, out var source) || !indices.TryGetValue(edge.TargetKey, out var target))
                throw new ArgumentException("Unknown edge endpoint.", nameof(edges));
            if (source == target) throw new ArgumentException("Self edges are not supported.", nameof(edges));
            if (!pairs.Add((source, target))) throw new ArgumentException("Duplicate directed endpoint pair.", nameof(edges));
            var units = FlowWeightTotal.ExactUnits(edge.Weight);
            sources.Add(source); targets.Add(target); edgeUnits.Add(units);
            outgoing[source].Add(target); indegree[target]++; outdegree[source]++;
            incomingUnits[target] += units; outgoingUnits[source] += units;
        }
        var remaining = (int[])indegree.Clone(); var consumed = new bool[Nodes.Length]; var layers = new int[Nodes.Length];
        for (var visited = 0; visited < Nodes.Length; visited++)
        {
            var next = -1;
            for (var candidate = 0; candidate < Nodes.Length; candidate++)
                if (!consumed[candidate] && remaining[candidate] == 0) { next = candidate; break; }
            if (next < 0) throw new ArgumentException("Directed cycles are not supported, including zero-weight cycles.", nameof(edges));
            consumed[next] = true;
            foreach (var target in outgoing[next])
            {
                layers[target] = Math.Max(layers[target], layers[next] + 1);
                remaining[target]--;
            }
        }
        var states = ImmutableArray.CreateBuilder<NodeState>(Nodes.Length);
        for (var i = 0; i < Nodes.Length; i++)
        {
            var kind = indegree[i] == 0 ? (outdegree[i] == 0 ? FlowNodeKind.Isolated : FlowNodeKind.Source)
                : outdegree[i] == 0 ? FlowNodeKind.Sink : FlowNodeKind.Internal;
            var balance = kind != FlowNodeKind.Internal ? FlowBalanceStatus.NotApplicable
                : incomingUnits[i] == outgoingUnits[i] ? FlowBalanceStatus.Balanced : FlowBalanceStatus.Imbalanced;
            states.Add(new(layers[i], kind, balance, new(incomingUnits[i]), new(outgoingUnits[i])));
        }
        _states = states.MoveToImmutable(); _edgeSources = sources.MoveToImmutable();
        _edgeTargets = targets.MoveToImmutable(); _edgeUnits = edgeUnits.MoveToImmutable();
        LayerCount = Nodes.IsEmpty ? 0 : layers.Max() + 1; DatasetVersion = datasetVersion;
    }
    public ImmutableArray<FlowNode> Nodes { get; }
    public ImmutableArray<FlowEdge> Edges { get; }
    public long DatasetVersion { get; }
    public int LayerCount { get; }

    public FlowChartSnapshot Query(FlowChartOptions? options = null)
    {
        options ??= FlowChartOptions.Default;
        // Refusal precedes geometry/scale work and never exposes partial geometry or hits.
        if (Nodes.Length + Edges.Length > options.GeometryBudget || Nodes.Length * 5 + Edges.Length * 2 > options.WorkBudget)
            return new(this, options, [], [], default, ChartBudgetStatus.InsufficientBudget);
        if (Nodes.IsEmpty) return new(this, options, [], [], default, ChartBudgetStatus.Satisfied);
        var layerUnits = new BigInteger[LayerCount]; var layerCounts = new int[LayerCount];
        foreach (var state in _states)
        {
            layerUnits[state.Layer] += BigInteger.Max(state.Incoming.Units, state.Outgoing.Units);
            layerCounts[state.Layer]++;
        }
        var scale = BigInteger.Zero; var maximumPopulation = 0;
        for (var i = 0; i < LayerCount; i++)
        {
            scale = BigInteger.Max(scale, layerUnits[i]); maximumPopulation = Math.Max(maximumPopulation, layerCounts[i]);
        }
        var reserve = FlowChartGeometry.CoordinateUnits / (10 * maximumPopulation);
        var gap = FlowChartGeometry.CoordinateUnits / (10 * (maximumPopulation + 1));
        var pitch = FlowChartGeometry.CoordinateUnits / LayerCount; var nodeWidth = pitch / 5;
        var tops = Enumerable.Repeat(gap, LayerCount).ToArray(); var portTops = new long[Nodes.Length];
        var geometry = ImmutableArray.CreateBuilder<FlowChartNode>(Nodes.Length);
        for (var index = 0; index < Nodes.Length; index++)
        {
            var state = _states[index];
            var flowHeight = WeightTicks(BigInteger.Max(state.Incoming.Units, state.Outgoing.Units), scale);
            var left = (pitch - nodeWidth) / 2 + state.Layer * pitch;
            var top = tops[state.Layer]; var bottom = top + flowHeight + reserve;
            portTops[index] = top + reserve / 2; tops[state.Layer] = bottom + gap;
            geometry.Add(new(new(DatasetVersion, Nodes[index]), index, state.Layer,
                Coordinate(left), Coordinate(top), Coordinate(left + nodeWidth), Coordinate(bottom),
                state.Kind, state.Balance, state.Incoming, state.Outgoing));
        }
        var nodes = geometry.MoveToImmutable();
        var incomingCursors = new long[Nodes.Length]; var outgoingCursors = new long[Nodes.Length];
        var ribbons = ImmutableArray.CreateBuilder<FlowChartRibbon>(Edges.Length);
        for (var index = 0; index < Edges.Length; index++)
        {
            var source = _edgeSources[index]; var target = _edgeTargets[index];
            var thickness = WeightTicks(_edgeUnits[index], scale);
            var status = Edges[index].Weight == 0 ? FlowRibbonStatus.Zero
                : thickness == 0 ? FlowRibbonStatus.Collapsed : FlowRibbonStatus.Visible;
            ribbons.Add(new(new(DatasetVersion, Edges[index]), index, source, target,
                nodes[source].Right, nodes[target].Left,
                Coordinate(portTops[source] + outgoingCursors[source]), Coordinate(portTops[target] + incomingCursors[target]),
                Coordinate(thickness), status));
            outgoingCursors[source] += thickness; incomingCursors[target] += thickness;
        }
        return new(this, options, nodes, ribbons.MoveToImmutable(), new(scale), ChartBudgetStatus.Satisfied);
    }
    private static long WeightTicks(BigInteger units, BigInteger scale)
        => scale.IsZero ? 0 : (long)(units * (4 * FlowChartGeometry.CoordinateUnits) / (5 * scale));
    private static double Coordinate(long ticks) => ticks * FlowChartGeometry.CoordinateQuantum;
    private readonly record struct NodeState(int Layer, FlowNodeKind Kind, FlowBalanceStatus Balance,
        FlowWeightTotal Incoming, FlowWeightTotal Outgoing);
}

public static class FlowChartGeometry
{
    public const long CoordinateUnits = 1L << 48;
    public const double CoordinateQuantum = 1d / CoordinateUnits;
    /// <summary>Half-open unit viewport. Node rectangles take precedence; overlapping strips
    /// use reverse edge order (last painted wins). Exact dyadic half-plane comparisons match
    /// the straight strip: x in [StartX,EndX), y in [top,top+Thickness). They do not round a
    /// just-outside point onto the sloped boundary. Zero/collapsed strips cannot hit.
    /// At most 64 nodes and 256 ribbons are examined; no tolerance invents hit area.</summary>
    public static FlowChartHit? HitTest(FlowChartSnapshot snapshot, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (x < 0 || x >= 1 || y < 0 || y >= 1) return null;
        foreach (var node in snapshot.Nodes)
            if (x >= node.Left && x < node.Right && y >= node.Top && y < node.Bottom)
                return new(node.Hit, null);
        for (var i = snapshot.Ribbons.Length - 1; i >= 0; i--)
        {
            var ribbon = snapshot.Ribbons[i];
            if (ribbon.Status != FlowRibbonStatus.Visible || x < ribbon.StartX || x >= ribbon.EndX) continue;
            if (ContainsRibbon(x, y, ribbon.StartX, ribbon.EndX, ribbon.SourceTop, ribbon.TargetTop,
                ribbon.SourceBottom, ribbon.TargetBottom)) return new(null, ribbon.Hit);
        }
        return null;
    }
    /// <summary>Exact containment for one finite straight strip in either normalized or
    /// projected coordinates, including signed coordinates. The four corners are (startX,
    /// sourceTop), (endX,targetTop), (endX,targetBottom), (startX,sourceBottom). Lower and
    /// upper sides may have different slopes after projection rounding. Left/top boundaries
    /// are included; right/bottom excluded. Reversed endpoints or top/bottom order are
    /// rejected; a zero-width or zero-area strip misses. This is not a general polygon API.
    /// The fixed eight-double input uses bounded exact dyadic cross-products (at most 4200
    /// bits), without division, geometric tolerance or backend-specific polygon filling.</summary>
    public static bool ContainsRibbon(double x, double y, double startX, double endX,
        double sourceTop, double targetTop, double sourceBottom, double targetBottom)
    {
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (!double.IsFinite(startX)) throw new ArgumentOutOfRangeException(nameof(startX));
        if (!double.IsFinite(endX)) throw new ArgumentOutOfRangeException(nameof(endX));
        if (!double.IsFinite(sourceTop)) throw new ArgumentOutOfRangeException(nameof(sourceTop));
        if (!double.IsFinite(targetTop)) throw new ArgumentOutOfRangeException(nameof(targetTop));
        if (!double.IsFinite(sourceBottom)) throw new ArgumentOutOfRangeException(nameof(sourceBottom));
        if (!double.IsFinite(targetBottom)) throw new ArgumentOutOfRangeException(nameof(targetBottom));
        if (endX < startX) throw new ArgumentException("Strip endpoints must be ordered left to right.", nameof(endX));
        if (sourceBottom < sourceTop || targetBottom < targetTop)
            throw new ArgumentException("Strip bottom must not be above its top.", nameof(sourceBottom));
        if (x < startX || x >= endX) return false;
        var start = SignedUnits(startX); var run = SignedUnits(endX) - start;
        var position = SignedUnits(x) - start; var vertical = SignedUnits(y);
        var top = SignedUnits(sourceTop); var bottom = SignedUnits(sourceBottom);
        return (vertical - top) * run >= (SignedUnits(targetTop) - top) * position
            && (vertical - bottom) * run < (SignedUnits(targetBottom) - bottom) * position;
    }
    private static BigInteger SignedUnits(double value)
        => value < 0 ? -FlowWeightTotal.ExactUnits(-value) : FlowWeightTotal.ExactUnits(value);

    /// <summary>Stable keyboard enumeration: original nodes followed by original edges,
    /// including zero and collapsed edges. Refused frames expose no selection.</summary>
    public static FlowChartHit? GetHit(FlowChartSnapshot snapshot, int index)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (index < 0) return null;
        if (index < snapshot.Nodes.Length) return new(snapshot.Nodes[index].Hit, null);
        index -= snapshot.Nodes.Length;
        return index < snapshot.Ribbons.Length ? new(null, snapshot.Ribbons[index].Hit) : null;
    }
}
