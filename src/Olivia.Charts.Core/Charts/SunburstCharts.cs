using System.Collections.Immutable;
using System.Numerics;

namespace Olivia.Core.Charts;

public enum SunburstSectorStatus { Visible, Zero, Collapsed }

/// <summary>Visible levels below a focused internal node or the virtual forest root.
/// Fixed equal-width radial bands, not globally weight-proportional annular areas.</summary>
public sealed class SunburstChartOptions
{
    public SunburstChartOptions(int displayDepth = 2, int geometryBudget = SunburstChartSource.MaximumNodes,
        int workBudget = SunburstChartSource.MaximumWork)
    {
        if (displayDepth < 1 || displayDepth > SunburstChartSource.MaximumDepth)
            throw new ArgumentOutOfRangeException(nameof(displayDepth));
        if (geometryBudget < 0 || geometryBudget > SunburstChartSource.MaximumNodes)
            throw new ArgumentOutOfRangeException(nameof(geometryBudget));
        if (workBudget < 0 || workBudget > SunburstChartSource.MaximumWork)
            throw new ArgumentOutOfRangeException(nameof(workBudget));
        DisplayDepth = displayDepth; GeometryBudget = geometryBudget; WorkBudget = workBudget;
    }
    public static SunburstChartOptions Default { get; } = new();
    public int DisplayDepth { get; }
    public int GeometryBudget { get; }
    /// <summary>Conservative query allowance: four visits per source node plus two per displayed sector.
    /// Admission and the separate bounded hit-test operation are excluded.</summary>
    public int WorkBudget { get; }
}

/// <summary>Half-open angle turns on a 2^-48 grid, starting at +X and increasing clockwise
/// in screen coordinates. Depth starts at zero below focus. Half-open equal-width radial
/// bands occupy [.2,1); leaves do not expand into unoccupied deeper bands. Sibling angles
/// partition their parent's span using exact cumulative descendant leaf totals. Zero and
/// positive precision-collapsed nodes retain keyboard identity but have no pointer area.</summary>
public readonly record struct SunburstChartSector(TreemapChartHit Hit, int NodeIndex, int Depth,
    double StartTurn, double EndTurn, double InnerRadius, double OuterRadius,
    SunburstSectorStatus Status, bool IsLeaf)
{
    public double SweepTurn => EndTurn - StartTurn;
}

public sealed class SunburstChartSnapshot
{
    internal SunburstChartSnapshot(SunburstChartSource source, SunburstChartOptions options, string? focusKey,
        TreemapWeightTotal total, int requiredGeometryCount, ImmutableArray<SunburstChartSector> sectors,
        ChartBudgetStatus budgetStatus)
    {
        Source = source; Options = options; FocusKey = focusKey; Total = total;
        RequiredGeometryCount = requiredGeometryCount; Sectors = sectors; BudgetStatus = budgetStatus;
    }
    public SunburstChartSource Source { get; }
    public long DatasetVersion => Source.DatasetVersion;
    public SunburstChartOptions Options { get; }
    public string? FocusKey { get; }
    public TreemapWeightTotal Total { get; }
    public int RequiredGeometryCount { get; }
    public int RequiredWorkCount => Source.Nodes.Length * 4 + RequiredGeometryCount * 2;
    public ChartBudgetStatus BudgetStatus { get; }
    public bool IsEmpty => RequiredGeometryCount == 0;
    public bool HasPositiveWeight => !Total.Units.IsZero;
    public ImmutableArray<SunburstChartSector> Sectors { get; }
}

/// <summary>Original bounded sunburst over the validated immutable Treemap forest contract:
/// at most 256 nodes, 16 levels, finite nonnegative leaf weights, explicit zero internal own
/// weights, ordinal identities, stable caller sibling order, exact dyadic leaf sums and raw
/// forest-wide leaf ranges. No sorting, minimum-angle enlargement, or missing-parent inference.
/// Geometry/work refusal returns no partial sector prefix. Angles, not global annular area,
/// encode weight; radial position encodes relative hierarchy depth.</summary>
public sealed class SunburstChartSource
{
    public const int MaximumNodes = TreemapChartSource.MaximumNodes;
    public const int MaximumDepth = TreemapChartSource.MaximumDepth;
    public const int MaximumWork = TreemapChartSource.MaximumWork;
    public const int MaximumTextLength = TreemapChartSource.MaximumTextLength;
    private readonly ImmutableArray<ImmutableArray<int>> _children;

