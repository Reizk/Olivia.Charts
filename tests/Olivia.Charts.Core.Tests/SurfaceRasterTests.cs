using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class SurfaceRasterTests
{
    private static readonly Point3DCamera Top = new(pitch: Math.PI / 2);

    [Fact]
    public void RectangularMatrixProducesTwoOriginalTrianglesPerCellInXOuterOrder()
    {
        var source = new SurfaceChartSource("matrix", [-2, 0, 3], [-4, 2], [0, 1, 2, 3, 4, 5], 17);
        var frame = SurfaceRasterizer.Render(source, Top, 40, 40, 0, 5);
        Assert.Equal(4, frame.Triangles.Length);
        Assert.Equal(new long[] { 0, 2, 3 }, Keys(frame.Triangles[0]));
        Assert.Equal(new long[] { 0, 3, 1 }, Keys(frame.Triangles[1]));
        Assert.Equal(new long[] { 2, 4, 5 }, Keys(frame.Triangles[2]));
        Assert.Equal(new long[] { 2, 5, 3 }, Keys(frame.Triangles[3]));
        foreach (var triangle in frame.Triangles)
            foreach (var vertex in new[] { triangle.A, triangle.B, triangle.C })
            {
                Assert.Equal(vertex.XIndex * source.YCount + vertex.YIndex, vertex.Key);
                Assert.Equal(source.Vertices[(int)vertex.Key], vertex);
            }
        Assert.Equal(frame.CoveredPixelCount, frame.TriangleIndices.Count(index => index >= 0));
        Assert.True(frame.CoveredPixelCount > 0);
    }

    [Theory]
    [InlineData(51, 5000)]
    [InlineData(101, 20000)]
    public void DefaultAndMaximumGridKeepAllRealTriangles(int side, int expectedTriangles)
    {
        var arguments = Enumerable.Range(0, side).Select(index => (double)index).ToArray();
        var source = new SurfaceChartSource("full-grid", arguments, arguments,
            Enumerable.Repeat<double?>(0, side * side));
        var frame = SurfaceRasterizer.Render(source, Top, 0, 0, -1, 1);
        Assert.Equal(expectedTriangles, frame.Triangles.Length);
        Assert.Equal(side * side, frame.Source.Vertices.Length);
        Assert.Empty(frame.Pixels); Assert.Empty(frame.Depths); Assert.Empty(frame.TriangleIndices);
    }

    [Fact]
    public void PerPixelDepthDiffersFromAverageTrianglePainterOrder()
    {
        double[] axis = [-1, -1d / 3, 1d / 3, 1];
        double?[] heights = [-.92, .79, .89, -.68, 0, -.04, .95, -.91,
            -.88, .69, .14, -.08, .56, .19, .38, -.49];
        var source = new SurfaceChartSource("folds", axis, axis, heights, 9);
        var camera = new Point3DCamera(yaw: .96, pitch: -.51, halfHeight: 1.5);
        var frame = SurfaceRasterizer.Render(source, camera, 80, 80, -1, 1);
        var overlappingPixels = 0; var wrongAverageOrderPixels = 0;
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                // Independent analytic oracle over original triangles. Nothing in this fixture
                // crosses the frustum; normalization is exactly center=0, common scale=1.
                var candidates = new List<(int Index, double Depth, double MeanDepth)>();
                foreach (var triangle in frame.Triangles)
                {
                    var a = Project(triangle.A, camera, 80); var b = Project(triangle.B, camera, 80);
                    var c = Project(triangle.C, camera, 80);
                    var area = Edge(a, b, c.X, c.Y);
                    if (Math.Abs(area) < 1e-12) continue;
                    var wa = Edge(b, c, x + .5, y + .5) / area;
                    var wb = Edge(c, a, x + .5, y + .5) / area;
                    var wc = 1 - wa - wb;
                    if (Math.Min(wa, Math.Min(wb, wc)) <= 1e-8) continue;
                    candidates.Add((triangle.Index, wa * a.Z + wb * b.Z + wc * c.Z, (a.Z + b.Z + c.Z) / 3));
                }
                if (candidates.Count == 0) continue;
                var nearest = candidates.MinBy(value => value.Depth);
                Assert.Equal(nearest.Index, frame.TriangleIndices[y * frame.Width + x]);
                Assert.Equal(nearest.Depth, frame.Depths[y * frame.Width + x], 9);
                if (candidates.Count < 2) continue;
                overlappingPixels++;
                if (nearest.Index != candidates.MinBy(value => value.MeanDepth).Index) wrongAverageOrderPixels++;
            }
        Assert.True(overlappingPixels > 100);
        Assert.True(wrongAverageOrderPixels > 0, "Fixture must distinguish real depth from average-depth sorting.");
    }

    [Fact]
    public void NearAndFarCrossingsSurviveEvenWhenEveryOriginalVertexIsOutsideDepthInterval()
    {
        var source = new SurfaceChartSource("depth-clipped", [-1, 1], [-1, 1], [-1, 1, -1, 1]);
        var camera = new Point3DCamera(pitch: Math.PI / 2, distance: 1, near: .25, far: 1.25);
        var frame = SurfaceRasterizer.Render(source, camera, 64, 64, -1, 1);
        Assert.All(source.Vertices, vertex => Assert.True(1 - vertex.Z!.Value < camera.Near || 1 - vertex.Z.Value > camera.Far));
        Assert.True(frame.CoveredPixelCount > 0);
        Assert.True(frame.CoveredPixelCount < 64 * 64);
        Assert.Equal(2, frame.Triangles.Length); // Clipping vertices do not replace originals.
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                if (frame.HitTest(x, y) is { } hit)
                {
                    Assert.InRange(hit.Depth, camera.Near, camera.Far);
                    Assert.InRange(hit.InterpolatedValue, -.250000001, .750000001);
                    Assert.Contains(hit.A, source.Vertices); Assert.Contains(hit.B, source.Vertices); Assert.Contains(hit.C, source.Vertices);
                }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void ViewportCrossingTrianglesCoverPixelsFromBothFaces(int face)
    {
        var source = Flat(0);
        var camera = new Point3DCamera(pitch: face * Math.PI / 2, halfHeight: .3);
        var frame = SurfaceRasterizer.Render(source, camera, 32, 32, -1, 1);
        // All four source corners project beyond the viewport. Whole-triangle rejection fails this.
        Assert.Equal(32 * 32, frame.CoveredPixelCount);
        Assert.All(frame.TriangleIndices, index => Assert.InRange(index, 0, 1));
        Assert.All(frame.Depths, depth => Assert.Equal(4, depth, 10));
        Assert.Equal((byte)255, frame.Pixels[(16 * 32 + 16) * 4 + 3]);
    }

    [Fact]
    public void GapVerticesOmitOnlyIncidentTrianglesAndNeverBecomeZeroHeights()
    {
        var one = new SurfaceChartSource("gap", [-1, 1], [-1, 1], [0, null, 0, 0]);
        var partial = SurfaceRasterizer.Render(one, Top, 64, 64, -1, 1);
        var triangle = Assert.Single(partial.Triangles);
        Assert.Equal(new long[] { 0, 2, 3 }, Keys(triangle));
        Assert.True(partial.CoveredPixelCount > 0);
        Assert.True(partial.CoveredPixelCount < SurfaceRasterizer.Render(Flat(0), Top, 64, 64, -1, 1).CoveredPixelCount);
        var diagonalGap = new SurfaceChartSource("gap", [-1, 1], [-1, 1], [double.NaN, 0, 0, 0]);
        var empty = SurfaceRasterizer.Render(diagonalGap, Top, 32, 32, -1, 1);
        Assert.Empty(empty.Triangles); Assert.Equal(0, empty.CoveredPixelCount);
        Assert.All(empty.Pixels, value => Assert.Equal((byte)0, value));
        Assert.All(empty.Depths, value => Assert.True(double.IsPositiveInfinity(value)));
        Assert.All(empty.TriangleIndices, value => Assert.Equal(-1, value));
        Assert.Null(empty.HitTest(16, 16));
    }

    [Theory]
    [InlineData(-1, 64)]
    [InlineData(64, -1)]
    [InlineData(2049, 0)]
    [InlineData(0, 2049)]
    [InlineData(2048, 513)]
    [InlineData(1025, 1024)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void OverBudgetViewportRejectsInsteadOfDownsampling(int width, int height)
    {
        var source = Flat(0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ArgumentOutOfRangeException>(() => SurfaceRasterizer.Render(source, Top, width, height));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 262144, "Rejection must happen before allocating raster buffers.");
    }

    [Theory]
    [InlineData(0, 2048)]
    [InlineData(2048, 0)]
    [InlineData(0, 0)]
    public void ZeroDimensionReturnsEmptyWithoutInventingResolution(int width, int height)
    {
        var frame = SurfaceRasterizer.Render(Flat(0), Top, width, height, -1, 1);
        Assert.Equal(width, frame.Width); Assert.Equal(height, frame.Height);
        Assert.Empty(frame.Pixels); Assert.Empty(frame.Depths); Assert.Empty(frame.TriangleIndices);
        Assert.Equal(0, frame.CoveredPixelCount); Assert.Null(frame.HitTest(0, 0));
    }

    [Fact]
    public void FixedBoundsKeepCenteredUniformNormalizationAndColorRangeAcrossFrames()
    {
        var first = SurfaceRasterizer.Render(Flat(0, 10), Top, 32, 32, -1, 1);
        var bytes = first.Pixels.ToArray();
        var second = SurfaceRasterizer.Render(Flat(.4, 11), Top, 32, 32, -1, 1);
        Assert.Equal(new Point3DVector(0, 0, 0), first.WorldCenter);
        Assert.Equal(1, first.WorldScale);
        Assert.Equal(first.WorldCenter, second.WorldCenter); Assert.Equal(first.WorldScale, second.WorldScale);
        Assert.Equal(-1, second.MinimumValue); Assert.Equal(1, second.MaximumValue);
        Assert.Equal(bytes, first.Pixels.ToArray());
        Assert.Equal(PixelColor(first, 16, 16), SurfaceRasterizer.ColorAt(0, -1, 1));
        Assert.Equal(PixelColor(second, 16, 16), SurfaceRasterizer.ColorAt(.4, -1, 1));
        Assert.NotEqual(PixelColor(first, 16, 16), PixelColor(second, 16, 16));
        Assert.Equal(10, first.HitTest(16, 16)!.Value.Version);
        Assert.Equal(11, second.HitTest(16, 16)!.Value.Version);
        var translated = new SurfaceChartSource("center", [100, 150], [200, 300], [0, 0, 0, 0]);
        var centered = SurfaceRasterizer.Render(translated, Top, 32, 32, -10, 10);
        Assert.Equal(new Point3DVector(125, 0, 250), centered.WorldCenter);
        Assert.Equal(50, centered.WorldScale); // One common scale, not independent axis stretching.
    }

    [Fact]
    public void SameFrameHitKeepsRawVerticesAndLabelsInterpolatedHeightExplicitly()
    {
        var source = new SurfaceChartSource("plane", [-1, 1], [-1, 1], [-2, 0, 0, 2], 42);
        var frame = SurfaceRasterizer.Render(source, Top, 64, 64, -2, 2);
        var hit = frame.HitTest(35, 34)!.Value;
        Assert.Equal("plane", hit.SourceId); Assert.Equal(42, hit.Version);
        Assert.Equal(frame.Depths[34 * 64 + 35], hit.Depth);
        Assert.Equal(frame.TriangleIndices[34 * 64 + 35], hit.TriangleIndex);
        Assert.Contains(hit.A, source.Vertices); Assert.Contains(hit.B, source.Vertices); Assert.Contains(hit.C, source.Vertices);
        Assert.Equal(hit.InterpolatedX + hit.InterpolatedY, hit.InterpolatedValue, 9);
        Assert.DoesNotContain(source.Vertices, vertex => Math.Abs(vertex.Z!.Value - hit.InterpolatedValue) < 1e-9);
        Assert.Null(frame.HitTest(-1, 0)); Assert.Null(frame.HitTest(64, 0)); Assert.Null(frame.HitTest(0, 64));
        Assert.Null(frame.HitTest(0, 0));
    }

    [Fact]
    public void FixedBoundsAreExplicitAndColorsClampOutsideThem()
    {
        Assert.Throws<ArgumentException>(() => SurfaceRasterizer.Render(Flat(0), Top, 8, 8, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SurfaceRasterizer.Render(Flat(0), Top, 8, 8, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SurfaceRasterizer.Render(Flat(0), Top, 8, 8, double.NaN, 1));
        Assert.Equal(SurfaceRasterizer.ColorAt(-1, -1, 1), SurfaceRasterizer.ColorAt(-99, -1, 1));
        Assert.Equal(SurfaceRasterizer.ColorAt(1, -1, 1), SurfaceRasterizer.ColorAt(99, -1, 1));
        Assert.Equal(SurfaceRasterizer.ColorAt(0, -1, 1), SurfaceRasterizer.ColorAt(7, 7, 7));
    }

    private static SurfaceChartSource Flat(double value, long version = 0) =>
        new("flat", [-1, 1], [-1, 1], [value, value, value, value], version);
    private static long[] Keys(SurfaceTriangle triangle) => [triangle.A.Key, triangle.B.Key, triangle.C.Key];
    private static (byte B, byte G, byte R, byte A) PixelColor(SurfaceRasterSnapshot frame, int x, int y)
    {
        var index = (y * frame.Width + x) * 4;
        return (frame.Pixels[index], frame.Pixels[index + 1], frame.Pixels[index + 2], frame.Pixels[index + 3]);
    }
    private static Point3DVector Project(SurfaceVertex vertex, Point3DCamera camera, int side)
    {
        var sy = Math.Sin(camera.Yaw); var cy = Math.Cos(camera.Yaw);
        var sp = Math.Sin(camera.Pitch); var cp = Math.Cos(camera.Pitch);
        var height = vertex.Z!.Value;
        return new((1 + (cy * vertex.X - sy * vertex.Y) / camera.HalfHeight) * side / 2,
            (1 - (-sy * sp * vertex.X + cp * height - cy * sp * vertex.Y) / camera.HalfHeight) * side / 2,
            camera.Distance - (sy * cp * vertex.X + sp * height + cy * cp * vertex.Y));
    }
    private static double Edge(Point3DVector a, Point3DVector b, double x, double y) =>
        (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
}
