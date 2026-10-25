using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class SurfaceAxisGraduationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Graduations_restore_real_numeric_coordinates_and_never_change_source_pixels(bool perspective)
    {
        var source = new SurfaceChartSource("offset", [100, 140, 200], [-45, 0, 35], Enumerable.Repeat<double?>(null, 9), 9);
        var camera = new Point3DCamera(yaw: .65, pitch: .4, halfHeight: 1.9);
        var frame = SurfaceRasterizer.Render(source, camera, 420, 360, -25, 50, false,
            SurfaceProjectionPlacement.Auto, new SurfaceRenderingOptions(Perspective: perspective));
        var pixels = frame.Pixels.ToArray(); var depths = frame.Depths.ToArray();
        var axes = SurfaceAxisReference.Build(frame, true);
        Assert.Same(frame, axes.Raster);
        Assert.Equal(3, axes.Ticks.Select(t => t.Dimension).Distinct().Count());
        Assert.InRange(axes.Lines.Length, 10, SurfaceAxisReference.MaximumDecoratedLineCount);
        Assert.Contains(axes.Spans, s => s.Kind == SurfaceAxisStrokeKind.Grid);
        foreach (var group in axes.Ticks.GroupBy(t => t.Dimension))
        {
            Assert.InRange(group.Count(), 1, SurfaceAxisReference.MaximumTicksPerAxis);
            Assert.Equal(group.Select(t => t.Value).Order(), group.Select(t => t.Value));
        }
        foreach (var tick in axes.Ticks)
        {
            // Invert the camera independently at the tick's exact depth. Tick values must
            // be original X/Y/height coordinates, not normalized units or array indices.
            var w = perspective ? tick.Anchor.Depth / camera.Distance : 1;
            var horizontal = (2 * tick.Anchor.X / frame.Width - 1) * camera.HalfHeight * frame.Width / frame.Height * w;
            var vertical = (1 - 2 * tick.Anchor.Y / frame.Height) * camera.HalfHeight * w;
            var forward = camera.Distance - tick.Anchor.Depth;
            var sy = Math.Sin(camera.Yaw); var cy = Math.Cos(camera.Yaw);
            var sp = Math.Sin(camera.Pitch); var cp = Math.Cos(camera.Pitch);
            var x = frame.WorldCenter.X + frame.WorldScale * (horizontal * cy - vertical * sy * sp + forward * sy * cp);
            var h = frame.WorldCenter.Y + frame.WorldScale * (vertical * cp + forward * sp);
            var y = frame.WorldCenter.Z + frame.WorldScale * (-horizontal * sy - vertical * cy * sp + forward * cy * cp);
            Assert.Equal(tick.Value, tick.Dimension == SurfaceAxisDimension.X ? x : tick.Dimension == SurfaceAxisDimension.Y ? y : h, 8);
            Assert.Equal(1, tick.OutwardX * tick.OutwardX + tick.OutwardY * tick.OutwardY, 10);
        }
        Assert.Equal(pixels, frame.Pixels.ToArray()); Assert.Equal(depths, frame.Depths.ToArray());
    }

    [Fact]
    public void Grid_strokes_obey_the_same_depth_as_surface_and_keep_bounded_sorted_storage()
    {
        var source = new SurfaceChartSource("covered", [-1, 1], [-1, 1], [2, 2, 2, 2]);
        var frame = SurfaceRasterizer.Render(source, new Point3DCamera(pitch: Math.PI / 2, halfHeight: 1.5), 160, 160, -1, 1);
        var axes = SurfaceAxisReference.Build(frame, true);
        Assert.Contains(axes.Lines, l => l.Kind == SurfaceAxisStrokeKind.Grid);
        var previous = -1;
        foreach (var span in axes.Spans)
        {
            var start = span.Y * frame.Width + span.StartX;
            Assert.True(start >= previous); previous = span.Y * frame.Width + span.EndX;
            for (var x = span.StartX; x < span.EndX; x++)
            {
                Assert.True(axes.ContainsPixel(x, span.Y));
                Assert.True(double.IsPositiveInfinity(frame.Depths[span.Y * frame.Width + x]));
            }
        }
        Assert.InRange(axes.Spans.Sum(s => s.EndX - s.StartX), 0,
            2 * SurfaceAxisReference.MaximumDecoratedLineCount * Math.Max(frame.Width, frame.Height));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Coplanar_projection_wins_grid_depth_at_every_covered_pixel(bool perspective)
    {
        var source = new SurfaceChartSource("flat", [-1, 1], [-1, 1], [0, 0, 0, 0]);
        var camera = new Point3DCamera(yaw: .65, pitch: .4, halfHeight: 1.8);
        var frame = SurfaceRasterizer.Render(source, camera, 320, 260, -1, 1, true,
            SurfaceProjectionPlacement.Bottom, new SurfaceRenderingOptions(Perspective: perspective));
        var axes = SurfaceAxisReference.Build(frame, true);
        Assert.Contains(frame.ProjectionPixels, p => p != 0);
        foreach (var span in axes.Spans.Where(s => s.Kind == SurfaceAxisStrokeKind.Grid))
            for (var x = span.StartX; x < span.EndX; x++) Assert.False(frame.IsProjectionAt(x, span.Y));
    }

    [Theory]
    [InlineData(-1e308, 1e308)]
    [InlineData(1e300, 1.000000000000001e300)]
    [InlineData(0, 1e-300)]
    public void Extreme_ranges_have_finite_bounded_graduations(double low, double high)
    {
        var source = new SurfaceChartSource("extreme", [low, high], [low, high], [null, null, null, null]);
        var frame = SurfaceRasterizer.Render(source, new Point3DCamera(yaw: .65, pitch: .5), 180, 180, low, high);
        var axes = SurfaceAxisReference.Build(frame, true);
        Assert.InRange(axes.Ticks.Length, 0, 3 * SurfaceAxisReference.MaximumTicksPerAxis);
        Assert.All(axes.Ticks, tick =>
        {
            Assert.InRange(tick.Value, low, high);
            Assert.InRange(tick.Anchor.X, 0, frame.Width); Assert.InRange(tick.Anchor.Y, 0, frame.Height);
        });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SurfaceAxisReference.Build(frame, true, cancellation.Token));
    }

    [Theory]
    [InlineData(600, 300, true)]
    [InlineData(300, 600, true)]
    [InlineData(600, 300, false)]
    [InlineData(300, 600, false)]
    public void Camera_fits_short_axis_and_default_scroll_moves_center_up_ten_percent(int width, int height, bool perspective)
    {
        var source = new SurfaceChartSource("fit", [0, 50], [0, 50], [0, 0, 0, 0]);
        var centered = SurfaceCameraLayout.Create(source, -18.5, 18.5, width, height, perspective, 0);
        var shifted = SurfaceCameraLayout.Create(source, -18.5, 18.5, width, height, perspective);
        var radius = Math.Sqrt(2 + .74 * .74);
        var shortExtent = centered.HalfHeight * Math.Min(1, (double)width / height);
        Assert.Equal(perspective ? radius / Math.Cos(Math.PI / 8) : radius, shortExtent, 10);
        if (perspective) Assert.Equal(45, 2 * Math.Atan(shortExtent / centered.Distance) * 180 / Math.PI, 8);
        var sy = Math.Sin(shifted.Yaw); var cy = Math.Cos(shifted.Yaw);
        var sp = Math.Sin(shifted.Pitch); var cp = Math.Cos(shifted.Pitch);
        var vertical = -sy * sp * -shifted.Target.X + cp * -shifted.Target.Y - cy * sp * -shifted.Target.Z;
        Assert.Equal(.4 * height, (1 - vertical / shifted.HalfHeight) * height / 2, 9);
        var frame = SurfaceRasterizer.Render(source, centered, width, height, -18.5, 18.5, false,
            SurfaceProjectionPlacement.Auto, new SurfaceRenderingOptions(Perspective: perspective));
        var axes = SurfaceAxisReference.Build(frame);
        Assert.Equal(9, axes.Lines.Length);
        foreach (var axis in axes.Axes)
            foreach (var point in new[] { axis.MinimumPoint!.Value, axis.MaximumPoint!.Value })
            { Assert.InRange(point.X, 0, width); Assert.InRange(point.Y, 0, height); }
    }
}
