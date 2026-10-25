using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Olivia.Core.Charts;

/// <summary>XOY projection location. Auto chooses the box plane farther from the camera eye.
/// These choices follow the reference's placement contract, not native camera/material parity.</summary>
public enum SurfaceProjectionPlacement { Auto, Top, Bottom }

/// <summary>A real matrix triangle. A/B/C are the unmodified source vertices, never clipping vertices.
/// Raster-created triangles retain one immutable vertex buffer and compact indices instead of
/// duplicating three vertices. Holding such a triangle keeps that vertex buffer alive.</summary>
public readonly record struct SurfaceTriangle
{
    private readonly ImmutableArray<SurfaceVertex> _sourceVertices;
    private readonly ExplicitVertices? _explicit;
    private readonly int _a, _b, _c;
    private sealed record ExplicitVertices(SurfaceVertex A, SurfaceVertex B, SurfaceVertex C);

    // Keep the original constructor, named arguments, init/with, deconstruction and value equality.
    public SurfaceTriangle(int Index, SurfaceVertex A, SurfaceVertex B, SurfaceVertex C) : this()
    { this.Index = Index; _explicit = new(A, B, C); }

    internal SurfaceTriangle(int index, ImmutableArray<SurfaceVertex> vertices, int a, int b, int c) : this()
    { Index = index; _sourceVertices = vertices; _a = a; _b = b; _c = c; }

    public int Index { get; init; }
    public SurfaceVertex A
    {
        get => !_sourceVertices.IsDefault ? _sourceVertices[_a] : _explicit?.A ?? default;
        init { _explicit = new(value, B, C); _sourceVertices = default; }
    }
    public SurfaceVertex B
    {
        get => !_sourceVertices.IsDefault ? _sourceVertices[_b] : _explicit?.B ?? default;
        init { _explicit = new(A, value, C); _sourceVertices = default; }
    }
    public SurfaceVertex C
    {
        get => !_sourceVertices.IsDefault ? _sourceVertices[_c] : _explicit?.C ?? default;
        init { _explicit = new(A, B, value); _sourceVertices = default; }
    }
    public void Deconstruct(out int index, out SurfaceVertex a, out SurfaceVertex b, out SurfaceVertex c)
    { index = Index; a = A; b = B; c = C; }
    public bool Equals(SurfaceTriangle other) => Index == other.Index && A == other.A && B == other.B && C == other.C;
    public override int GetHashCode() => HashCode.Combine(Index, A, B, C);
}

/// <summary>Same-frame depth-buffer hit. The three vertices are originals; the explicitly named
/// interpolated fields describe the pixel center and are NOT another original matrix sample.</summary>
public readonly record struct SurfaceRasterHit(string SourceId, long Version, int TriangleIndex,
    SurfaceVertex A, SurfaceVertex B, SurfaceVertex C,
    double InterpolatedX, double InterpolatedY, double InterpolatedValue, double Depth);

/// <summary>Immutable, managed CPU frame. Pixels are row-major unpremultiplied BGRA32, with
/// material alpha on coverage and transparent elsewhere. Only the nearest layer is retained.
/// Optional domain planes carry negative identity -2/-3/-4 (world X/height/Y);
/// -1 is uncovered. Depths and TriangleIndices describe this exact source/version/frame.
/// No native resource is retained; releasing the frame releases its managed buffers for collection.</summary>
public sealed class SurfaceRasterSnapshot
{
    internal SurfaceRasterSnapshot(SurfaceChartSource source, Point3DCamera camera, int width, int height,
        double minimumValue, double maximumValue, Point3DVector worldCenter, double worldScale,
        ImmutableArray<SurfaceTriangle> triangles, byte[] pixels, double[] depths, int[] indices, byte[] projectionPixels, int covered,
        bool showProjection, SurfaceProjectionPlacement projectionPlacement, double? projectionPlaneValue,
        SurfaceRenderingOptions renderingOptions, int edgeSampledPixelCount)
    {
        Source = source; Camera = camera; Width = width; Height = height;
        MinimumValue = minimumValue; MaximumValue = maximumValue;
        (MinimumHeight, MaximumHeight) = renderingOptions.HeightRange(minimumValue, maximumValue);
        WorldCenter = worldCenter; WorldScale = worldScale; Triangles = triangles;
        // Ownership is transferred: the renderer never retains or mutates these arrays afterward.
        Pixels = ImmutableCollectionsMarshal.AsImmutableArray(pixels);
        Depths = ImmutableCollectionsMarshal.AsImmutableArray(depths);
        TriangleIndices = ImmutableCollectionsMarshal.AsImmutableArray(indices);
        CoveredPixelCount = covered;
        ProjectionPixels = ImmutableCollectionsMarshal.AsImmutableArray(projectionPixels);
        ShowProjection = showProjection; ProjectionPlacement = projectionPlacement;
        ProjectionPlaneValue = projectionPlaneValue;
        RenderingOptions = renderingOptions;
        EdgeSampledPixelCount = edgeSampledPixelCount;
    }

