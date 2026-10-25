using System.Collections.Immutable;
using System.Numerics;

namespace Olivia.Core.Charts;

/// <summary>One original forest record. Keys are ordinal identities and labels are display-only.
/// A null parent identifies a root. Weight is required, finite and nonnegative; a node which has
/// children must explicitly carry zero. Only leaf weights contribute to descendant totals.</summary>
public sealed record TreemapNode
{
    public TreemapNode(string key, string label, string? parentKey, double? weight)
    {
        BarChartSource.ValidateText(key, nameof(key), identity: true);
        BarChartSource.ValidateText(label, nameof(label));
        if (parentKey is not null) BarChartSource.ValidateText(parentKey, nameof(parentKey), identity: true);
        if (!weight.HasValue || !double.IsFinite(weight.Value) || weight.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(weight), "A treemap weight must be present, finite and nonnegative.");
        Key = key; Label = label; ParentKey = parentKey; Weight = weight.Value;
    }
    public string Key { get; }
    public string Label { get; }
    public string? ParentKey { get; }
    public double Weight { get; }
}

/// <summary>Exact sum of original leaf weights in units of 2^-1074. Overflow is determined
/// before rounding. Geometry never uses the rounded double as a denominator.</summary>
public readonly record struct TreemapWeightTotal
{
    internal TreemapWeightTotal(BigInteger units) => Units = units;
    public const int UnitExponent = -1074;
    public BigInteger Units { get; }
    public bool IsOverflow => new FlowWeightTotal(Units).IsOverflow;
    public bool IsExact => new FlowWeightTotal(Units).IsExact;
    public double RoundedValue => new FlowWeightTotal(Units).RoundedValue;
}

public enum TreemapRectangleStatus { Visible, Zero, Collapsed }

/// <summary>DisplayDepth is the number of visible levels below the focused container (or
/// virtual forest root). No padding or minimum area is inserted. Budget refusal is atomic.</summary>
public sealed class TreemapChartOptions
{
    public TreemapChartOptions(int displayDepth = 2, int geometryBudget = TreemapChartSource.MaximumNodes,
        int workBudget = TreemapChartSource.MaximumWork)
    {
        if (displayDepth < 1 || displayDepth > TreemapChartSource.MaximumDepth)
            throw new ArgumentOutOfRangeException(nameof(displayDepth));
        if (geometryBudget < 0 || geometryBudget > TreemapChartSource.MaximumNodes)
            throw new ArgumentOutOfRangeException(nameof(geometryBudget));
        if (workBudget < 0 || workBudget > TreemapChartSource.MaximumWork)
            throw new ArgumentOutOfRangeException(nameof(workBudget));
        DisplayDepth = displayDepth; GeometryBudget = geometryBudget; WorkBudget = workBudget;
    }
    public static TreemapChartOptions Default { get; } = new();
    public int DisplayDepth { get; }
    public int GeometryBudget { get; }
    /// <summary>Conservative query visit allowance: 4 per source node and 2 per displayed
    /// rectangle. Fixed admission and bounded hit testing are separate operations.</summary>
    public int WorkBudget { get; }
}

/// <summary>Original record, version, exact aggregate, and immutable half-open leaf range
/// [LeafStart, LeafStart+LeafCount) in Source.LeafIndices. Zero-weight leaves retain a range.</summary>
public readonly record struct TreemapChartHit(long DatasetVersion, TreemapNode Node,
    TreemapWeightTotal Aggregate, int LeafStart, int LeafCount)
{
    public string Key => Node.Key;
    public string Label => Node.Label;
    public string? ParentKey => Node.ParentKey;
    public double Weight => Node.Weight;
}

