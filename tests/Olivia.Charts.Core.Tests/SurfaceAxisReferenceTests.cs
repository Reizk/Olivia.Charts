using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class SurfaceAxisReferenceTests
{
    [Theory]
    [InlineData(0, 0, 0, -6, 12, 100)] // X/height ties: left/top; forward eye: back.
    [InlineData(-.4, .3, -2, 14, -8, 160)] // Pan reverses all three farther faces.
    [InlineData(0, 0, -1, -6, 12, 100)] // Exact eye-at-center tie on all dimensions.
    public void FarFacesUseCameraEyeIncludingPanAndDocumentedTies(double panX, double panHeight,
        double panY, double farX, double farHeight, double farY)
    {
        var source = new SurfaceChartSource("asymmetric", [-6, 14], [100, 160], [0, 0, 0, 0]);
        var camera = new Point3DCamera(target: new(panX, panHeight, panY), distance: 1);
        var raster = SurfaceRasterizer.Render(source, camera, 0, 0, -8, 12);
        var reference = SurfaceAxisReference.Build(raster);
        Assert.Equal(new Point3DVector(farX, farHeight, farY), reference.FarCorner);
    }

    [Fact]
    public void AsymmetricRawRangesAndEndpointsUseTheExactCenteredCameraProjection()
    {
        var source = new SurfaceChartSource("asymmetric", [-6, 14], [100, 160], [0, 1, 2, 3], 71);
        var camera = new Point3DCamera(yaw: .37, pitch: .46, target: new(.15, -.1, .2), halfHeight: 1.8);
        var raster = SurfaceRasterizer.Render(source, camera, 80, 48, -8, 12);
        var reference = SurfaceAxisReference.Build(raster);
        Assert.Same(raster, reference.Raster);
        Assert.Equal(new Point3DVector(4, 2, 130), raster.WorldCenter);
        Assert.Equal(30, raster.WorldScale);
        Assert.Equal(new Point3DVector(-6, -8, 100), reference.FarCorner);
        Assert.Equal(9, reference.Lines.Length);
        Assert.Equal(3, reference.Lines.Count(line => line.Dimension.HasValue));
        Assert.Collection(reference.Axes,
            axis => CheckAxis(axis, SurfaceAxisDimension.X, -6, 14, new(-6, -8, 100), new(14, -8, 100)),
            axis => CheckAxis(axis, SurfaceAxisDimension.Y, 100, 160, new(-6, -8, 100), new(-6, -8, 160)),
            axis => CheckAxis(axis, SurfaceAxisDimension.Z, -8, 12, new(-6, -8, 100), new(-6, 12, 100)));
        foreach (var axis in reference.Axes)
            if (axis.LabelAnchor is { } anchor)
            {
                Assert.True(reference.ContainsPixel((int)anchor.X, (int)anchor.Y));
                Assert.True(double.IsPositiveInfinity(raster.Depths[(int)anchor.Y * raster.Width + (int)anchor.X]));
            }

        void CheckAxis(SurfaceAxisReferenceAxis axis, SurfaceAxisDimension dimension, double minimum,
            double maximum, Point3DVector minimumRaw, Point3DVector maximumRaw)
        {
            Assert.Equal(dimension, axis.Dimension); Assert.Equal(minimum, axis.Minimum); Assert.Equal(maximum, axis.Maximum);
            var expectedMinimum = Project(minimumRaw, new(4, 2, 130), 30, camera, 80, 48);
            var expectedMaximum = Project(maximumRaw, new(4, 2, 130), 30, camera, 80, 48);
            EqualPoint(expectedMinimum, axis.MinimumPoint!.Value); EqualPoint(expectedMaximum, axis.MaximumPoint!.Value);
            var line = Assert.Single(reference.Lines, line => line.Dimension == dimension);
            EqualPoint(expectedMinimum, line.Start); EqualPoint(expectedMaximum, line.End);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CrossingRangeSegmentsClipAgainstBothViewportSidesWithoutReplacingRawEndpoints(bool horizontal)
    {
        var source = horizontal
            ? new SurfaceChartSource("horizontal", [-2, 2], [-1, 1], [null, null, null, null])
            : new SurfaceChartSource("vertical", [-.2, .2], [-.2, .2], [null, null, null, null]);
        var camera = new Point3DCamera(halfHeight: .3);
        var raster = SurfaceRasterizer.Render(source, camera, 32, 32, horizontal ? 0 : -2, horizontal ? 0 : 2);
        var reference = SurfaceAxisReference.Build(raster);
        var dimension = horizontal ? SurfaceAxisDimension.X : SurfaceAxisDimension.Z;
        var axis = Assert.Single(reference.Axes, axis => axis.Dimension == dimension);
        var line = Assert.Single(reference.Lines, line => line.Dimension == dimension);
        if (horizontal)
        {
            Assert.True(axis.MinimumPoint!.Value.X < 0); Assert.True(axis.MaximumPoint!.Value.X > 32);
            Assert.Equal(0, line.Start.X, 9); Assert.Equal(32, line.End.X, 9);
            Assert.Equal(16, line.Start.Y, 9); Assert.Equal(16, line.End.Y, 9);
        }
        else
        {
            Assert.True(axis.MinimumPoint!.Value.Y > 32); Assert.True(axis.MaximumPoint!.Value.Y < 0);
            Assert.Equal(32, line.Start.Y, 9); Assert.Equal(0, line.End.Y, 9);
            Assert.Equal(32d / 3, line.Start.X, 9); Assert.Equal(32d / 3, line.End.X, 9);
        }
        AssertSpansAreSortedBoundedAndPickable(reference);
    }

    [Fact]
    public void NearAndFarClipAnEdgeEvenWithBothOriginalEndpointsOutsideAndEdgeOnProjectionUsesNearestDepth()
    {
        var source = new SurfaceChartSource("depth", [-1, 1], [-1, 1], [null, null, null, null]);
        var camera = new Point3DCamera(halfHeight: 2, distance: 1, near: .3, far: .7);
        var raster = SurfaceRasterizer.Render(source, camera, 32, 32, -1, 1);
        var reference = SurfaceAxisReference.Build(raster);
        var axis = Assert.Single(reference.Axes, axis => axis.Dimension == SurfaceAxisDimension.Y);
        Assert.Equal(2, axis.MinimumPoint!.Value.Depth); Assert.Equal(0, axis.MaximumPoint!.Value.Depth);
        Assert.Equal(3, reference.Lines.Length);
        Assert.All(reference.Lines, line =>
        {
            Assert.Equal(.7, line.Start.Depth, 10); Assert.Equal(.3, line.End.Depth, 10);
            Assert.Equal(line.Start.X, line.End.X); Assert.Equal(line.Start.Y, line.End.Y);
        });
        Assert.Equal(3, reference.Spans.Sum(span => span.EndX - span.StartX));
        Assert.Equal(.3, axis.LabelAnchor!.Value.Depth, 10);
    }

    [Theory]
    [InlineData(false, 1)] // Exact depth tie with the real surface.
    [InlineData(false, 2)] // Surface is strictly nearer than every reference edge.
    [InlineData(true, 0)] // Top projection wins the raster, then wins the reference depth tie.
    public void SameFrameDepthHidesLinesBehindSurfaceAndProjectionAndKeepsTies(bool projection, double value)
    {
        var camera = new Point3DCamera(pitch: Math.PI / 2);
        const int side = 42, column = 4, row = 21;
        // Independent coverage calculation: the box's left edge is (1 - 1/1.25)*42/2 = 4.2.
        // Its digital stroke is column 4, whose center 4.5 is also inside the real face.
        // A 40-pixel viewport instead rounds the edge just below 4 and strokes column 3,
        // outside the face; that fixture cannot exercise the intended overlapping depth test.
        var leftEdge = (1 - 1 / camera.HalfHeight) * side / 2;
        Assert.Equal(column, (int)Math.Floor(leftEdge));
        Assert.True(column + .5 > leftEdge);
        var empty = new SurfaceChartSource("empty", [-1, 1], [-1, 1], [null, null, null, null]);
        var background = SurfaceAxisReference.Build(SurfaceRasterizer.Render(empty, camera, side, side, -1, 1));
        var source = new SurfaceChartSource("covered", [-1, 1], [-1, 1], [value, value, value, value]);
        var raster = SurfaceRasterizer.Render(source, camera, side, side, -1, 1, projection, SurfaceProjectionPlacement.Top);
        var reference = SurfaceAxisReference.Build(raster);
        Assert.True(background.ContainsPixel(column, row));
        Assert.True(raster.TriangleIndices[row * side + column] >= 0);
        Assert.Equal(projection ? 3 : 4 - value, raster.Depths[row * side + column], 10);
        Assert.Equal(projection, raster.IsProjectionAt(column, row));
        Assert.False(reference.ContainsPixel(column, row));
        Assert.Equal(projection, raster.HitTest(column, row) is null);
        if (raster.HitTest(column, row) is { } hit) Assert.Equal(value, hit.InterpolatedValue, 10);
        Assert.True(background.Spans.Sum(span => span.EndX - span.StartX) > reference.Spans.Sum(span => span.EndX - span.StartX));
    }

    [Fact]
    public void BuildingAndPublishingNewFramesNeverMutatesOldSourceRasterOrReferenceOwnership()
    {
        var source = new SurfaceChartSource("retained", [-1, 1], [-1, 1], [0, .2, -.4, .7], 10);
        var camera = new Point3DCamera(yaw: .65, pitch: .55, halfHeight: 1.8);
        var raster = SurfaceRasterizer.Render(source, camera, 80, 64, -1, 1, true);
        var pixels = raster.Pixels.ToArray(); var depths = raster.Depths.ToArray();
        var indices = raster.TriangleIndices.ToArray(); var projection = raster.ProjectionPixels.ToArray();
        var vertices = source.Vertices.ToArray(); var triangles = raster.Triangles.ToArray();
        var reference = SurfaceAxisReference.Build(raster);
        var spans = reference.Spans.ToArray(); var axes = reference.Axes.ToArray(); var lines = reference.Lines.ToArray();
        var newerSource = new SurfaceChartSource("retained", [-1, 1], [-1, 1], [.7, 0, .4, -.3], 11);
        var newer = SurfaceAxisReference.Build(SurfaceRasterizer.Render(newerSource,
            new Point3DCamera(yaw: -.4, pitch: .8, target: new(.2, 0, 0)), 72, 56, -1, 1, true));
        Assert.NotSame(reference.Raster, newer.Raster); Assert.Same(raster, reference.Raster);
        Assert.Equal(10, reference.Raster.Source.Version); Assert.Equal(11, newer.Raster.Source.Version);
        Assert.Equal(pixels, raster.Pixels.ToArray()); Assert.Equal(depths, raster.Depths.ToArray());
        Assert.Equal(indices, raster.TriangleIndices.ToArray()); Assert.Equal(projection, raster.ProjectionPixels.ToArray());
        Assert.Equal(vertices, source.Vertices.ToArray()); Assert.Equal(triangles, raster.Triangles.ToArray());
        Assert.Equal(spans, reference.Spans.ToArray()); Assert.Equal(axes, reference.Axes.ToArray());
        Assert.Equal(lines, reference.Lines.ToArray());
        AssertSpansAreSortedBoundedAndPickable(reference);
    }

    [Theory]
    [InlineData(0, 2048)]
    [InlineData(2048, 0)]
    [InlineData(0, 0)]
    public void EmptyViewportRetainsOnlyRealRangesAndNoPickingCoverage(int width, int height)
    {
        var source = new SurfaceChartSource("empty-viewport", [10, 20], [-4, 7], [null, null, null, null]);
        var raster = SurfaceRasterizer.Render(source, Point3DCamera.Default, width, height, -8, 9);
        var reference = SurfaceAxisReference.Build(raster);
        Assert.Same(raster, reference.Raster); Assert.Empty(reference.Lines); Assert.Empty(reference.Spans);
        Assert.Equal(new double[] { 10, -4, -8 }, reference.Axes.Select(axis => axis.Minimum));
        Assert.Equal(new double[] { 20, 7, 9 }, reference.Axes.Select(axis => axis.Maximum));
        Assert.All(reference.Axes, axis =>
        {
            Assert.Null(axis.MinimumPoint); Assert.Null(axis.MaximumPoint); Assert.Null(axis.LabelAnchor);
        });
        Assert.False(reference.ContainsPixel(0, 0));
    }

    [Fact]
    public void MaximumAdmittedViewportHasNineEdgesAndOnlyPerimeterBoundedPixelStorage()
    {
        var source = new SurfaceChartSource("maximum", [-1, 1], [-1, 1], [null, null, null, null]);
        var raster = SurfaceRasterizer.Render(source, new Point3DCamera(yaw: .37, pitch: .5), 2048, 512, -1, 1);
        var reference = SurfaceAxisReference.Build(raster);
        Assert.InRange(reference.Lines.Length, 1, SurfaceAxisReference.MaximumLineCount);
        Assert.InRange(reference.Spans.Sum(span => span.EndX - span.StartX), 1, SurfaceAxisReference.MaximumVisiblePixels);
        AssertSpansAreSortedBoundedAndPickable(reference);
        Assert.Throws<ArgumentNullException>(() => SurfaceAxisReference.Build(null!));
    }

    private static void AssertSpansAreSortedBoundedAndPickable(SurfaceAxisReferenceSnapshot reference)
    {
        var previousEnd = -1;
        foreach (var span in reference.Spans)
        {
            Assert.InRange(span.Y, 0, reference.Raster.Height - 1);
            Assert.InRange(span.StartX, 0, reference.Raster.Width - 1);
            Assert.InRange(span.EndX, span.StartX + 1, reference.Raster.Width);
            var start = span.Y * reference.Raster.Width + span.StartX;
            Assert.True(start >= previousEnd);
            previousEnd = span.Y * reference.Raster.Width + span.EndX;
            Assert.True(reference.ContainsPixel(span.StartX, span.Y)); Assert.True(reference.ContainsPixel(span.EndX - 1, span.Y));
        }
        Assert.False(reference.ContainsPixel(-1, 0)); Assert.False(reference.ContainsPixel(0, -1));
        Assert.False(reference.ContainsPixel(reference.Raster.Width, 0)); Assert.False(reference.ContainsPixel(0, reference.Raster.Height));
        Assert.False(reference.ContainsPixel(int.MaxValue, int.MaxValue));
    }

    private static void EqualPoint(SurfaceAxisReferencePoint expected, SurfaceAxisReferencePoint actual)
    {
        Assert.Equal(expected.X, actual.X, 9); Assert.Equal(expected.Y, actual.Y, 9); Assert.Equal(expected.Depth, actual.Depth, 9);
    }

    // Independent explicit camera equations, not the production Basis or reference projection helper.
    private static SurfaceAxisReferencePoint Project(Point3DVector raw, Point3DVector center, double scale,
        Point3DCamera camera, int width, int height)
    {
        var x = (raw.X - center.X) / scale - camera.Target.X;
        var h = (raw.Y - center.Y) / scale - camera.Target.Y;
        var y = (raw.Z - center.Z) / scale - camera.Target.Z;
        var sy = Math.Sin(camera.Yaw); var cy = Math.Cos(camera.Yaw);
        var sp = Math.Sin(camera.Pitch); var cp = Math.Cos(camera.Pitch);
        return new((1 + (cy * x - sy * y) / (camera.HalfHeight * width / height)) * width / 2,
            (1 - (-sy * sp * x + cp * h - cy * sp * y) / camera.HalfHeight) * height / 2,
            camera.Distance - (sy * cp * x + sp * h + cy * cp * y));
    }
}
