using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class SurfacePerspectiveTests
{
    [Theory]
    [InlineData(4, .01, 16, 1.5)]
    [InlineData(.3, .08, .6, .3)] // Some original vertices are behind the eye.
    [InlineData(2, 1.5, 2.1, .9)] // Near and far planes both cut the surface.
    public void Perspective_pixels_match_independent_ray_plane_intersections(double distance, double near, double far, double halfHeight)
    {
        double[] x = [-1, -.2, 1], y = [-1, .6, 1];
        var source = new SurfaceChartSource("plane", x, y,
            x.SelectMany(a => y.Select(b => (double?)(.2 + .3 * a - .15 * b))), 42);
        var camera = new Point3DCamera(yaw: .45, pitch: .7, target: new(.12, -.08, .2),
            distance: distance, near: near, far: far, halfHeight: halfHeight);
        var frame = SurfaceRasterizer.Render(source, camera, 96, 80, -1, 1, false,
            SurfaceProjectionPlacement.Auto, new SurfaceRenderingOptions(Perspective: true));
        var sy = Math.Sin(camera.Yaw); var cy = Math.Cos(camera.Yaw);
        var sp = Math.Sin(camera.Pitch); var cp = Math.Cos(camera.Pitch);
        var right = new Point3DVector(cy, 0, -sy);
        var up = new Point3DVector(-sy * sp, cp, -cy * sp);
        var forward = new Point3DVector(sy * cp, sp, cy * cp);
        var eye = new Point3DVector(camera.Target.X + distance * forward.X,
            camera.Target.Y + distance * forward.Y, camera.Target.Z + distance * forward.Z);
        var compared = 0;
        for (var py = 0; py < frame.Height; py++)
            for (var px = 0; px < frame.Width; px++)
            {
                var sx = (2 * (px + .5) / frame.Width - 1) * halfHeight * frame.Width / frame.Height;
                var syScreen = (1 - 2 * (py + .5) / frame.Height) * halfHeight;
                var ray = new Point3DVector(sx * right.X + syScreen * up.X - distance * forward.X,
                    sx * right.Y + syScreen * up.Y - distance * forward.Y,
                    sx * right.Z + syScreen * up.Z - distance * forward.Z);
                var t = (.2 + .3 * eye.X - .15 * eye.Z - eye.Y) / (ray.Y - .3 * ray.X + .15 * ray.Z);
                var ix = eye.X + t * ray.X; var iy = eye.Z + t * ray.Z; var height = eye.Y + t * ray.Y;
                var depth = t * distance;
                var hit = frame.HitTest(px, py);
                if (t <= 0 || depth < near || depth > far || ix < -1 || ix > 1 || iy < -1 || iy > 1)
                { Assert.Null(hit); continue; }
                var actual = Assert.IsType<SurfaceRasterHit>(hit);
                Assert.Equal(ix, actual.InterpolatedX, 9);
                Assert.Equal(iy, actual.InterpolatedY, 9);
                Assert.Equal(height, actual.InterpolatedValue, 9);
                Assert.Equal(depth, actual.Depth, 9);
                Assert.Equal(42, actual.Version);
                Assert.Equal(source.Vertices[(int)actual.A.Key], actual.A);
                var color = SurfaceRasterizer.ColorAt(height, -1, 1);
                var offset = (py * frame.Width + px) * 4;
                Assert.InRange(Math.Abs(color.R - frame.Pixels[offset + 2]), 0, 1);
                Assert.InRange(Math.Abs(color.G - frame.Pixels[offset + 1]), 0, 1);
                Assert.InRange(Math.Abs(color.B - frame.Pixels[offset]), 0, 1);
                compared++;
            }
        Assert.True(compared > 10);
        var axes = SurfaceAxisReference.Build(frame);
        Assert.Same(frame, axes.Raster);
        foreach (var line in axes.Lines)
            foreach (var point in new[] { line.Start, line.End })
            {
                Assert.InRange(point.X, -1e-8, frame.Width + 1e-8);
                Assert.InRange(point.Y, -1e-8, frame.Height + 1e-8);
                Assert.InRange(point.Depth, near - 1e-8, far + 1e-8);
            }
    }

    [Fact]
    public void Perspective_foreground_is_larger_while_legacy_projection_keeps_parallel_scale()
    {
        var camera = new Point3DCamera(pitch: Math.PI / 2, distance: 2, halfHeight: 2);
        int Coverage(double height, bool perspective)
        {
            var source = new SurfaceChartSource("flat", [-1, 1], [-1, 1], Enumerable.Repeat<double?>(height, 4));
            return SurfaceRasterizer.Render(source, camera, 80, 80, -1, 1, false,
                SurfaceProjectionPlacement.Auto, new SurfaceRenderingOptions(Perspective: perspective)).CoveredPixelCount;
        }
        Assert.Equal(Coverage(-.5, false), Coverage(.5, false));
        Assert.True(Coverage(.5, true) > 2 * Coverage(-.5, true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Lighting_changes_only_color_and_preserves_both_faces_and_published_frames(int face)
    {
        var source = new SurfaceChartSource("slope", [-1, 0, 1], [-1, 1], [-.6, -.4, .3, .5, -.2, 0]);
        var camera = new Point3DCamera(yaw: .3, pitch: face * .8);
        var unlit = SurfaceRasterizer.Render(source, camera, 80, 64, -1, 1, true,
            SurfaceProjectionPlacement.Auto, new SurfaceRenderingOptions(Perspective: true));
        var oldPixels = unlit.Pixels.ToArray();
        var lit = SurfaceRasterizer.Render(source, camera, 80, 64, -1, 1, true,
            SurfaceProjectionPlacement.Auto, SurfaceRenderingOptions.LitPerspective);
        Assert.Equal(unlit.Depths.ToArray(), lit.Depths.ToArray());
        Assert.Equal(unlit.TriangleIndices.ToArray(), lit.TriangleIndices.ToArray());
        Assert.Equal(unlit.ProjectionPixels.ToArray(), lit.ProjectionPixels.ToArray());
        Assert.True(lit.CoveredPixelCount > 0);
        Assert.False(unlit.Pixels.SequenceEqual(lit.Pixels));
        Assert.Equal(oldPixels, unlit.Pixels.ToArray());
        for (var py = 0; py < lit.Height; py++)
            for (var px = 0; px < lit.Width; px++) Assert.Equal(unlit.HitTest(px, py), lit.HitTest(px, py));
    }

    [Fact]
    public void Perspective_projection_pixels_keep_plane_depth_but_have_no_raw_surface_hit()
    {
        var source = new SurfaceChartSource("projection", [-1, 1], [-1, 1], [-.2, .2, .1, .5]);
        var camera = new Point3DCamera(yaw: .4, pitch: .7);
        var frame = SurfaceRasterizer.Render(source, camera, 80, 64, -1, 1, true,
            SurfaceProjectionPlacement.Top, SurfaceRenderingOptions.LitPerspective);
        var projected = 0;
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
                if (frame.IsProjectionAt(x, y)) { projected++; Assert.Null(frame.HitTest(x, y)); }
        Assert.True(projected > 100);
        Assert.Equal(1, frame.ProjectionPlaneValue);
    }
}