/// <summary>Normalized half-open rectangle on the fixed 2^-48 coordinate grid. Depth is
/// relative to the displayed forest, starting at zero. Parent/child rectangles nest; siblings
/// never overlap. Zero and precision-collapsed positive aggregates have no pointer area.</summary>
public readonly record struct TreemapChartRectangle(TreemapChartHit Hit, int NodeIndex, int Depth,
    double Left, double Top, double Right, double Bottom, TreemapRectangleStatus Status, bool IsLeaf)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

public sealed class TreemapChartSnapshot
{
    internal TreemapChartSnapshot(TreemapChartSource source, TreemapChartOptions options, string? focusKey,
        TreemapWeightTotal total, int requiredGeometryCount, ImmutableArray<TreemapChartRectangle> rectangles,
        ChartBudgetStatus budgetStatus)
    {
        Source = source; Options = options; FocusKey = focusKey; Total = total;
        RequiredGeometryCount = requiredGeometryCount; Rectangles = rectangles; BudgetStatus = budgetStatus;
    }
    public TreemapChartSource Source { get; }
    public long DatasetVersion => Source.DatasetVersion;
    public TreemapChartOptions Options { get; }
    public string? FocusKey { get; }
    public TreemapWeightTotal Total { get; }
    public ImmutableArray<TreemapChartRectangle> Rectangles { get; }
    public int RequiredGeometryCount { get; }
    public int RequiredWorkCount => Source.Nodes.Length * 4 + RequiredGeometryCount * 2;
    public ChartBudgetStatus BudgetStatus { get; }
    /// <summary>No records in the displayed range; an all-zero range is separately HasPositiveWeight=false.</summary>
    public bool IsEmpty => RequiredGeometryCount == 0;
    public bool HasPositiveWeight => !Total.Units.IsZero;
}

/// <summary>Original immutable complete forest of at most 256 nodes, 16 levels and 256 UTF-16
/// code units per text. Input enumerators advance at most 257 times and are disposed. Keys,
/// parent existence, self parents, cycles and depth are checked before publication. Roots and
/// siblings preserve caller order, even if children were supplied before parents. Admission and
/// queries have finite linear traversals; exact totals need at most 2106 bits and ratio products
/// at most 2154 bits. No padding, sorting by weight, inferred parents or internal own-weight
/// allocation occurs. Every internal raw weight must be zero; totals are exact leaf sums.</summary>
public sealed class TreemapChartSource
{
    public const int MaximumNodes = 256;
    public const int MaximumDepth = 16;
    public const int MaximumWork = MaximumNodes * 6;
    public const int MaximumTextLength = 256;
    private readonly Dictionary<string, int> _indices;
    private readonly ImmutableArray<ImmutableArray<int>> _children;
    private readonly ImmutableArray<NodeState> _states;
    private readonly ImmutableArray<int> _preorder;

