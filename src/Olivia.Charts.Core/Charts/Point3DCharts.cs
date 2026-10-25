using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>Original keyed XYZ triple. Missing or nonfinite coordinates make the entire triple a gap.</summary>
public readonly record struct Point3DSample(long Key, double? X, double? Y, double? Z)
{
    public bool IsGap => !X.HasValue || !Y.HasValue || !Z.HasValue ||
        !double.IsFinite(X.Value) || !double.IsFinite(Y.Value) || !double.IsFinite(Z.Value);
}

public readonly record struct Point3DVector(double X, double Y, double Z);

/// <summary>Bounded orthographic camera in uniformly normalized, right-handed world coordinates.</summary>
public sealed class Point3DCamera
{
    public Point3DCamera(double yaw = 0, double pitch = 0, Point3DVector target = default,
        double halfHeight = 1.25, double distance = 4, double near = .01, double far = 16)
    {
        Validate(yaw, -Math.PI, Math.PI, nameof(yaw));
        Validate(pitch, -Math.PI / 2, Math.PI / 2, nameof(pitch));
        Validate(target.X, -4, 4, nameof(target)); Validate(target.Y, -4, 4, nameof(target)); Validate(target.Z, -4, 4, nameof(target));
        Validate(halfHeight, .01, 16, nameof(halfHeight)); Validate(distance, .01, 32, nameof(distance));
        Validate(near, .001, 64, nameof(near)); Validate(far, near, 64, nameof(far));
        if (far <= near) throw new ArgumentOutOfRangeException(nameof(far));
        Yaw = yaw; Pitch = pitch; Target = target; HalfHeight = halfHeight; Distance = distance; Near = near; Far = far;
    }
    public static Point3DCamera Default { get; } = new();
    public double Yaw { get; }
    public double Pitch { get; }
    public Point3DVector Target { get; }
    public double HalfHeight { get; }
    public double Distance { get; }
    public double Near { get; }
    public double Far { get; }
    internal static void Validate(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum) throw new ArgumentOutOfRangeException(name);
    }
    internal (Point3DVector Right, Point3DVector Up, Point3DVector Forward) Basis()
    {
        var sy = Math.Sin(Yaw); var cy = Math.Cos(Yaw); var sp = Math.Sin(Pitch); var cp = Math.Cos(Pitch);
        return (new(cy, 0, -sy), new(-sy * sp, cp, -cy * sp), new(sy * cp, sp, cy * cp));
    }
}

public sealed class Point3DViewport
{
    public Point3DViewport(double width, double height)
    {
        Point3DCamera.Validate(width, 0, 8192, nameof(width)); Point3DCamera.Validate(height, 0, 8192, nameof(height));
        if (width > 0 && width < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (height > 0 && height < 1) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width; Height = height;
    }
    public static Point3DViewport Default { get; } = new(800, 600);
    public double Width { get; }
    public double Height { get; }
    public bool IsEmpty => Width == 0 || Height == 0;
}

public readonly record struct Point3DHit(string SourceId, long Version, Point3DSample Sample)
{
    public long Key => Sample.Key;
    public double? X => Sample.X;
    public double? Y => Sample.Y;
    public double? Z => Sample.Z;
}

public readonly record struct Point3DProjectedPoint(Point3DHit Hit, int SourceIndex, double X, double Y, double Depth);
public readonly record struct Point3DRay(Point3DVector Origin, Point3DVector Direction);

public sealed class Point3DSnapshot
{
    internal Point3DSnapshot(Point3DSource source, Point3DCamera camera, Point3DViewport viewport,
        double worldScale, ImmutableArray<Point3DProjectedPoint> points, ImmutableArray<Point3DHit> gaps,
        int clippedCount, int requiredPointCount, int geometryBudget)
    {
        Source = source; Camera = camera; Viewport = viewport; WorldScale = worldScale; Points = points; Gaps = gaps;
        ClippedCount = clippedCount; RequiredPointCount = requiredPointCount;
        BudgetStatus = requiredPointCount <= geometryBudget ? ChartBudgetStatus.Satisfied : ChartBudgetStatus.InsufficientBudget;
    }
    public Point3DSource Source { get; }
    public Point3DCamera Camera { get; }
    public Point3DViewport Viewport { get; }
    /// <summary>Raw coordinates are divided by this common scale; zero-only and empty sources use one.</summary>
    public double WorldScale { get; }
    /// <summary>Far-to-near draw order, then ascending source index for depth ties.</summary>
    public ImmutableArray<Point3DProjectedPoint> Points { get; }
    public ImmutableArray<Point3DHit> Gaps { get; }
    public int ClippedCount { get; }
    public int RequiredPointCount { get; }
    public ChartBudgetStatus BudgetStatus { get; }
}

/// <summary>Immutable bounded raw triples. Uniform normalization preserves axis proportions and raw identity.</summary>
public sealed class Point3DSource
{
    public const int MaximumRawEntries = 2048;
    public Point3DSource(string id, IEnumerable<Point3DSample> samples, long version = 0)
    {
        BarChartSource.ValidateText(id, nameof(id), identity: true);
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        Samples = BarChartSource.CopyBounded(samples, MaximumRawEntries, nameof(samples));
        var keys = new HashSet<long>();
        foreach (var sample in Samples)
            if (!keys.Add(sample.Key)) throw new ArgumentException("Duplicate sample key.", nameof(samples));
        Id = id; Version = version;
    }
    public string Id { get; }
    public long Version { get; }
    public ImmutableArray<Point3DSample> Samples { get; }