    public SurfaceChartSource Source { get; }
    public Point3DCamera Camera { get; }
    public SurfaceRenderingOptions RenderingOptions { get; }
    public int Width { get; }
    public int Height { get; }
    public double MinimumValue { get; }
    public double MaximumValue { get; }
    /// <summary>Domain/projection limits, independent of the original color stops.</summary>
    public double MinimumHeight { get; }
    public double MaximumHeight { get; }
    /// <summary>Raw center in world=(argument X, height, argument Y) order.</summary>
    public Point3DVector WorldCenter { get; }
    /// <summary>One shared half-extent for all three axes. Fixed height bounds keep animation stable.</summary>
    public double WorldScale { get; }
    public ImmutableArray<SurfaceTriangle> Triangles { get; }
    public ImmutableArray<byte> Pixels { get; }
    public ImmutableArray<double> Depths { get; }
    public ImmutableArray<int> TriangleIndices { get; }
    public int CoveredPixelCount { get; }
    /// <summary>Pixels resolved from four private edge samples. Does not change center coverage or picking.</summary>
    public int EdgeSampledPixelCount { get; }
    /// <summary>True only for an optional domain plane, never an original source triangle.</summary>
    public bool IsDomainAt(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height &&
        TriangleIndices[y * Width + x] < -1;
    public bool ShowProjection { get; }
    public SurfaceProjectionPlacement ProjectionPlacement { get; }
    public double? ProjectionPlaneValue { get; }
    /// <summary>One byte per pixel when enabled: 1 only where the plane won the same depth test.
    /// Empty when disabled. TriangleIndices still refer to unchanged original mesh triangles.</summary>
    public ImmutableArray<byte> ProjectionPixels { get; }
    public bool IsProjectionAt(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height &&
        !ProjectionPixels.IsEmpty && ProjectionPixels[y * Width + x] != 0;

    /// <summary>Reads the visible triangle at the integer pixel's center, not a nearest-vertex guess.</summary>
    public SurfaceRasterHit? HitTest(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return null;
        var pixel = y * Width + x;
        var triangleIndex = TriangleIndices[pixel];
        // The reference's projected mesh has no refined-point hit metadata. Do not invent
        // a source height from the plane or pick through it to an occluded surface.
        if (triangleIndex < 0 || IsProjectionAt(x, y)) return null;
        var triangle = Triangles[triangleIndex];
        var (right, up, forward) = Camera.Basis();
        var w = RenderingOptions.W(Depths[pixel], Camera);
        var horizontal = (2 * ((x + .5) / Width) - 1) * Camera.HalfHeight * ((double)Width / Height) * w;
        var vertical = (1 - 2 * ((y + .5) / Height)) * Camera.HalfHeight * w;
        var forwardDistance = Camera.Distance - Depths[pixel];
        // Inverse projection of the SAME winning depth. This is equivalent to
        // barycentric interpolation on the original triangle, including clipped triangles.
        var nx = Camera.Target.X + right.X * horizontal + up.X * vertical + forward.X * forwardDistance;
        var ny = Camera.Target.Y + right.Y * horizontal + up.Y * vertical + forward.Y * forwardDistance;
        var nz = Camera.Target.Z + right.Z * horizontal + up.Z * vertical + forward.Z * forwardDistance;
        var argumentX = Math.FusedMultiplyAdd(nx, WorldScale, WorldCenter.X);
        var argumentY = Math.FusedMultiplyAdd(nz, WorldScale, WorldCenter.Z);
        var value = Math.FusedMultiplyAdd(ny, WorldScale, WorldCenter.Y);
        if (!double.IsFinite(argumentX) || !double.IsFinite(argumentY) || !double.IsFinite(value))
            throw new InvalidOperationException("The interpolated raw hit cannot be represented as finite coordinates.");
        return new(Source.Id, Source.Version, triangleIndex, triangle.A, triangle.B, triangle.C,
            argumentX, argumentY, value, Depths[pixel]);
    }
}

/// <summary>Bounded CPU surface renderer, not a GPU/native shading or camera-equivalence
/// implementation. Uses two real triangles per grid cell, six-plane polygon clipping, no backface
/// culling, and per-pixel interpolated depth. It never sorts triangles by average depth or downsamples.</summary>
public static class SurfaceRasterizer
{
    public const int MaximumDimension = 2048;
    public const int MaximumPixels = 1_048_576;

    /// <summary>Supply BOTH fixed bounds for stable animation normalization and color. When omitted,
    /// finite heights determine this one frame's range. Null/nonfinite heights omit incident triangles.</summary>
    public static SurfaceRasterSnapshot Render(SurfaceChartSource source, Point3DCamera camera,
        int width, int height, double? minimumValue = null, double? maximumValue = null) =>
        Render(source, camera, width, height, minimumValue, maximumValue, false);

    /// <summary>Optionally flatten the same mesh to one value-bound plane, retaining raw-value
    /// colors and shared depth. The original six-parameter overload remains binary compatible.</summary>
    public static SurfaceRasterSnapshot Render(SurfaceChartSource source, Point3DCamera camera,
        int width, int height, double? minimumValue, double? maximumValue,
        bool showProjection, SurfaceProjectionPlacement projectionPlacement = SurfaceProjectionPlacement.Auto)
        => Render(source, camera, width, height, minimumValue, maximumValue, showProjection, projectionPlacement, CancellationToken.None);

    /// <summary>Cancellable background rendering. Only private projection/edge scratch storage is pooled;
    /// every published snapshot retains exclusive ownership of its pixel, depth and identity arrays.</summary>
    public static SurfaceRasterSnapshot Render(SurfaceChartSource source, Point3DCamera camera,
        int width, int height, double? minimumValue, double? maximumValue,
        bool showProjection, SurfaceProjectionPlacement projectionPlacement, CancellationToken cancellationToken)
        => Render(source, camera, width, height, minimumValue, maximumValue, showProjection, projectionPlacement,
            SurfaceRenderingOptions.Legacy, cancellationToken);

    public static SurfaceRasterSnapshot Render(SurfaceChartSource source, Point3DCamera camera,
        int width, int height, double? minimumValue, double? maximumValue,
        bool showProjection, SurfaceProjectionPlacement projectionPlacement, SurfaceRenderingOptions renderingOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(renderingOptions);
        cancellationToken.ThrowIfCancellationRequested();
        // Admission checks deliberately precede mesh, projection, or pixel-buffer allocation.
        if (!Enum.IsDefined(projectionPlacement)) throw new ArgumentOutOfRangeException(nameof(projectionPlacement));
        if (width < 0 || width > MaximumDimension) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0 || height > MaximumDimension) throw new ArgumentOutOfRangeException(nameof(height));
        if ((long)width * height > MaximumPixels)
            throw new ArgumentOutOfRangeException(nameof(width), "Surface viewport exceeds the explicit pixel budget.");
        if (minimumValue.HasValue != maximumValue.HasValue)
            throw new ArgumentException("Supply both fixed value bounds, or neither.", nameof(minimumValue));
        var minimum = minimumValue ?? double.PositiveInfinity;
        var maximum = maximumValue ?? double.NegativeInfinity;
        if (!minimumValue.HasValue)
        {
            foreach (var vertex in source.Vertices)
                if (!vertex.IsGap)
                {
                    minimum = Math.Min(minimum, vertex.Z!.Value);
                    maximum = Math.Max(maximum, vertex.Z!.Value);
                }
            if (double.IsPositiveInfinity(minimum)) minimum = maximum = 0;
        }
        ValidateRange(minimum, maximum);
        var (minimumHeight, maximumHeight) = renderingOptions.HeightRange(minimum, maximum);
        var center = new Point3DVector(Midpoint(source.XArguments[0], source.XArguments[^1]),
            Midpoint(minimumHeight, maximumHeight), Midpoint(source.YArguments[0], source.YArguments[^1]));
        var scale = Math.Max(HalfExtent(source.XArguments[0], source.XArguments[^1]),
            Math.Max(HalfExtent(minimumHeight, maximumHeight), HalfExtent(source.YArguments[0], source.YArguments[^1])));
        if (scale == 0) scale = 1;
        // The native Auto contract selects the farther box face by camera-eye distance.
        // In this centered orthographic coordinate system, the sign of eye.Y gives the
        // identical face comparison; equality chooses Top. Pan is part of the eye position.
        double? planeValue = showProjection
            ? projectionPlacement == SurfaceProjectionPlacement.Top ||
              projectionPlacement == SurfaceProjectionPlacement.Auto &&
              camera.Target.Y + camera.Distance * Math.Sin(camera.Pitch) <= 0 ? maximumHeight : minimumHeight
            : null;
        var triangles = BuildTriangles(source, cancellationToken);
        var count = width * height;
        var pixels = new byte[count * 4];
        var depths = new double[count]; Array.Fill(depths, double.PositiveInfinity);
        var indices = new int[count]; Array.Fill(indices, -1);
        var projectionPixels = showProjection ? new byte[count] : Array.Empty<byte>();
        SurfaceEdgeAntialiaser? edgeSamples = null;
        if (count == 0) return Frame(0);

        var (right, up, forward) = camera.Basis();
        var projected = ArrayPool<RasterVertex>.Shared.Rent(source.Vertices.Length * (showProjection ? 2 : 1));
        Point3DVector[]? normals = null;
        try
        {
            if (renderingOptions.Lighting)
            {
                normals = ArrayPool<Point3DVector>.Shared.Rent(source.Vertices.Length);
                Array.Clear(normals, 0, source.Vertices.Length);
                foreach (var triangle in triangles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var a = Position(triangle.A); var b = Position(triangle.B); var c = Position(triangle.C);
                    // WPF's generated vertex normals average unit face normals, not face areas.
                    var normal = SurfaceLighting.Unit(SurfaceLighting.Cross(SurfaceLighting.Subtract(b, a), SurfaceLighting.Subtract(c, a)));
                    normals[(int)triangle.A.Key] = SurfaceLighting.Add(normals[(int)triangle.A.Key], normal);
                    normals[(int)triangle.B.Key] = SurfaceLighting.Add(normals[(int)triangle.B.Key], normal);
                    normals[(int)triangle.C.Key] = SurfaceLighting.Add(normals[(int)triangle.C.Key], normal);
                }
            }
            var halfWidth = camera.HalfHeight * ((double)width / height);
            // Same projected-vertex workspace, topology, frustum, color transfer and depth buffer
            // for both passes. Only the projection geometry flattens; its color keeps raw height.
            // Reuse clipping stack storage outside both loops, never per-triangle stackalloc.
            Span<RasterVertex> first = stackalloc RasterVertex[12];
            Span<RasterVertex> second = stackalloc RasterVertex[12];
            Span<RasterVertex> corners = stackalloc RasterVertex[8];
            var covered = 0;
            for (var coveragePass = 0; coveragePass < (renderingOptions.EdgeAntialiasing ? 2 : 1); coveragePass++)
            {
                for (var pass = 0; pass < (showProjection ? 2 : 1); pass++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var isProjection = pass == 1;
                    var vertexOffset = pass * source.Vertices.Length;
                    // Coverage rerasterization reuses both immutable projected/lighted layers.
                    if (coveragePass == 0)
                    {
                        foreach (var vertex in source.Vertices)
                        {
                            if (vertex.IsGap) continue;
                            var position = Position(vertex);
                            if (isProjection) position = position with { Y = Normalize(planeValue!.Value, center.Y, scale) };
                            var relative = SurfaceLighting.Subtract(position, camera.Target);
                            var light = renderingOptions.Lighting
                                ? SurfaceLighting.Evaluate(isProjection ? new(0, -1, 0) : normals![(int)vertex.Key], relative,
                                    camera, renderingOptions.Perspective, right, up, forward, reverseNormal: false)
                                : (Diffuse: 1d, Specular: 0d);
                            var backLight = renderingOptions.Lighting
                                ? SurfaceLighting.Evaluate(isProjection ? new(0, -1, 0) : normals![(int)vertex.Key], relative,
                                    camera, renderingOptions.Perspective, right, up, forward, reverseNormal: true)
                                : (Diffuse: 1d, Specular: 0d);
                            var point = new RasterVertex(Point3DChartGeometry.Dot(relative, right) / halfWidth,
                                Point3DChartGeometry.Dot(relative, up) / camera.HalfHeight,
                                camera.Distance - Point3DChartGeometry.Dot(relative, forward), vertex.Z!.Value,
                                light.Diffuse, light.Specular, backLight.Diffuse, backLight.Specular);
                            // WPF Gouraud colors are affine in screen space. Carry color * W
                            // through homogeneous clipping, then divide before raster interpolation.
                            if (renderingOptions.Perspective) point = PremultiplyLight(point);
                            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Depth))
                                throw new ArgumentException("A finite source coordinate cannot be represented under these fixed bounds.", nameof(source));
                            projected[vertexOffset + (int)vertex.Key] = point;
                        }
                    }
                    foreach (var triangle in triangles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        first[0] = projected[vertexOffset + (int)triangle.A.Key];
                        first[1] = projected[vertexOffset + (int)triangle.B.Key];
                        first[2] = projected[vertexOffset + (int)triangle.C.Key];
                        var vertexCount = 3;
                        for (var plane = 0; plane < 6 && vertexCount > 0; plane++)
                        {
                            vertexCount = Clip(first[..vertexCount], second, plane, camera, renderingOptions);
                            var temporary = first; first = second; second = temporary;
                        }
                        for (var index = 1; index + 1 < vertexCount; index++)
                            Rasterize(first[0], first[index], first[index + 1], triangle.Index,
                                width, height, minimum, maximum, camera, pixels, depths, indices,
                                projectionPixels, isProjection, ref covered, cancellationToken, renderingOptions, edgeSamples: edgeSamples);
                    }
                }
                if (renderingOptions.Material?.Domain is { A: > 0 } domain)
                {
                    // Three far box faces are real clipped geometry with the same depth buffer.
                    // Draw after data; data/projection retain coplanar ties and their identities.
                    var eye = SurfaceLighting.Add(camera.Target, new(camera.Distance * forward.X,
                        camera.Distance * forward.Y, camera.Distance * forward.Z));
                    var far = (eye.X < 0 ? 1 : 0) | (eye.Y <= 0 ? 2 : 0) | (eye.Z < 0 ? 4 : 0);
                    for (var axis = 0; axis < 3; axis++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var bit = 1 << axis;
                        var normal = axis == 0 ? new Point3DVector(1, 0, 0)
                            : axis == 1 ? new Point3DVector(0, 1, 0) : new Point3DVector(0, 0, 1);
                        for (var i = 0; i < 8; i++)
                        {
                            var p = new Point3DVector(
                                Normalize((i & 1) == 0 ? source.XArguments[0] : source.XArguments[^1], center.X, scale),
                                Normalize((i & 2) == 0 ? minimumHeight : maximumHeight, center.Y, scale),
                                Normalize((i & 4) == 0 ? source.YArguments[0] : source.YArguments[^1], center.Z, scale));
                            var relative = SurfaceLighting.Subtract(p, camera.Target);
                            var diffuse = renderingOptions.Lighting ? SurfaceLighting.Evaluate(normal, relative,
                                camera, renderingOptions.Perspective, right, up, forward).Diffuse : 1;
                            corners[i] = new(Point3DChartGeometry.Dot(relative, right) / halfWidth,
                                Point3DChartGeometry.Dot(relative, up) / camera.HalfHeight,
                                camera.Distance - Point3DChartGeometry.Dot(relative, forward), 0, diffuse, 0);
                            if (renderingOptions.Perspective) corners[i] = PremultiplyLight(corners[i]);
                        }
                        var a = far & bit;
                        var u = axis == 0 ? 2 : 1;
                        var v = axis == 2 ? 2 : 4;
                        for (var triangle = 0; triangle < 2; triangle++)
                        {
                            first[0] = corners[a]; first[1] = corners[a | (triangle == 0 ? u : u | v)];
                            first[2] = corners[a | (triangle == 0 ? u | v : v)];
                            var vertexCount = 3;
                            for (var plane = 0; plane < 6 && vertexCount > 0; plane++)
                            {
                                vertexCount = Clip(first[..vertexCount], second, plane, camera, renderingOptions);
                                var temporary = first; first = second; second = temporary;
                            }
                            for (var index = 1; index + 1 < vertexCount; index++)
                                Rasterize(first[0], first[index], first[index + 1], -2 - axis,
                                    width, height, minimum, maximum, camera, pixels, depths, indices,
                                    projectionPixels, false, ref covered, cancellationToken, renderingOptions, domain, edgeSamples);
                        }
                    }
                }
                if (coveragePass == 0 && renderingOptions.EdgeAntialiasing)
                {
                    edgeSamples = SurfaceEdgeAntialiaser.Create(width, height, source.YCount, triangles,
                        indices, projectionPixels, pixels, cancellationToken);
                    if (edgeSamples is null) break;
                }
                else edgeSamples?.Resolve(pixels, cancellationToken);
            }
            return Frame(covered);
        }
        finally
        {
            ArrayPool<RasterVertex>.Shared.Return(projected);
            if (normals is not null) ArrayPool<Point3DVector>.Shared.Return(normals);
            edgeSamples?.Dispose();
        }