    public TreemapChartSource(IEnumerable<TreemapNode> nodes, long datasetVersion = 0)
    {
        if (datasetVersion < 0) throw new ArgumentOutOfRangeException(nameof(datasetVersion));
        Nodes = BarChartSource.CopyBounded(nodes, MaximumNodes, nameof(nodes));
        _indices = new(StringComparer.Ordinal);
        for (var index = 0; index < Nodes.Length; index++)
        {
            ArgumentNullException.ThrowIfNull(Nodes[index]);
            if (!_indices.TryAdd(Nodes[index].Key, index)) throw new ArgumentException("Duplicate node key.", nameof(nodes));
        }
        var roots = ImmutableArray.CreateBuilder<int>();
        var children = Enumerable.Range(0, Nodes.Length).Select(_ => new List<int>()).ToArray();
        for (var index = 0; index < Nodes.Length; index++)
        {
            var parentKey = Nodes[index].ParentKey;
            if (parentKey is null) { roots.Add(index); continue; }
            if (!_indices.TryGetValue(parentKey, out var parent)) throw new ArgumentException("Unknown parent key.", nameof(nodes));
            if (parent == index) throw new ArgumentException("A node cannot parent itself.", nameof(nodes));
            children[parent].Add(index);
        }
        foreach (var index in Enumerable.Range(0, Nodes.Length))
            if (children[index].Count > 0 && Nodes[index].Weight != 0)
                throw new ArgumentException("Internal nodes must explicitly carry zero; their aggregate is the sum of leaves.", nameof(nodes));
        // Each node has exactly one parent, so root reachability proves acyclicity. The queue
        // bounds stack-free validation; disconnected cyclic components cannot disappear silently.
        var queue = new Queue<(int Index, int Depth)>();
        foreach (var root in roots) queue.Enqueue((root, 0));
        var visited = 0; var depthCount = 0;
        while (queue.TryDequeue(out var item))
        {
            visited++;
            if (item.Depth >= MaximumDepth) throw new ArgumentException("The forest exceeds the maximum depth.", nameof(nodes));
            depthCount = Math.Max(depthCount, item.Depth + 1);
            foreach (var child in children[item.Index]) queue.Enqueue((child, item.Depth + 1));
        }
        if (visited != Nodes.Length) throw new ArgumentException("Parent cycles are not supported, including zero-weight cycles.", nameof(nodes));
        RootIndices = roots.ToImmutable();
        _children = children.Select(list => list.ToImmutableArray()).ToImmutableArray();
        var states = new NodeState[Nodes.Length];
        var preorder = ImmutableArray.CreateBuilder<int>(Nodes.Length);
        var leaves = ImmutableArray.CreateBuilder<int>();
        var total = BigInteger.Zero;
        foreach (var root in RootIndices) total += Visit(root, 0);
        _states = ImmutableArray.CreateRange(states); _preorder = preorder.MoveToImmutable();
        LeafIndices = leaves.ToImmutable(); Total = new(total); DepthCount = depthCount; DatasetVersion = datasetVersion;

        BigInteger Visit(int index, int depth)
        {
            var start = preorder.Count; preorder.Add(index); var leafStart = leaves.Count;
            var units = BigInteger.Zero;
            if (_children[index].IsEmpty) { units = FlowWeightTotal.ExactUnits(Nodes[index].Weight); leaves.Add(index); }
            else foreach (var child in _children[index]) units += Visit(child, depth + 1);
            states[index] = new(depth, start, preorder.Count - start, leafStart, leaves.Count - leafStart, new(units));
            return units;
        }
    }
    public ImmutableArray<TreemapNode> Nodes { get; }
    public ImmutableArray<int> RootIndices { get; }
    public ImmutableArray<int> LeafIndices { get; }
    public long DatasetVersion { get; }
    public int DepthCount { get; }
    public TreemapWeightTotal Total { get; }
    public int GetNodeIndex(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _indices.TryGetValue(key, out var index) ? index : throw new ArgumentException("Unknown node key.", nameof(key));
    }
    public ImmutableArray<int> GetChildren(string key) => _children[GetNodeIndex(key)];
    public string? GetParentKey(string key) => Nodes[GetNodeIndex(key)].ParentKey;
    public TreemapWeightTotal GetAggregate(string key) => _states[GetNodeIndex(key)].Total;

    internal TreemapChartHit GetNodeHit(int index)
    {
        var state = _states[index];
        return new(DatasetVersion, Nodes[index], state.Total, state.LeafStart, state.LeafCount);
    }

