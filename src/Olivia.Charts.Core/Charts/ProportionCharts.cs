using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>Original stable identity, display-only label, and unmodified input weight.
/// Unlike Cartesian gaps, missing, nonfinite, and negative weights are rejected.</summary>
public sealed record ProportionCategory
{
    public ProportionCategory(string key, string label, double? weight)
    {
        BarChartSource.ValidateText(key, nameof(key), identity: true);
        BarChartSource.ValidateText(label, nameof(label));
        if (!weight.HasValue || !double.IsFinite(weight.Value) || weight.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(weight), "A proportion weight must be present, finite, and nonnegative.");
        Key = key; Label = label; Weight = weight;
    }
    public string Key { get; }
    public string Label { get; }
    public double? Weight { get; }
}

public sealed class ProportionChartOptions
{
    public ProportionChartOptions(double innerRadius = 0, int geometryBudget = ProportionChartSource.MaximumCategories)
    {
        if (!double.IsFinite(innerRadius) || innerRadius < 0 || innerRadius >= 1)
            throw new ArgumentOutOfRangeException(nameof(innerRadius));
        if (geometryBudget < 0 || geometryBudget > ProportionChartSource.MaximumCategories)
            throw new ArgumentOutOfRangeException(nameof(geometryBudget));
        InnerRadius = innerRadius; GeometryBudget = geometryBudget;
    }
    public static ProportionChartOptions Default { get; } = new();
    public double InnerRadius { get; }
    public int GeometryBudget { get; }
}

public enum ProportionSliceStatus { Visible, Zero, Collapsed }

public readonly record struct ProportionChartHit(long DatasetVersion, ProportionCategory Category)
{
    public string Key => Category.Key;
    public string Label => Category.Label;
    public double? Weight => Category.Weight;
}

/// <summary>Angles in radians, clockwise in screen coordinates, starting at +X.
/// Visible intervals are half-open [StartAngle, EndAngle); the partition closes at 2π.
/// Positive weights unrepresentable at angle precision are explicitly Collapsed, never enlarged.</summary>
public readonly record struct ProportionChartSlice(ProportionChartHit Hit, int CategoryIndex,
    double StartAngle, double EndAngle, double Fraction, ProportionSliceStatus Status)
{
    public double SweepAngle => EndAngle - StartAngle;
}

public sealed class ProportionChartSnapshot
{
    internal ProportionChartSnapshot(ProportionChartSource source, ProportionChartOptions options,
        ImmutableArray<ProportionChartSlice> slices, double totalScale, double scaledTotal)
    {
        Source = source; Options = options; Slices = slices;
        TotalScale = totalScale; ScaledTotal = scaledTotal;
        RequiredSliceCount = source.Categories.Length;
        BudgetStatus = RequiredSliceCount <= options.GeometryBudget ? ChartBudgetStatus.Satisfied : ChartBudgetStatus.InsufficientBudget;
    }
    public ProportionChartSource Source { get; }
    public long DatasetVersion => Source.DatasetVersion;
    public ProportionChartOptions Options { get; }
    public ImmutableArray<ProportionChartSlice> Slices { get; }
    public int RequiredSliceCount { get; }
    public ChartBudgetStatus BudgetStatus { get; }
    public bool IsEmpty => TotalScale == 0;
    /// <summary>Largest input weight. TotalScale * ScaledTotal represents the sum without
    /// requiring its product to fit in a double; the scaled sum is compensated and rounded.</summary>
    public double TotalScale { get; }
    public double ScaledTotal { get; }
    /// <summary>Rounded total, possibly positive infinity. Geometry never divides by this product.</summary>
    public double TotalWeight => TotalScale * ScaledTotal;
    public bool TotalOverflow => double.IsPositiveInfinity(TotalWeight);
}