    public Point3DSnapshot Query(Point3DCamera? camera = null, Point3DViewport? viewport = null, int geometryBudget = MaximumRawEntries)
    {
        if (geometryBudget < 0 || geometryBudget > MaximumRawEntries) throw new ArgumentOutOfRangeException(nameof(geometryBudget));
        camera ??= Point3DCamera.Default; viewport ??= Point3DViewport.Default;
        var scale = 0d;
        foreach (var sample in Samples)
            if (!sample.IsGap)
                scale = Math.Max(scale, Math.Max(Math.Abs(sample.X!.Value), Math.Max(Math.Abs(sample.Y!.Value), Math.Abs(sample.Z!.Value))));
        if (scale == 0) scale = 1;
        var gaps = ImmutableArray.CreateBuilder<Point3DHit>();
        var points = new List<Point3DProjectedPoint>();
        var clipped = 0;
        var (right, up, forward) = camera.Basis();
        for (var index = 0; index < Samples.Length; index++)
        {
            var sample = Samples[index]; var hit = new Point3DHit(Id, Version, sample);
            if (sample.IsGap) { gaps.Add(hit); continue; }
            if (viewport.IsEmpty) { clipped++; continue; }
            var relative = new Point3DVector(sample.X!.Value / scale - camera.Target.X,
                sample.Y!.Value / scale - camera.Target.Y, sample.Z!.Value / scale - camera.Target.Z);
            var depth = camera.Distance - Point3DChartGeometry.Dot(relative, forward);
            var nx = Point3DChartGeometry.Dot(relative, right) / (camera.HalfHeight * (viewport.Width / viewport.Height));
            var ny = Point3DChartGeometry.Dot(relative, up) / camera.HalfHeight;
            if (depth < camera.Near || depth > camera.Far || nx < -1 || nx > 1 || ny < -1 || ny > 1)
            { clipped++; continue; }
            points.Add(new(hit, index, (nx + 1) * (viewport.Width / 2), (1 - ny) * (viewport.Height / 2), depth));
        }
        var required = points.Count;
        points.Sort((a, b) => { var depth = b.Depth.CompareTo(a.Depth); return depth != 0 ? depth : a.SourceIndex.CompareTo(b.SourceIndex); });
        return new(this, camera, viewport, scale, required <= geometryBudget ? points.ToImmutableArray() : [],
            gaps.ToImmutable(), clipped, required, geometryBudget);
    }
}

/// <summary>CPU orthographic projection utilities. Picking uses closed screen-space disks, not ray/sphere intersection.</summary>
public static class Point3DChartGeometry
{
    public static Point3DHit? HitTest(Point3DSnapshot snapshot, double x, double y, double radius = 5)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateCoordinates(x, y); Point3DCamera.Validate(radius, 0, 32, nameof(radius));
        if (!Contains(snapshot.Viewport, x, y)) return null;
        for (var i = snapshot.Points.Length - 1; i >= 0; i--)
        {
            var point = snapshot.Points[i]; var dx = Math.Abs(point.X - x); var dy = Math.Abs(point.Y - y);
            if (dx > radius || dy > radius) continue;
            if (radius == 0) return point.Hit;
            var nx = dx / radius; var ny = dy / radius;
            if (nx * nx + ny * ny <= 1) return point.Hit;
        }
        return null;
    }
    /// <summary>Draw-order keyboard enumeration includes occluded points. Invalid indices return null.</summary>
    public static Point3DHit? GetHit(Point3DSnapshot snapshot, int index)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return index >= 0 && index < snapshot.Points.Length ? snapshot.Points[index].Hit : null;
    }
    /// <summary>Orthographic ray in normalized world coordinates; null for empty/outside viewports.</summary>
    public static Point3DRay? RayAt(Point3DSnapshot snapshot, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ValidateCoordinates(x, y);
        var viewport = snapshot.Viewport;
        if (!Contains(viewport, x, y)) return null;
        var camera = snapshot.Camera; var (right, up, forward) = camera.Basis();
        var horizontal = (2 * (x / viewport.Width) - 1) * camera.HalfHeight * (viewport.Width / viewport.Height);
        var vertical = (1 - 2 * (y / viewport.Height)) * camera.HalfHeight;
        if (!double.IsFinite(horizontal)) return null;
        var origin = new Point3DVector(camera.Target.X + right.X * horizontal + up.X * vertical + forward.X * camera.Distance,
            camera.Target.Y + right.Y * horizontal + up.Y * vertical + forward.Y * camera.Distance,
            camera.Target.Z + right.Z * horizontal + up.Z * vertical + forward.Z * camera.Distance);
        if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y) || !double.IsFinite(origin.Z)) return null;
        return new(origin, new(-forward.X, -forward.Y, -forward.Z));
    }
    internal static double Dot(Point3DVector a, Point3DVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static bool Contains(Point3DViewport viewport, double x, double y) =>
        !viewport.IsEmpty && x >= 0 && x <= viewport.Width && y >= 0 && y <= viewport.Height;
    private static void ValidateCoordinates(double x, double y)
    {
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
    }
}