    public SunburstChartSource(IEnumerable<TreemapNode> nodes, long datasetVersion = 0)
    {
        Hierarchy = new(nodes, datasetVersion);
        _children = Nodes.Select(node => Hierarchy.GetChildren(node.Key)).ToImmutableArray();
    }
    public TreemapChartSource Hierarchy { get; }
    public ImmutableArray<TreemapNode> Nodes => Hierarchy.Nodes;
    public ImmutableArray<int> RootIndices => Hierarchy.RootIndices;
    public ImmutableArray<int> LeafIndices => Hierarchy.LeafIndices;
    public int DepthCount => Hierarchy.DepthCount;
    public long DatasetVersion => Hierarchy.DatasetVersion;
    public TreemapWeightTotal Total => Hierarchy.Total;
    public ImmutableArray<int> GetChildren(string key) => Hierarchy.GetChildren(key);
    public string? GetParentKey(string key) => Hierarchy.GetParentKey(key);
    public TreemapWeightTotal GetAggregate(string key) => Hierarchy.GetAggregate(key);
    public int GetNodeIndex(string key) => Hierarchy.GetNodeIndex(key);

    public SunburstChartSnapshot Query(SunburstChartOptions? options = null, string? focusKey = null)
    {
        options ??= SunburstChartOptions.Default;
        var siblings = RootIndices; var total = Total;
        if (focusKey is not null)
        {
            var index = GetNodeIndex(focusKey);
            if (_children[index].IsEmpty)
                throw new ArgumentException("Only an internal node can be a drill focus.", nameof(focusKey));
            siblings = _children[index]; total = Hierarchy.GetNodeHit(index).Aggregate;
        }
        var required = Count(siblings, 0);
        if (required > options.GeometryBudget || Nodes.Length * 4 + required * 2 > options.WorkBudget)
            return new(this, options, focusKey, total, required, [], ChartBudgetStatus.InsufficientBudget);
        var sectors = ImmutableArray.CreateBuilder<SunburstChartSector>(required);
        Layout(siblings, total.Units, 0, SunburstChartGeometry.AngleUnits, 0);
        return new(this, options, focusKey, total, required, sectors.MoveToImmutable(), ChartBudgetStatus.Satisfied);

        int Count(ImmutableArray<int> indices, int depth)
        {
            var count = indices.Length;
            if (depth + 1 < options.DisplayDepth)
                foreach (var index in indices) count += Count(_children[index], depth + 1);
            return count;
        }
        void Layout(ImmutableArray<int> indices, BigInteger denominator, long start, long end, int depth)
        {
            var cumulative = BigInteger.Zero; var previous = start;
            var inner = Radius(depth); var outer = Radius(depth + 1);
            foreach (var index in indices)
            {
                var hit = Hierarchy.GetNodeHit(index); cumulative += hit.Aggregate.Units;
                var next = denominator.IsZero ? start : start + (long)((end - start) * cumulative / denominator);
                var status = hit.Aggregate.Units.IsZero ? SunburstSectorStatus.Zero
                    : previous == next ? SunburstSectorStatus.Collapsed : SunburstSectorStatus.Visible;
                sectors.Add(new(hit, index, depth, previous * SunburstChartGeometry.AngleQuantum,
                    next * SunburstChartGeometry.AngleQuantum, inner, outer, status, _children[index].IsEmpty));
                if (depth + 1 < options.DisplayDepth && !_children[index].IsEmpty)
                    Layout(_children[index], hit.Aggregate.Units, previous, next, depth + 1);
                previous = next;
            }
        }
        double Radius(int band) => band == options.DisplayDepth ? 1d
            : SunburstChartGeometry.HoleRadius + (1d - SunburstChartGeometry.HoleRadius) * band / options.DisplayDepth;
    }
}

public static class SunburstChartGeometry
{
    public const long AngleUnits = 1L << 48;
    public const double AngleQuantum = 1d / AngleUnits;
    public const double HoleRadius = .2;

    /// <summary>Centered normalized coordinates. The hole r&lt;.2, outer boundary r&gt;=1,
    /// unused deeper bands, zero/collapsed sectors and refused frames return null. Shared
    /// radial edges belong to the outer band; shared angular edges to the following sibling.
    /// The +X seam is turn zero (including negative signed zero). At most 256 sectors scanned.</summary>
    public static TreemapChartHit? HitTest(SunburstChartSnapshot snapshot, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (x <= -1 || x >= 1 || y <= -1 || y >= 1) return null;
        var radius = Math.Sqrt(x * x + y * y);
        if (radius < HoleRadius || radius >= 1) return null;
        var turn = Math.Atan2(y, x) / Math.Tau;
        if (turn < 0) turn += 1;
        // An angle immediately below the seam may round to 1; retain its final-sector side.
        if (turn >= 1 || (turn == 0 && y < 0 && x > 0)) turn = Math.BitDecrement(1d);
        foreach (var sector in snapshot.Sectors)
            if (sector.Status == SunburstSectorStatus.Visible && radius >= sector.InnerRadius
                && radius < sector.OuterRadius && turn >= sector.StartTurn && turn < sector.EndTurn)
                return sector.Hit;
        return null;
    }

    /// <summary>Preorder keyboard identity includes zero and positive-collapsed records.</summary>
    public static TreemapChartHit? GetHit(SunburstChartSnapshot snapshot, int index)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return index >= 0 && index < snapshot.Sectors.Length ? snapshot.Sectors[index].Hit : null;
    }
}