        Point3DVector Position(SurfaceVertex vertex) => new(Normalize(vertex.X, center.X, scale),
            Normalize(vertex.Z!.Value, center.Y, scale), Normalize(vertex.Y, center.Z, scale));

        SurfaceRasterSnapshot Frame(int coverage) => new(source, camera, width, height, minimum, maximum,
            center, scale, triangles, pixels, depths, indices, projectionPixels, coverage,
            showProjection, projectionPlacement, planeValue, renderingOptions, edgeSamples?.PixelCount ?? 0);
    }

    /// <summary>The same fixed-domain color transfer used for every covered pixel and UI legend.</summary>
    public static (byte B, byte G, byte R, byte A) ColorAt(double value, double minimum, double maximum)
        => ColorAt(value, minimum, maximum, null);

    public static (byte B, byte G, byte R, byte A) ColorAt(double value, double minimum, double maximum,
        SurfaceMaterial? material)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        ValidateRange(minimum, maximum);
        return material?.ColorAt(value, minimum, maximum) ?? ColorUnchecked(value, minimum, maximum);
    }

    private static ImmutableArray<SurfaceTriangle> BuildTriangles(SurfaceChartSource source, CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<SurfaceTriangle>(2 * (source.XCount - 1) * (source.YCount - 1));
        for (var ix = 0; ix + 1 < source.XCount; ix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var iy = 0; iy + 1 < source.YCount; iy++)
            {
                var a = source.VertexAt(ix, iy); var b = source.VertexAt(ix + 1, iy);
                var c = source.VertexAt(ix + 1, iy + 1); var d = source.VertexAt(ix, iy + 1);
                if (!a.IsGap && !b.IsGap && !c.IsGap)
                    result.Add(new(result.Count, source.Vertices, (int)a.Key, (int)b.Key, (int)c.Key));
                if (!a.IsGap && !c.IsGap && !d.IsGap)
                    result.Add(new(result.Count, source.Vertices, (int)a.Key, (int)c.Key, (int)d.Key));
            }
        }
        // Full finite grids exactly fill the admitted topology. Transfer that buffer instead
        // of copying the compact topology again. Gaps leave spare capacity, so retain
        // the compact-copy path there; no array is ever returned to a pool or reused later.
        return result.Count == result.Capacity ? result.MoveToImmutable() : result.ToImmutable();
    }

    private static int Clip(ReadOnlySpan<RasterVertex> input, Span<RasterVertex> output,
        int plane, Point3DCamera camera, SurfaceRenderingOptions options)
    {
        var count = 0; var previous = input[^1]; var previousDistance = Distance(previous, plane, camera, options);
        foreach (var current in input)
        {
            var currentDistance = Distance(current, plane, camera, options);
            var previousInside = previousDistance >= 0; var currentInside = currentDistance >= 0;
            // Strict crossings only: an endpoint exactly on the plane is emitted by the
            // ordinary inside branch, never duplicated as an extra intersection vertex.
            if ((previousDistance < 0 && currentDistance > 0) || (previousDistance > 0 && currentDistance < 0))
            {
                // Opposite signs; scaled magnitudes avoid overflow in the denominator.
                var magnitude = Math.Max(Math.Abs(previousDistance), Math.Abs(currentDistance));
                var p = Math.Abs(previousDistance) / magnitude; var c = Math.Abs(currentDistance) / magnitude;
                output[count++] = Lerp(previous, current, p / (p + c));
            }
            if (currentInside) output[count++] = current;
            previous = current; previousDistance = currentDistance;
        }
        return count;
    }

    private static double Distance(RasterVertex point, int plane, Point3DCamera camera, SurfaceRenderingOptions options) => plane switch
    {
        0 => point.X + options.W(point.Depth, camera), 1 => options.W(point.Depth, camera) - point.X,
        2 => point.Y + options.W(point.Depth, camera), 3 => options.W(point.Depth, camera) - point.Y,
        4 => point.Depth - camera.Near, _ => camera.Far - point.Depth
    };

    private static RasterVertex Lerp(RasterVertex a, RasterVertex b, double t) => new(
        a.X * (1 - t) + b.X * t, a.Y * (1 - t) + b.Y * t,
        a.Depth * (1 - t) + b.Depth * t, Interpolate(a.Value, b.Value, 0, 1 - t, t, 0),
        a.Diffuse * (1 - t) + b.Diffuse * t, a.Specular * (1 - t) + b.Specular * t,
        a.BackDiffuse * (1 - t) + b.BackDiffuse * t, a.BackSpecular * (1 - t) + b.BackSpecular * t);

    private static RasterVertex PremultiplyLight(RasterVertex p) => p with
    {
        Diffuse = p.Diffuse * p.Depth, Specular = p.Specular * p.Depth,
        BackDiffuse = p.BackDiffuse * p.Depth, BackSpecular = p.BackSpecular * p.Depth
    };

    private static void Rasterize(RasterVertex a, RasterVertex b, RasterVertex c, int triangleIndex,
        int width, int height, double minimum, double maximum, Point3DCamera camera,
        byte[] pixels, double[] depths, int[] indices, byte[] projectionPixels, bool isProjection, ref int covered,
        CancellationToken cancellationToken, SurfaceRenderingOptions options, SurfaceColor? fixedColor = null,
        SurfaceEdgeAntialiaser? edgeSamples = null)
    {
        // Clip in homogeneous coordinates BEFORE dividing by W. Eye/near-plane crossings
        // must not create infinities or discard triangles whose interiors are visible.
        a = Screen(a); b = Screen(b); c = Screen(c);
        RasterVertex Screen(RasterVertex p)
        {
            var w = options.W(p.Depth, camera);
            if (options.Perspective) p = p with
            {
                Diffuse = p.Diffuse / p.Depth, Specular = p.Specular / p.Depth,
                BackDiffuse = p.BackDiffuse / p.Depth, BackSpecular = p.BackSpecular / p.Depth
            };
            return p with { X = (p.X / w + 1) * width / 2, Y = (1 - p.Y / w) * height / 2 };
        }
        var area = Edge(a, b, c.X, c.Y);
        if (area == 0 || !double.IsFinite(area)) return; // Edge-on triangles have zero pixel area.
        // Screen Y is reversed: positive area is the CW/back material pass. All vertices
        // of that primitive use the same side, even where a smoothed normal faces away.
        if (area > 0 && !fixedColor.HasValue)
        {
            a = a with { Diffuse = a.BackDiffuse, Specular = a.BackSpecular };
            b = b with { Diffuse = b.BackDiffuse, Specular = b.BackSpecular };
            c = c with { Diffuse = c.BackDiffuse, Specular = c.BackSpecular };
        }
        var lowOffset = edgeSamples is null ? .5 : .75;
        var highOffset = edgeSamples is null ? .5 : .25;
        var minX = Math.Max(0, (int)Math.Ceiling(Math.Min(a.X, Math.Min(b.X, c.X)) - lowOffset));
        var maxX = Math.Min(width - 1, (int)Math.Floor(Math.Max(a.X, Math.Max(b.X, c.X)) - highOffset));
        var minY = Math.Max(0, (int)Math.Ceiling(Math.Min(a.Y, Math.Min(b.Y, c.Y)) - lowOffset));
        var maxY = Math.Min(height - 1, (int)Math.Floor(Math.Max(a.Y, Math.Max(b.Y, c.Y)) - highOffset));
        for (var y = minY; y <= maxY; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = minX; x <= maxX; x++)
            {
                var pixel = y * width + x;
                var sampleBase = edgeSamples?.SampleBase(pixel) ?? -1;
                if (edgeSamples is not null && sampleBase < 0) continue;
                for (var sample = 0; sample < (edgeSamples is null ? 1 : 4); sample++)
                {
                    var sx = x + (edgeSamples is null ? .5 : .25 + .5 * (sample & 1));
                    var sy = y + (edgeSamples is null ? .5 : .25 + .5 * (sample >> 1));
                    var wa = Edge(b, c, sx, sy) / area;
                    var wb = Edge(c, a, sx, sy) / area;
                    var wc = 1 - wa - wb;
                    if (wa < -1e-12 || wb < -1e-12 || wc < -1e-12) continue;
                    // Shared-edge tolerance cannot introduce negative color weights.
                    wa = Math.Max(0, wa); wb = Math.Max(0, wb); wc = Math.Max(0, wc);
                    var weightSum = wa + wb + wc; wa /= weightSum; wb /= weightSum; wc /= weightSum;
                    var diffuse = wa * a.Diffuse + wb * b.Diffuse + wc * c.Diffuse;
                    var specular = wa * a.Specular + wb * b.Specular + wc * c.Specular;
                    if (options.Perspective)
                    {
                        wa /= a.Depth; wb /= b.Depth; wc /= c.Depth;
                        weightSum = wa + wb + wc; wa /= weightSum; wb /= weightSum; wc /= weightSum;
                    }
                    var depth = Math.Clamp(wa * a.Depth + wb * b.Depth + wc * c.Depth, camera.Near, camera.Far);
                    var tolerance = fixedColor.HasValue ? 1e-9 * Math.Max(1, Math.Abs(depth)) : 0;
                    if (depth + tolerance >= (edgeSamples is null ? depths[pixel] : edgeSamples.Depths[sampleBase + sample])) continue;
                    if (edgeSamples is null)
                    {
                        if (!fixedColor.HasValue && indices[pixel] < 0) covered++;
                        if (fixedColor.HasValue && indices[pixel] >= 0) covered--;
                        depths[pixel] = depth; indices[pixel] = triangleIndex;
                        if (projectionPixels.Length != 0) projectionPixels[pixel] = isProjection ? (byte)1 : (byte)0;
                    }
                    else edgeSamples.Depths[sampleBase + sample] = depth;
                    var value = Interpolate(a.Value, b.Value, c.Value, wa, wb, wc);
                    var color = fixedColor?.Bgra ?? options.Material?.ColorAt(value, minimum, maximum)
                        ?? ColorUnchecked(value, minimum, maximum);
                    if (options.Lighting)
                        color = SurfaceLighting.Shade(color, diffuse, specular);
                    if (edgeSamples is not null)
                        edgeSamples.Colors[sampleBase + sample] = (uint)(color.B | color.G << 8 | color.R << 16 | color.A << 24);
                    else
                    {
                        var offset = pixel * 4;
                        pixels[offset] = color.B; pixels[offset + 1] = color.G;
                        pixels[offset + 2] = color.R; pixels[offset + 3] = color.A;
                    }
                }
            }
        }
    }

    private static double Edge(RasterVertex a, RasterVertex b, double x, double y) =>
        (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);

    private static double Interpolate(double a, double b, double c, double wa, double wb, double wc)
    {
        var value = wa * a + wb * b + wc * c;
        if (double.IsFinite(value)) return value;
        // A convex combination of finite source heights must remain finite even when an
        // intermediate sum overflows. Scaling preserves that invariant without altering bounds.
        var scale = Math.Max(Math.Abs(a), Math.Max(Math.Abs(b), Math.Abs(c)));
        return Math.Clamp(wa * (a / scale) + wb * (b / scale) + wc * (c / scale), -1, 1) * scale;
    }

    private static (byte B, byte G, byte R, byte A) ColorUnchecked(double value, double minimum, double maximum)
    {
        var range = maximum - minimum;
        var t = minimum == maximum ? .5 : double.IsFinite(range)
            ? Math.Clamp((value - minimum) / range, 0, 1)
            : Math.Clamp((value / 2 - minimum / 2) / (maximum / 2 - minimum / 2), 0, 1);
        static byte Channel(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return (Channel(1.5 - Math.Abs(4 * t - 1)), Channel(1.5 - Math.Abs(4 * t - 2)),
            Channel(1.5 - Math.Abs(4 * t - 3)), 255);
    }

    private static void ValidateRange(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum)) throw new ArgumentOutOfRangeException(nameof(minimum));
        if (!double.IsFinite(maximum) || maximum < minimum) throw new ArgumentOutOfRangeException(nameof(maximum));
    }
    private static double Midpoint(double minimum, double maximum) => minimum / 2 + maximum / 2;
    private static double HalfExtent(double minimum, double maximum) => maximum / 2 - minimum / 2;
    private static double Normalize(double value, double center, double scale)
    {
        var difference = value - center;
        return double.IsFinite(difference) ? difference / scale : value / scale - center / scale;
    }
    private readonly record struct RasterVertex(double X, double Y, double Depth, double Value,
        double Diffuse, double Specular, double BackDiffuse = 1, double BackSpecular = 0);
}
