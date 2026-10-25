using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>Chart coordinates: X and Y are matrix arguments; Z is height.</summary>
public enum SurfaceAxisDimension { X, Y, Z }
public enum SurfaceAxisStrokeKind { Border, Grid, Tick }

/// <summary>Raster coordinates and camera depth, in the exact raster's pixel space.</summary>
public readonly record struct SurfaceAxisReferencePoint(double X, double Y, double Depth);

/// <summary>A six-plane-clipped box edge. Null Dimension denotes a neutral box border.
/// A line describes geometry, not guaranteed visibility: draw Spans, not these full segments.</summary>
public readonly record struct SurfaceAxisReferenceLine(SurfaceAxisReferencePoint Start,
    SurfaceAxisReferencePoint End, SurfaceAxisDimension? Dimension, SurfaceAxisStrokeKind Kind = SurfaceAxisStrokeKind.Border,
    SurfaceAxisReferencePlane? Plane = null);

/// <summary>Fixed normalized world coordinate of a far-face grid (0 = X, 1 = height, 2 = Y).</summary>
public readonly record struct SurfaceAxisReferencePlane(int WorldAxis, double Coordinate);

/// <summary>A visible one-pixel-high run, with inclusive StartX and exclusive EndX.</summary>
public readonly record struct SurfaceAxisReferenceSpan(int Y, int StartX, int EndX, SurfaceAxisDimension? Dimension,
    SurfaceAxisStrokeKind Kind = SurfaceAxisStrokeKind.Border)
{
    public SurfaceAxisReferencePlane? Plane { get; init; }
}

/// <summary>A raw numeric graduation on a silhouette edge. Anchor and outward vector are in raster pixels.
/// Labels still require measured text bounds, clipping and overlap checks in the UI.</summary>
public readonly record struct SurfaceAxisTick(SurfaceAxisDimension Dimension, double Value,
    SurfaceAxisReferencePoint Anchor, double OutwardX, double OutwardY);

/// <summary>Original domain bounds and their un-clipped projected endpoints. Endpoints are null
/// for an empty viewport or a perspective endpoint behind the eye. LabelAnchor is a visible background line-pixel center;
/// callers must still check the whole text footprint before placing a label inside the raster.</summary>
public readonly record struct SurfaceAxisReferenceAxis(SurfaceAxisDimension Dimension, double Minimum,
    double Maximum, SurfaceAxisReferencePoint? MinimumPoint, SurfaceAxisReferencePoint? MaximumPoint,
    SurfaceAxisReferencePoint? LabelAnchor);

/// <summary>Immutable range reference belonging to one exact rendered frame. No raster, source,
/// projection or picking buffer is modified. Storage is proportional to bounded line length, not area.</summary>
public sealed class SurfaceAxisReferenceSnapshot
{
    internal SurfaceAxisReferenceSnapshot(SurfaceRasterSnapshot raster, Point3DVector farCorner,
        ImmutableArray<SurfaceAxisReferenceLine> lines, ImmutableArray<SurfaceAxisReferenceSpan> spans,
        ImmutableArray<SurfaceAxisReferenceAxis> axes, ImmutableArray<SurfaceAxisTick> ticks = default)
    {
        Raster = raster; FarCorner = farCorner; Lines = lines; Spans = spans; Axes = axes;
        Ticks = ticks.IsDefault ? [] : ticks;
    }

    public SurfaceRasterSnapshot Raster { get; }
    /// <summary>The corner shared by the three farther faces, in raw world=(argument X,height,argument Y) order.</summary>
    public Point3DVector FarCorner { get; }
    public ImmutableArray<SurfaceAxisReferenceLine> Lines { get; }
    public ImmutableArray<SurfaceAxisReferenceSpan> Spans { get; }
    public ImmutableArray<SurfaceAxisReferenceAxis> Axes { get; }
    public ImmutableArray<SurfaceAxisTick> Ticks { get; }

