using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class SurfaceReferenceCalibrationTests
{
    [Theory]
    [InlineData(SurfaceProjectionPlacement.Top, 22.2)]
    [InlineData(SurfaceProjectionPlacement.Bottom, -22.2)]
    [InlineData(SurfaceProjectionPlacement.Auto, -22.2)]
    public void Padded_domain_changes_geometry_not_color_bounds_or_raw_vertices(SurfaceProjectionPlacement placement, double plane)
    {
        var source = new SurfaceChartSource("flat", [0, 50], [0, 50], [0, 0, 0, 0]);
        var options = SurfaceRenderingOptions.LitPerspective with { HeightSideMarginFraction = .1 };
        var camera = SurfaceCameraLayout.Create(source, -22.2, 22.2, 420, 360);
        var frame = SurfaceRasterizer.Render(source, camera, 420, 360, -18.5, 18.5, true, placement, options);
        Assert.Equal(-18.5, frame.MinimumValue); Assert.Equal(18.5, frame.MaximumValue);
        Assert.Equal(-22.2, frame.MinimumHeight, 10); Assert.Equal(22.2, frame.MaximumHeight, 10);
        Assert.Equal(plane, frame.ProjectionPlaneValue!.Value, 10);
        Assert.All(frame.Triangles, t => { Assert.Equal(0, t.A.Z); Assert.Equal(0, t.B.Z); Assert.Equal(0, t.C.Z); });
        var axes = SurfaceAxisReference.Build(frame, true);
        var height = Assert.Single(axes.Axes.Where(a => a.Dimension == SurfaceAxisDimension.Z));
        Assert.Equal(-22.2, height.Minimum, 10); Assert.Equal(22.2, height.Maximum, 10);
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                if (frame.IsProjectionAt(x, y)) Assert.Null(frame.HitTest(x, y));
                else if (frame.HitTest(x, y) is { } hit)
                    Assert.Equal(0, hit.InterpolatedValue, 9);
            }
    }

    [Theory]
    [InlineData(420, 360, .4)]
    [InlineData(700, 600, .9)]
    public void Official_default_ticks_keep_five_unit_step_across_viewports_and_rotation(int width, int height, double yaw)
    {
        var source = new SurfaceChartSource("empty", [0, 50], [0, 50], [null, null, null, null]);
        var camera = SurfaceCameraLayout.Create(source, -22.2, 22.2, width, height);
        camera = new(yaw, camera.Pitch, camera.Target, camera.HalfHeight, camera.Distance);
        var frame = SurfaceRasterizer.Render(source, camera, width, height, -18.5, 18.5, false,
            SurfaceProjectionPlacement.Auto, SurfaceRenderingOptions.LitPerspective with { HeightSideMarginFraction = .1 });
        var axes = SurfaceAxisReference.Build(frame, true);
        foreach (var dimension in new[] { SurfaceAxisDimension.X, SurfaceAxisDimension.Y })
            Assert.Equal(Enumerable.Range(0, 11).Select(i => i * 5d), axes.Ticks.Where(t => t.Dimension == dimension).Select(t => t.Value));
        Assert.Equal(Enumerable.Range(-4, 9).Select(i => i * 5d), axes.Ticks.Where(t => t.Dimension == SurfaceAxisDimension.Z).Select(t => t.Value));
    }

    [Fact]
    public void Margin_validation_precedes_render_allocation_and_legacy_range_stays_unchanged()
    {
        Assert.Equal((-double.MaxValue, double.MaxValue), SurfaceRenderingOptions.Legacy.HeightRange(-double.MaxValue, double.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SurfaceRenderingOptions { HeightSideMarginFraction = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SurfaceRenderingOptions { HeightSideMarginFraction = -.1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => (SurfaceRenderingOptions.Legacy with { HeightSideMarginFraction = .1 }).HeightRange(-double.MaxValue, double.MaxValue));
    }

    [Theory]
    [InlineData(true, .4)]
    [InlineData(true, -.8)]
    [InlineData(false, .4)]
    public void Grid_face_light_matches_actual_domain_pixels_without_specular_or_identity_changes(bool lit, double yaw)
    {
        var white = new SurfaceColor(255, 255, 255);
        var source = new SurfaceChartSource("empty", [0, 50], [0, 50], [null, null, null, null]);
        var camera = new Point3DCamera(yaw: yaw, pitch: .35, halfHeight: 1.9);
        var frame = SurfaceRasterizer.Render(source, camera, 420, 360, -18.5, 18.5, false,
            SurfaceProjectionPlacement.Auto, new SurfaceRenderingOptions(true, lit) { HeightSideMarginFraction = .1, Material = new(white, white, white, white) });
        var axes = SurfaceAxisReference.Build(frame, true);
        var planes = axes.Spans.Where(s => s.Plane is not null).Select(s => s.Plane!.Value).Distinct().ToArray();
        Assert.Equal(3, planes.Length);
        foreach (var plane in planes)
        {
            var pixel = Enumerable.Range(0, frame.TriangleIndices.Length).First(i => frame.TriangleIndices[i] == -2 - plane.WorldAxis);
            var shaded = axes.ShadeDomainColor(white, plane);
            Assert.InRange(Math.Abs(shaded.R - frame.Pixels[pixel * 4 + 2]), 0, 1);
            Assert.InRange(Math.Abs(shaded.G - frame.Pixels[pixel * 4 + 1]), 0, 1);
            Assert.InRange(Math.Abs(shaded.B - frame.Pixels[pixel * 4]), 0, 1);
            var grid = axes.ShadeDomainColor(new(208, 208, 208), plane);
            Assert.True(grid.R < shaded.R); Assert.True(grid.G < shaded.G); Assert.True(grid.B < shaded.B);
            Assert.Null(frame.HitTest(pixel % frame.Width, pixel / frame.Width));
        }
    }
}