    public TreemapChartSnapshot Query(TreemapChartOptions? options = null, string? focusKey = null)
    {
        options ??= TreemapChartOptions.Default;
        var siblings = RootIndices; var baseDepth = 0; var start = 0; var end = Nodes.Length; var total = Total;
        if (focusKey is not null)
        {
            var focusIndex = GetNodeIndex(focusKey);
            if (_children[focusIndex].IsEmpty) throw new ArgumentException("Only an internal node can be a drill focus.", nameof(focusKey));
            var state = _states[focusIndex]; siblings = _children[focusIndex]; baseDepth = state.Depth + 1;
            start = state.PreorderStart + 1; end = state.PreorderStart + state.SubtreeCount; total = state.Total;
        }
        var required = 0;
        for (var position = start; position < end; position++)
            if (_states[_preorder[position]].Depth - baseDepth < options.DisplayDepth) required++;
        if (required > options.GeometryBudget || Nodes.Length * 4 + required * 2 > options.WorkBudget)
            return new(this, options, focusKey, total, required, [], ChartBudgetStatus.InsufficientBudget);
        var rectangles = ImmutableArray.CreateBuilder<TreemapChartRectangle>(required);
        Layout(siblings, total.Units, 0, 0, TreemapChartGeometry.CoordinateUnits, TreemapChartGeometry.CoordinateUnits, 0);
        return new(this, options, focusKey, total, required, rectangles.MoveToImmutable(), ChartBudgetStatus.Satisfied);

        void Layout(ImmutableArray<int> indices, BigInteger denominator, long left, long top, long right, long bottom, int depth)
        {
            var horizontal = depth % 2 == 0; var length = horizontal ? right - left : bottom - top;
            var origin = horizontal ? left : top; var cumulative = BigInteger.Zero; var previous = origin;
            foreach (var index in indices)
            {
                var state = _states[index]; cumulative += state.Total.Units;
                var next = denominator.IsZero ? origin : origin + (long)(length * cumulative / denominator);
                var nodeLeft = horizontal ? previous : left; var nodeTop = horizontal ? top : previous;
                var nodeRight = horizontal ? next : right; var nodeBottom = horizontal ? bottom : next;
                var status = state.Total.Units.IsZero ? TreemapRectangleStatus.Zero
                    : nodeLeft == nodeRight || nodeTop == nodeBottom ? TreemapRectangleStatus.Collapsed : TreemapRectangleStatus.Visible;
                rectangles.Add(new(new(DatasetVersion, Nodes[index], state.Total, state.LeafStart, state.LeafCount),
                    index, depth, Coordinate(nodeLeft), Coordinate(nodeTop), Coordinate(nodeRight), Coordinate(nodeBottom), status, _children[index].IsEmpty));
                if (depth + 1 < options.DisplayDepth && !_children[index].IsEmpty)
                    Layout(_children[index], state.Total.Units, nodeLeft, nodeTop, nodeRight, nodeBottom, depth + 1);
                previous = next;
            }
        }
    }
    private static double Coordinate(long ticks) => ticks * TreemapChartGeometry.CoordinateQuantum;
    private readonly record struct NodeState(int Depth, int PreorderStart, int SubtreeCount,
        int LeafStart, int LeafCount, TreemapWeightTotal Total);
}

public static class TreemapChartGeometry
{
    public const long CoordinateUnits = 1L << 48;
    public const double CoordinateQuantum = 1d / CoordinateUnits;

    /// <summary>Returns the deepest displayed original rectangle owning this half-open point.
    /// Shared edges have one sibling owner. At most 256 records are checked. Refused frames,
    /// zero/collapsed nodes and outside points never yield an invented hit.</summary>
    public static TreemapChartHit? HitTest(TreemapChartSnapshot snapshot, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (x < 0 || x >= 1 || y < 0 || y >= 1) return null;
        TreemapChartHit? hit = null;
        foreach (var rectangle in snapshot.Rectangles)
            if (rectangle.Status == TreemapRectangleStatus.Visible && x >= rectangle.Left && x < rectangle.Right
                && y >= rectangle.Top && y < rectangle.Bottom) hit = rectangle.Hit;
        return hit;
    }

    /// <summary>Preorder keyboard identity includes internal, zero and collapsed records.</summary>
    public static TreemapChartHit? GetHit(TreemapChartSnapshot snapshot, int index)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return index >= 0 && index < snapshot.Rectangles.Length ? snapshot.Rectangles[index].Hit : null;
    }
}