    /// <summary>Apply the same diffuse-only lighting as the underlying far domain face.
    /// Grid paint remains an adapter choice; this never changes depth or source identity.</summary>
    public SurfaceColor ShadeDomainColor(SurfaceColor color, SurfaceAxisReferencePlane plane)
    {
        if (plane.WorldAxis is < 0 or > 2 || !double.IsFinite(plane.Coordinate))
            throw new ArgumentOutOfRangeException(nameof(plane));
        if (!Raster.RenderingOptions.Lighting) return color;
        var normal = plane.WorldAxis == 0 ? new Point3DVector(1, 0, 0)
            : plane.WorldAxis == 1 ? new Point3DVector(0, 1, 0) : new Point3DVector(0, 0, 1);
        var position = new Point3DVector(normal.X * plane.Coordinate, normal.Y * plane.Coordinate, normal.Z * plane.Coordinate);
        var (right, up, forward) = Raster.Camera.Basis();
        var diffuse = SurfaceLighting.Evaluate(normal, SurfaceLighting.Subtract(position, Raster.Camera.Target),
            Raster.Camera, Raster.RenderingOptions.Perspective, right, up, forward).Diffuse;
        var shaded = SurfaceLighting.Shade(color.Bgra, diffuse, 0);
        return new(shaded.R, shaded.G, shaded.B, shaded.A);
    }

    /// <summary>Only visible reference pixels block UI picking. Hidden/clipped lines do not.</summary>
    public bool ContainsPixel(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Raster.Width || y >= Raster.Height) return false;
        var pixel = y * Raster.Width + x;
        var low = 0; var high = Spans.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var span = Spans[middle]; var start = span.Y * Raster.Width + span.StartX;
            if (pixel < start) high = middle - 1;
            else if (pixel >= span.Y * Raster.Width + span.EndX) low = middle + 1;
            else return true;
        }
        return false;
    }
}

/// <summary>Bounded range reference with optional numeric graduations and far-face grids.
/// The three farther box faces follow camera-eye distance (including pan); ties choose
/// left, top, back. Their union contributes nine edges; the three meeting at the far corner are axes.</summary>
public static class SurfaceAxisReference
{
    public const int MaximumLineCount = 9;
    public const int MaximumVisiblePixels = MaximumLineCount * SurfaceRasterizer.MaximumDimension;
    public const int MaximumTicksPerAxis = 16;
    public const int MaximumDecoratedLineCount = MaximumLineCount + 9 * MaximumTicksPerAxis;

    public static SurfaceAxisReferenceSnapshot Build(SurfaceRasterSnapshot raster) => Build(raster, false);