/// <summary>At most 256 original categories, each with at most 256 UTF-16 code units per text.
/// Copies input with at most cap + 1 advances and disposes its enumerator. Query performs bounded
/// linear scans; no sorting, merging, sampling, or minimum-angle substitution occurs.</summary>
public sealed class ProportionChartSource
{
    public const int MaximumCategories = 256;
    public const int MaximumTextLength = 256;
    public ProportionChartSource(IEnumerable<ProportionCategory> categories, long datasetVersion = 0)
    {
        if (datasetVersion < 0) throw new ArgumentOutOfRangeException(nameof(datasetVersion));
        Categories = BarChartSource.CopyBounded(categories, MaximumCategories, nameof(categories));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in Categories)
        {
            ArgumentNullException.ThrowIfNull(category);
            if (!keys.Add(category.Key)) throw new ArgumentException("Duplicate category key.", nameof(categories));
        }
        DatasetVersion = datasetVersion;
    }
    public ImmutableArray<ProportionCategory> Categories { get; }
    public long DatasetVersion { get; }

    public ProportionChartSnapshot Query(ProportionChartOptions? options = null)
    {
        options ??= ProportionChartOptions.Default;
        var scale = 0d;
        foreach (var category in Categories) scale = Math.Max(scale, category.Weight!.Value);
        var total = 0d; var compensation = 0d;
        if (scale > 0)
            foreach (var category in Categories) AddCompensated(category.Weight!.Value / scale, ref total, ref compensation);
        // Whole-frame refusal avoids silently presenting a partial denominator or partial chart.
        if (Categories.Length > options.GeometryBudget) return new(this, options, [], scale, total);
        var slices = ImmutableArray.CreateBuilder<ProportionChartSlice>(Categories.Length);
        var cumulative = 0d; var cumulativeCompensation = 0d; var angle = 0d;
        for (var i = 0; i < Categories.Length; i++)
        {
            var category = Categories[i];
            var weight = category.Weight!.Value;
            var scaled = scale > 0 ? weight / scale : 0;
            var fraction = total > 0 ? scaled / total : 0;
            AddCompensated(scaled, ref cumulative, ref cumulativeCompensation);
            var end = total > 0 ? Math.Clamp(cumulative / total * Math.Tau, angle, Math.Tau) : 0;
            // A zero or underflowed weight cannot consume compensated rounding residue.
            if (scaled == 0) end = angle;
            var status = weight == 0 ? ProportionSliceStatus.Zero : end > angle ? ProportionSliceStatus.Visible : ProportionSliceStatus.Collapsed;
            slices.Add(new(new(DatasetVersion, category), i, angle, end, fraction, status));
            angle = end;
        }
        return new(this, options, slices.MoveToImmutable(), scale, total);
    }

    private static void AddCompensated(double value, ref double sum, ref double compensation)
    {
        if (value == 0) return;
        var adjusted = value - compensation;
        var next = sum + adjusted;
        compensation = (next - sum) - adjusted;
        sum = next;
    }
}

public static class ProportionChartGeometry
{
    /// <summary>Unit-circle coordinates centered at (0,0), Y downward. Outer edge is included;
    /// donut hole and its inner edge are excluded. A pie's center belongs to the +X interval.
    /// No tolerance expands a sector or allows a hit through the hole.</summary>
    public static ProportionChartHit? HitTest(ProportionChartSnapshot snapshot, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (x < -1 || x > 1 || y < -1 || y > 1 || snapshot.IsEmpty) return null;
        var largest = Math.Max(Math.Abs(x), Math.Abs(y));
        var smallest = Math.Min(Math.Abs(x), Math.Abs(y));
        var ratio = largest > 0 ? smallest / largest : 0;
        var radius = largest * Math.Sqrt(1 + ratio * ratio);
        if (radius > 1) return null;
        var inner = snapshot.Options.InnerRadius;
        if (inner > 0 && largest <= inner)
        {
            // Compare in hole-radius units, so a subnormal inner radius is never squared
            // or rounded into a zero-width hole (including diagonal subnormal points).
            var innerX = x / inner; var innerY = y / inner;
            if (innerX * innerX + innerY * innerY <= 1) return null;
        }
        var angle = x == 0 && y == 0 ? 0 : Math.Atan2(y, x);
        if (angle < 0) angle += Math.Tau;
        // Addition can round an infinitesimal negative angle up to 2π; it belongs just below the seam.
        if (angle >= Math.Tau) angle = Math.BitDecrement(Math.Tau);
        foreach (var slice in snapshot.Slices)
            if (slice.Status == ProportionSliceStatus.Visible && angle >= slice.StartAngle && angle < slice.EndAngle)
                return slice.Hit;
        return null;
    }

    /// <summary>Original category-order keyboard enumeration, including zero and collapsed entries.
    /// No entries are exposed through a refused geometry frame.</summary>
    public static ProportionChartHit? GetHit(ProportionChartSnapshot snapshot, int index)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return index >= 0 && index < snapshot.Slices.Length ? snapshot.Slices[index].Hit : null;
    }
}
