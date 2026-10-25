using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class SurfaceMaterialTests
{
    private static readonly SurfaceMaterial Paint = new(new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(120, 140, 160));

    [Fact]
    public void Three_sorted_stops_use_zero_not_midpoint_and_keep_legacy_transfer_available()
    {
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), Color(-10));
        Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)255), Color(0));
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), Color(20));
        Assert.Equal(((byte)0, (byte)128, (byte)128, (byte)255), Color(-5));
        Assert.Equal(((byte)128, (byte)128, (byte)0, (byte)255), Color(10));
        Assert.Equal(Color(-10), Color(-100)); Assert.Equal(Color(20), Color(100));
        Assert.Equal(Color(-5), SurfaceRasterizer.ColorAt(-5e307, -1e308, 1e308, Paint));
        Assert.Equal(SurfaceRasterizer.ColorAt(5, -10, 20), SurfaceRasterizer.ColorAt(5, -10, 20, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => SurfaceRasterizer.ColorAt(double.NaN, -1, 1, Paint));
        Assert.Throws<ArgumentOutOfRangeException>(() => SurfaceRasterizer.ColorAt(0, 2, 1, Paint));
        (byte, byte, byte, byte) Color(double value) => SurfaceRasterizer.ColorAt(value, -10, 20, Paint);
    }

    [Fact]
    public void Stops_outside_the_domain_are_sorted_and_exact_duplicates_collapse()
    {
        // Sorted reference positions are 0, 10, 20, even if the visible domain is 10..20.
        Assert.Equal(Paint.Second.Bgra, SurfaceRasterizer.ColorAt(10, 10, 20, Paint));
        Assert.Equal(Paint.Third.Bgra, SurfaceRasterizer.ColorAt(20, 10, 20, Paint));
        Assert.Equal(Paint.First.Bgra, SurfaceRasterizer.ColorAt(-20, -20, -10, Paint));
        Assert.Equal(Paint.Second.Bgra, SurfaceRasterizer.ColorAt(-10, -20, -10, Paint));
        Assert.Equal(Paint.Second.Bgra, SurfaceRasterizer.ColorAt(10, 0, 10, Paint));
        Assert.Equal(Paint.First.Bgra, SurfaceRasterizer.ColorAt(0, 0, 0, Paint));
    }

    [Theory]
    [InlineData(false, .65, .4)]
    [InlineData(true, .65, .4)]
    [InlineData(true, -.65, -.4)]
    [InlineData(true, 2.3, .4)]
    public void Far_planes_have_geometric_depth_and_no_source_identity(bool perspective, double yaw, double pitch)
    {
        var source = new SurfaceChartSource("empty", [-1, 1], [-1, 1], [null, null, null, null]);
        var camera = new Point3DCamera(yaw: yaw, pitch: pitch, halfHeight: 1.9);
        var frame = SurfaceRasterizer.Render(source, camera, 200, 180, -1, 1, false,
            SurfaceProjectionPlacement.Auto, new SurfaceRenderingOptions(perspective) { Material = Paint });
        Assert.Equal(0, frame.CoveredPixelCount);
        Assert.Empty(frame.Triangles);
        Assert.Equal(new[] { -4, -3, -2 }, frame.TriangleIndices.Where(i => i < -1).Distinct().Order());
        var (right, up, forward) = camera.Basis();
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                var pixel = y * frame.Width + x;
                if (!frame.IsDomainAt(x, y)) continue;
                Assert.Null(frame.HitTest(x, y)); Assert.False(frame.IsProjectionAt(x, y));
                Assert.Equal((byte)255, frame.Pixels[pixel * 4 + 3]);
                Assert.InRange(frame.Depths[pixel], camera.Near, camera.Far);
                var w = perspective ? frame.Depths[pixel] / camera.Distance : 1;
                var h = (2 * ((x + .5) / frame.Width) - 1) * camera.HalfHeight * frame.Width / frame.Height * w;
                var v = (1 - 2 * ((y + .5) / frame.Height)) * camera.HalfHeight * w;
                var d = camera.Distance - frame.Depths[pixel];
                var world = new Point3DVector(camera.Target.X + right.X * h + up.X * v + forward.X * d,
                    camera.Target.Y + right.Y * h + up.Y * v + forward.Y * d,
                    camera.Target.Z + right.Z * h + up.Z * v + forward.Z * d);
                var axis = -2 - frame.TriangleIndices[pixel];
                var component = axis == 0 ? world.X : axis == 1 ? world.Y : world.Z;
                var eyeComponent = axis == 0 ? forward.X : axis == 1 ? forward.Y : forward.Z;
                Assert.Equal(eyeComponent < 0 ? 1 : -1, component, 8);
            }
        var axes = SurfaceAxisReference.Build(frame, true);
        Assert.Contains(axes.Spans, s => s.Kind == SurfaceAxisStrokeKind.Grid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Domain_material_does_not_cover_surface_or_coplanar_projection_and_old_frames_are_immutable(bool perspective)
    {
        var source = new SurfaceChartSource("flat", [-1, 1], [-1, 1], [0, 0, 0, 0]);
        var camera = new Point3DCamera(yaw: .65, pitch: .4, halfHeight: 1.8);
        var options = new SurfaceRenderingOptions(perspective) { Material = Paint with { Domain = null } };
        var before = SurfaceRasterizer.Render(source, camera, 200, 180, -1, 1, true,
            SurfaceProjectionPlacement.Bottom, options);
        var pixels = before.Pixels.ToArray();
        var withDomain = SurfaceRasterizer.Render(source, camera, 200, 180, -1, 1, true,
            SurfaceProjectionPlacement.Bottom, options with { Material = Paint });
        Assert.Contains(withDomain.TriangleIndices, index => index < -1);
        Assert.Equal(before.CoveredPixelCount, withDomain.CoveredPixelCount);
        for (var i = 0; i < before.Depths.Length; i++)
        {
            if (before.TriangleIndices[i] < 0) continue;
            Assert.Equal(before.Depths[i], withDomain.Depths[i]);
            Assert.Equal(before.TriangleIndices[i], withDomain.TriangleIndices[i]);
            Assert.Equal(before.ProjectionPixels[i], withDomain.ProjectionPixels[i]);
            for (var channel = 0; channel < 4; channel++) Assert.Equal(before.Pixels[i * 4 + channel], withDomain.Pixels[i * 4 + channel]);
        }
        var axes = SurfaceAxisReference.Build(withDomain, true);
        foreach (var span in axes.Spans.Where(s => s.Kind == SurfaceAxisStrokeKind.Grid))
            for (var x = span.StartX; x < span.EndX; x++) Assert.False(withDomain.IsProjectionAt(x, span.Y));
        Assert.Equal(pixels, before.Pixels.ToArray());
        Assert.Null(before.RenderingOptions.Material!.Domain);
    }

    [Fact]
    public void Invisible_planes_leave_no_depth_and_lighting_changes_only_color()
    {
        var source = new SurfaceChartSource("gaps", [-1, 1], [-1, 1], [null, null, null, null]);
        var camera = new Point3DCamera(yaw: .65, pitch: .4, halfHeight: 1.8);
        SurfaceRasterSnapshot Render(SurfaceMaterial material, bool lighting = false) =>
            SurfaceRasterizer.Render(source, camera, 100, 90, -1, 1, false, SurfaceProjectionPlacement.Auto,
                new SurfaceRenderingOptions(true, lighting) { Material = material });
        var invisible = Render(Paint with { Domain = new(120, 140, 160, 0) });
        Assert.All(invisible.Depths, depth => Assert.True(double.IsPositiveInfinity(depth)));
        var plain = Render(Paint); var lit = Render(Paint, true);
        Assert.Equal(plain.Depths.ToArray(), lit.Depths.ToArray());
        Assert.Equal(plain.TriangleIndices.ToArray(), lit.TriangleIndices.ToArray());
        Assert.False(plain.Pixels.SequenceEqual(lit.Pixels));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SurfaceRasterizer.Render(source, camera, 100, 90, -1, 1,
            false, SurfaceProjectionPlacement.Auto, new SurfaceRenderingOptions { Material = Paint }, cancellation.Token));
    }
}