    public static SurfaceAxisReferenceSnapshot Build(SurfaceRasterSnapshot raster, bool includeGraduations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(raster);
        cancellationToken.ThrowIfCancellationRequested();
        var camera = raster.Camera;
        var (right, up, forward) = camera.Basis();
        var eye = new Point3DVector(camera.Target.X + camera.Distance * forward.X,
            camera.Target.Y + camera.Distance * forward.Y, camera.Target.Z + camera.Distance * forward.Z);
        // Each opposite face-center pair has equal transverse coordinates. For this centered
        // box the eye-coordinate sign is the exact distance comparison, without squared overflow.
        var farIndex = (eye.X < 0 ? 1 : 0) | (eye.Y <= 0 ? 2 : 0) | (eye.Z < 0 ? 4 : 0);
        var source = raster.Source;
        var minimum = new Point3DVector(source.XArguments[0], raster.MinimumHeight, source.YArguments[0]);
        var maximum = new Point3DVector(source.XArguments[^1], raster.MaximumHeight, source.YArguments[^1]);
        var farCorner = Corner(farIndex, minimum, maximum);
        var axes = ImmutableArray.CreateBuilder<SurfaceAxisReferenceAxis>(3);
        if (raster.Width == 0 || raster.Height == 0)
        {
            axes.Add(new(SurfaceAxisDimension.X, minimum.X, maximum.X, null, null, null));
            axes.Add(new(SurfaceAxisDimension.Y, minimum.Z, maximum.Z, null, null, null));
            axes.Add(new(SurfaceAxisDimension.Z, minimum.Y, maximum.Y, null, null, null));
            return new(raster, farCorner, [], [], axes.MoveToImmutable());
        }

        var halfWidth = camera.HalfHeight * ((double)raster.Width / raster.Height);
        Span<SurfaceAxisReferencePoint> corners = stackalloc SurfaceAxisReferencePoint[8];
        for (var i = 0; i < corners.Length; i++)
        {
            var raw = Corner(i, minimum, maximum);
            var relative = new Point3DVector(Normalize(raw.X, raster.WorldCenter.X, raster.WorldScale) - camera.Target.X,
                Normalize(raw.Y, raster.WorldCenter.Y, raster.WorldScale) - camera.Target.Y,
                Normalize(raw.Z, raster.WorldCenter.Z, raster.WorldScale) - camera.Target.Z);
            // Reuse the camera basis and dot product used by SurfaceRasterizer, with its exact
            // centered shared-scale normalization and homogeneous frustum.
            corners[i] = new(Point3DChartGeometry.Dot(relative, right) / halfWidth,
                Point3DChartGeometry.Dot(relative, up) / camera.HalfHeight,
                camera.Distance - Point3DChartGeometry.Dot(relative, forward));
        }
        axes.Add(Axis(SurfaceAxisDimension.X, 1, minimum.X, maximum.X, corners, farIndex, raster));
        axes.Add(Axis(SurfaceAxisDimension.Y, 4, minimum.Z, maximum.Z, corners, farIndex, raster));
        axes.Add(Axis(SurfaceAxisDimension.Z, 2, minimum.Y, maximum.Y, corners, farIndex, raster));
        var lines = ImmutableArray.CreateBuilder<SurfaceAxisReferenceLine>(MaximumLineCount);
        for (var worldAxis = 0; worldAxis < 3; worldAxis++)
        {
            var bit = 1 << worldAxis;
            var otherBits = 7 ^ bit;
            for (var i = 0; i < 8; i++)
            {
                if ((i & bit) != 0) continue;
                var matches = ~(i ^ farIndex) & otherBits;
                if (matches == 0) continue; // Edge touches neither selected face.
                var a = corners[i]; var b = corners[i | bit];
                if (!Clip(ref a, ref b, raster)) continue;
                SurfaceAxisDimension? dimension = matches == otherBits ? worldAxis switch
                {
                    0 => SurfaceAxisDimension.X, 1 => SurfaceAxisDimension.Z, _ => SurfaceAxisDimension.Y
                } : null;
                lines.Add(new(Screen(a, raster), Screen(b, raster), dimension));
            }
        }

        var ticks = includeGraduations
            ? SurfaceAxisGraduations.Add(raster, corners, farIndex, minimum, maximum, lines, cancellationToken)
            : ImmutableArray<SurfaceAxisTick>.Empty;

        // Bounded line storage, never another width*height coverage or depth array.
        var capacity = 0;
        foreach (var line in lines) capacity += PixelBound(line, raster) * StrokeWidth(line);
        var pixels = new List<VisiblePixel>(capacity);
        Span<SurfaceAxisReferencePlane?> planes = stackalloc SurfaceAxisReferencePlane?[3];
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Plane is { } plane) planes[plane.WorldAxis] = plane;
            Rasterize(line, raster, pixels);
        }
        pixels.Sort(static (a, b) =>
        {
            var order = a.Index.CompareTo(b.Index);
            if (order == 0) order = a.Depth.CompareTo(b.Depth);
            if (order == 0) order = a.Kind.CompareTo(b.Kind);
            // At an equal-depth intersection an axis takes precedence over a neutral border.
            return order != 0 ? order : Priority(a.Dimension).CompareTo(Priority(b.Dimension));
        });
        var spans = ImmutableArray.CreateBuilder<SurfaceAxisReferenceSpan>();
        Span<double> anchorDistances = stackalloc double[3];
        anchorDistances.Fill(double.PositiveInfinity);
        var previous = -1;
        foreach (var pixel in pixels)
        {
            if (pixel.Index == previous) continue;
            previous = pixel.Index;
            var y = pixel.Index / raster.Width; var x = pixel.Index % raster.Width;
            var plane = pixel.PlaneWorldAxis < 0 ? null : planes[pixel.PlaneWorldAxis];
            if (spans.Count > 0 && spans[^1] is var last && last.Y == y &&
                last.EndX == x && last.Dimension == pixel.Dimension && last.Kind == pixel.Kind && last.Plane == plane)
                spans[^1] = last with { EndX = x + 1 };
            else spans.Add(new(y, x, x + 1, pixel.Dimension, pixel.Kind) { Plane = plane });
            if (pixel.Dimension is not { } dimension || !double.IsPositiveInfinity(raster.Depths[pixel.Index])) continue;
            var axisIndex = (int)dimension;
            var axis = axes[axisIndex];
            if (axis.MaximumPoint is not { } endpoint) continue;
            var dx = x + .5 - endpoint.X; var dy = y + .5 - endpoint.Y;
            var distance = dx * dx + dy * dy;
            if (distance >= anchorDistances[axisIndex]) continue;
            anchorDistances[axisIndex] = distance;
            axes[axisIndex] = axis with { LabelAnchor = new(x + .5, y + .5, pixel.Depth) };
        }
        return new(raster, farCorner, lines.ToImmutable(), spans.ToImmutable(), axes.MoveToImmutable(), ticks);
    }

    private static int Priority(SurfaceAxisDimension? dimension) => dimension.HasValue ? (int)dimension.Value : 3;
    private static Point3DVector Corner(int index, Point3DVector minimum, Point3DVector maximum) => new(
        (index & 1) == 0 ? minimum.X : maximum.X, (index & 2) == 0 ? minimum.Y : maximum.Y,
        (index & 4) == 0 ? minimum.Z : maximum.Z);
    private static double Normalize(double value, double center, double scale)
    {
        var difference = value - center;
        return double.IsFinite(difference) ? difference / scale : value / scale - center / scale;
    }
    internal static SurfaceAxisReferencePoint Screen(SurfaceAxisReferencePoint p, SurfaceRasterSnapshot raster) => new(
        (p.X / raster.RenderingOptions.W(p.Depth, raster.Camera) + 1) * raster.Width / 2,
        (1 - p.Y / raster.RenderingOptions.W(p.Depth, raster.Camera)) * raster.Height / 2, p.Depth);
    private static SurfaceAxisReferencePoint? Endpoint(SurfaceAxisReferencePoint p, SurfaceRasterSnapshot raster) =>
        raster.RenderingOptions.W(p.Depth, raster.Camera) > 0 ? Screen(p, raster) : null;
    private static SurfaceAxisReferenceAxis Axis(SurfaceAxisDimension dimension, int bit, double minimum,
        double maximum, ReadOnlySpan<SurfaceAxisReferencePoint> corners, int farIndex, SurfaceRasterSnapshot raster) =>
        new(dimension, minimum, maximum, Endpoint(corners[farIndex & ~bit], raster),
            Endpoint(corners[farIndex | bit], raster), null);

    internal static bool Clip(ref SurfaceAxisReferencePoint a, ref SurfaceAxisReferencePoint b, SurfaceRasterSnapshot raster)
    {
        // Same six half-spaces and scaled crossing interpolation as the surface polygon clipper.
        for (var plane = 0; plane < 6; plane++)
        {
            var da = Distance(a, plane, raster); var db = Distance(b, plane, raster);
            if (da < 0 && db < 0) return false;
            if (da >= 0 && db >= 0) continue;
            var magnitude = Math.Max(Math.Abs(da), Math.Abs(db));
            var pa = Math.Abs(da) / magnitude; var pb = Math.Abs(db) / magnitude;
            var intersection = Lerp(a, b, pa / (pa + pb));
            if (da < 0) a = intersection; else b = intersection;
        }
        return true;
    }
    private static double Distance(SurfaceAxisReferencePoint point, int plane, SurfaceRasterSnapshot raster) => plane switch
    {
        0 => point.X + raster.RenderingOptions.W(point.Depth, raster.Camera),
        1 => raster.RenderingOptions.W(point.Depth, raster.Camera) - point.X,
        2 => point.Y + raster.RenderingOptions.W(point.Depth, raster.Camera),
        3 => raster.RenderingOptions.W(point.Depth, raster.Camera) - point.Y,
        4 => point.Depth - raster.Camera.Near, _ => raster.Camera.Far - point.Depth
    };
    internal static SurfaceAxisReferencePoint Lerp(SurfaceAxisReferencePoint a, SurfaceAxisReferencePoint b, double t) =>
        new(a.X * (1 - t) + b.X * t, a.Y * (1 - t) + b.Y * t, a.Depth * (1 - t) + b.Depth * t);

    private static int PixelBound(SurfaceAxisReferenceLine line, SurfaceRasterSnapshot raster)
    {
        var horizontal = Math.Abs(line.End.X - line.Start.X) >= Math.Abs(line.End.Y - line.Start.Y);
        var limit = horizontal ? raster.Width : raster.Height;
        var a = horizontal ? line.Start.X : line.Start.Y; var b = horizontal ? line.End.X : line.End.Y;
        return Math.Clamp((int)Math.Floor(Math.Max(a, b)), 0, limit - 1) -
            Math.Clamp((int)Math.Floor(Math.Min(a, b)), 0, limit - 1) + 1;
    }
    private static void Rasterize(SurfaceAxisReferenceLine line, SurfaceRasterSnapshot raster, List<VisiblePixel> pixels)
    {
        var dx = line.End.X - line.Start.X; var dy = line.End.Y - line.Start.Y;
        var horizontal = Math.Abs(dx) >= Math.Abs(dy);
        var a = horizontal ? line.Start.X : line.Start.Y; var b = horizontal ? line.End.X : line.End.Y;
        var limit = horizontal ? raster.Width : raster.Height;
        var start = Math.Clamp((int)Math.Floor(Math.Min(a, b)), 0, limit - 1);
        var end = Math.Clamp((int)Math.Floor(Math.Max(a, b)), 0, limit - 1);
        var planeDepth = line.Plane is { } plane ? PlaneEquation(plane, raster) : (DepthEquation?)null;
        var strokeWidth = StrokeWidth(line);
        for (var major = start; major <= end; major++)
        {
            // Digital strokes sample one center per major-axis pixel, then widen X/Y grid bands.
            // Frustum-boundary strokes snap to the last in-bounds pixel, never outside the frame.
            var t = a == b ? (line.End.Depth < line.Start.Depth ? 1 : 0) :
                Math.Clamp((major + .5 - a) / (b - a), 0, 1);
            var point = Lerp(line.Start, line.End, t);
            if (raster.RenderingOptions.Perspective)
                point = point with { Depth = 1 / ((1 - t) / line.Start.Depth + t / line.End.Depth) };
            var baseX = horizontal ? major : Math.Clamp((int)Math.Floor(point.X), 0, raster.Width - 1);
            var baseY = horizontal ? Math.Clamp((int)Math.Floor(point.Y), 0, raster.Height - 1) : major;
            for (var band = 0; band < strokeWidth; band++)
            {
                var x = baseX + (horizontal ? 0 : band); var y = baseY + (horizontal ? band : 0);
                if (x >= raster.Width || y >= raster.Height) continue;
                var index = y * raster.Width + x;
                var depth = Math.Clamp(point.Depth, raster.Camera.Near, raster.Camera.Far);
                // Evaluate the face at the actual pixel center. Sampling the line center
                // instead produces alternating grid bleed-through on a coplanar projection.
                if (planeDepth is { } equation) depth = equation.At(x, y);
                if (depth < raster.Camera.Near || depth > raster.Camera.Far) continue;
                // Surface/projection keep ties, including floating-point plane interpolation noise.
                var tolerance = line.Plane.HasValue ? 1e-9 * Math.Max(1, Math.Abs(depth)) : 0;
                var onDomain = raster.TriangleIndices[index] < -1 &&
                    depth <= raster.Depths[index] + 1e-9 * Math.Max(1, Math.Abs(depth));
                if (onDomain || depth + tolerance < raster.Depths[index])
                    pixels.Add(new(index, depth, line.Dimension, line.Kind, line.Plane?.WorldAxis ?? -1));
            }
        }
    }

    private static int StrokeWidth(SurfaceAxisReferenceLine line) =>
        line.Kind == SurfaceAxisStrokeKind.Grid && line.Dimension != SurfaceAxisDimension.Z ? 2 : 1;

    private static DepthEquation PlaneEquation(SurfaceAxisReferencePlane plane, SurfaceRasterSnapshot raster)
    {
        var camera = raster.Camera; var (right, up, forward) = camera.Basis();
        double Component(Point3DVector v) => plane.WorldAxis == 0 ? v.X : plane.WorldAxis == 1 ? v.Y : v.Z;
        var tx = 2 * camera.HalfHeight / raster.Height * Component(right);
        var ty = -2 * camera.HalfHeight / raster.Height * Component(up);
        var offset = camera.HalfHeight * ((1d - raster.Width) / raster.Height * Component(right)
            + (1 - 1d / raster.Height) * Component(up));
        var f = Component(forward);
        var distance = Component(camera.Target) + camera.Distance * f - plane.Coordinate;
        return raster.RenderingOptions.Perspective
            ? new(0, 0, distance, -tx / camera.Distance, -ty / camera.Distance, f - offset / camera.Distance)
            : new(tx, ty, distance + offset, 0, 0, f);
    }
    private readonly record struct DepthEquation(double NX, double NY, double N, double DX, double DY, double D)
    {
        internal double At(int x, int y)
        {
            var denominator = DX * x + DY * y + D;
            return Math.Abs(denominator) < 1e-12 ? double.PositiveInfinity : (NX * x + NY * y + N) / denominator;
        }
    }
    private readonly record struct VisiblePixel(int Index, double Depth, SurfaceAxisDimension? Dimension,
        SurfaceAxisStrokeKind Kind, int PlaneWorldAxis);
}
